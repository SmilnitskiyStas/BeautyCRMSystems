using System.Security.Cryptography;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyCommon;
using BeautyCrm.Application.Features.BeautyOverview;
using BeautyCrm.Application.Features.BeautyProductImages;
using BeautyCrm.Application.Features.BeautyStaff;
using Microsoft.Extensions.Logging;

namespace BeautyCrm.Application.Features.BeautyLocations;

public sealed record LogoWriteResult(bool Found, LocationDto? Location = null, string? OldKey = null);

/// <summary>Порт даних логотипу закладу (під RLS tenant). Запис - під FOR UPDATE рядка закладу.</summary>
public interface ILocationLogoStore
{
    /// <summary>Ставить (або знімає, якщо <paramref name="newKey"/> = null) ключ логотипу; повертає попередній ключ для видалення файлів після commit.</summary>
    Task<LogoWriteResult> SetLogoAsync(Guid locationId, string? newKey, CancellationToken ct);
}

/// <summary>
/// Логотип закладу (контракт .ai/artifacts/location-logo). Той самий конвеєр і сховище, що й фото товарів (ADR-027): права -> медіа ввімкнено ->
/// заклад існує -> файл з тіла -> обробка -> файли -> транзакція (рядок ніколи не раніше файлів). Збій БД - новий файл видаляється;
/// попередній файл - best effort лише після commit. Логи без ключів сховища й імен файлів клієнта.
/// </summary>
public sealed class LocationLogoService(ILocationLogoStore store, ILocationStore locations, IProductImageStorage storage, IImageProcessor processor,
    MediaOptions options, ILogger<LocationLogoService> log)
{
    private static readonly Error Forbidden = Error.Forbidden("forbidden_role", "Only owner or admin can manage locations.");
    private static readonly Error Disabled = Error.NotFound("media_disabled", "Images are not available.");
    private static readonly Error NotFound = Error.NotFound("location_not_found", "Location not found.");

    public bool IsEnabled => options.Enabled;

    public async Task<Result<LocationDto>> UploadAsync(Actor actor, Guid locationId, Func<CancellationToken, Task<Result<Stream>>> readFile, CancellationToken ct)
    {
        if (!StaffRoles.CanEditLocations(actor)) return Forbidden;
        if (!options.Enabled) return Disabled;
        if (await locations.GetAsync(locationId, ct) is null) return NotFound;

        var file = await readFile(ct);
        if (!file.IsOk) return Reject(locationId, file.Error!);
        await using var input = file.Value!;

        var processed = await processor.ProcessAsync(input, ct);
        if (!processed.IsOk) return Reject(locationId, processed.Error!);
        var image = processed.Value!;

        string? key = null;
        for (var attempt = 0; attempt < 3 && key is null; attempt++)
        {
            var candidate = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            if (await storage.TryWriteAsync(actor.TenantId, candidate, image.Variants, ct)) key = candidate;
        }
        if (key is null) throw new InvalidOperationException("Could not allocate a unique storage key.");

        var committed = false;
        try
        {
            var written = await store.SetLogoAsync(locationId, key, ct);
            if (!written.Found) return Reject(locationId, NotFound);
            committed = true;
            if (written.OldKey is { } old && old != key) RemoveFiles(actor.TenantId, old, locationId); // лише після commit
            log.LogInformation("location_logo_uploaded location={Location} bytes={Bytes} w={W} h={H}", locationId, image.TotalBytes, image.Width, image.Height);
            return written.Location!;
        }
        finally
        {
            if (!committed) RemoveFiles(actor.TenantId, key, locationId);
        }
    }

    /// <summary>Ідемпотентно: заклад без логотипу також дає 200 з поточним DTO.</summary>
    public async Task<Result<LocationDto>> DeleteAsync(Actor actor, Guid locationId, CancellationToken ct)
    {
        if (!StaffRoles.CanEditLocations(actor)) return Forbidden;
        if (!options.Enabled) return Disabled;
        var r = await store.SetLogoAsync(locationId, null, ct);
        if (!r.Found) return NotFound;
        if (r.OldKey is { } old) RemoveFiles(actor.TenantId, old, locationId); // лише після commit
        log.LogInformation("location_logo_deleted location={Location}", locationId);
        return r.Location!;
    }

    private Result<LocationDto> Reject(Guid locationId, Error e)
    {
        log.LogInformation("location_logo_rejected location={Location} code={Code}", locationId, e.Code);
        return e;
    }

    /// <summary>Best effort: збій видалення файлу логується й не змінює відповіді (залишок прибере очищувач).</summary>
    private void RemoveFiles(Guid tenantId, string key, Guid locationId)
    {
        try
        {
            if (storage.Delete(tenantId, key)) return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        log.LogWarning("location_logo_file_cleanup_failed location={Location}", locationId);
    }
}
