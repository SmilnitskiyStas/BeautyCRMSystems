using System.Diagnostics;
using System.Security.Cryptography;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyCommon;
using Microsoft.Extensions.Logging;

namespace BeautyCrm.Application.Features.BeautyProductImages;

/// <summary>
/// Фото товарів у CRM (ADR-027 §2.7, §33.22.5). Порядок upload: права -> функція ввімкнена -> товар -> файл з тіла -> швидкі ліміти ->
/// обробка -> запис файлів -> транзакція (рядок ніколи не раніше файлів). Видалення файлів - лише після commit.
/// Логи без ключів сховища й імен файлів клієнта.
/// </summary>
public sealed class ProductImageService(IProductImageStore store, IProductImageStorage storage, IImageProcessor processor, MediaOptions options,
    TimeProvider clock, ILogger<ProductImageService> log)
{
    private static readonly System.Diagnostics.Metrics.Counter<long> ResultCounter =
        new System.Diagnostics.Metrics.Meter("BeautyCrm.ProductImages").CreateCounter<long>("beauty.product_image.result");

    private static readonly Error Disabled = Error.NotFound("product_images_disabled", "Product images are not available.");
    private static readonly Error ProductNotFound = Error.NotFound("product_not_found", "Product not found.");
    private static readonly Error ImageNotFound = Error.NotFound("product_image_not_found", "Product image not found.");
    private static readonly Error InvalidOrder = Error.Validation("invalid_image_order", "imageIds must be an exact permutation of the product's images.");

    public bool IsEnabled => options.Enabled;

    private static Error? Need(Actor a) =>
        a.Access.AtLeast(PermissionKeys.InventoryManage, PermissionLevels.All) ? null : Error.ForbiddenPermission(PermissionKeys.InventoryManage, PermissionLevels.All);

    private static Result<T> Rejected<T>(string result, Error e)
    {
        Activity.Current?.SetTag("beauty.product_image.result", result);
        ResultCounter.Add(1, new KeyValuePair<string, object?>("result", result));
        return e;
    }

    private static Error WithLimit(string code, string message, int limit) =>
        Error.Validation(code, message) with { Extras = new Dictionary<string, object?> { ["limit"] = limit } };

    /// <param name="readFile">Читає частину <c>file</c> з тіла запиту (після перевірок товару): 422 file_required, 413 file_too_large.</param>
    public async Task<Result<ProductImageDto>> UploadAsync(Actor actor, Guid productId, Func<CancellationToken, Task<Result<Stream>>> readFile, CancellationToken ct)
    {
        if (Need(actor) is { } denied) return denied;
        if (!options.Enabled) return Disabled;
        if (!await store.ProductExistsAsync(productId, ct)) return ProductNotFound;

        var started = Stopwatch.GetTimestamp();
        var file = await readFile(ct);
        if (!file.IsOk) return Reject<ProductImageDto>(productId, file.Error!);
        await using var input = file.Value!;

        var (inProduct, inTenant) = await store.CountsAsync(productId, ct);
        if (inProduct >= options.MaxImagesPerProduct) return Reject<ProductImageDto>(productId, WithLimit("too_many_images", "Too many images for this product.", options.MaxImagesPerProduct));
        if (inTenant >= options.MaxImagesPerTenant) return Reject<ProductImageDto>(productId, WithLimit("image_quota_exceeded", "Image quota exceeded.", options.MaxImagesPerTenant));

        var processed = await processor.ProcessAsync(input, ct);
        if (!processed.IsOk) return Reject<ProductImageDto>(productId, processed.Error!);
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
            var written = await store.AddAsync(productId,
                new NewProductImage(key, image.TotalBytes, image.Width, image.Height, actor.UserId, clock.GetUtcNow()),
                options.MaxImagesPerProduct, options.MaxImagesPerTenant, ct);
            switch (written.Status)
            {
                case ImageWriteStatus.ProductNotFound: return Reject<ProductImageDto>(productId, ProductNotFound);
                case ImageWriteStatus.TooManyImages: return Reject<ProductImageDto>(productId, WithLimit("too_many_images", "Too many images for this product.", options.MaxImagesPerProduct));
                case ImageWriteStatus.QuotaExceeded: return Reject<ProductImageDto>(productId, WithLimit("image_quota_exceeded", "Image quota exceeded.", options.MaxImagesPerTenant));
            }
            committed = true;
            var dto = written.Row!.ToDto(options);
            log.LogInformation("product_image_uploaded product={Product} image={Image} bytes={Bytes} w={W} h={H} ms={Ms}",
                productId, dto.Id, image.TotalBytes, image.Width, image.Height, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            Activity.Current?.SetTag("beauty.product_image.result", "uploaded");
            ResultCounter.Add(1, new KeyValuePair<string, object?>("result", "uploaded"));
            return dto;
        }
        finally
        {
            if (!committed) RemoveFiles(actor.TenantId, key, null);
        }
    }

    private Result<T> Reject<T>(Guid productId, Error e)
    {
        log.LogInformation("product_image_rejected product={Product} code={Code}", productId, e.Code);
        return Rejected<T>(e.Code, e);
    }

    /// <summary>Best effort: збій видалення файлу логується й не змінює відповіді (залишок прибере очищувач).</summary>
    private void RemoveFiles(Guid tenantId, string key, Guid? imageId)
    {
        try
        {
            if (storage.Delete(tenantId, key)) return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        log.LogWarning("product_image_file_cleanup_failed image={Image}", imageId);
    }

    public async Task<Result<bool>> DeleteAsync(Actor actor, Guid productId, Guid imageId, CancellationToken ct)
    {
        if (Need(actor) is { } denied) return denied;
        if (!options.Enabled) return Disabled;
        var r = await store.DeleteAsync(productId, imageId, ct);
        if (r.Status == ImageWriteStatus.ProductNotFound) return ProductNotFound;
        if (r.Status == ImageWriteStatus.ImageNotFound) return ImageNotFound;
        RemoveFiles(actor.TenantId, r.Key!, imageId); // лише після commit
        log.LogInformation("product_image_deleted product={Product} image={Image}", productId, imageId);
        return true;
    }

    public async Task<Result<ProductImageListDto>> ReorderAsync(Actor actor, Guid productId, ProductImageOrderRequest? request, CancellationToken ct)
    {
        if (Need(actor) is { } denied) return denied;
        if (!options.Enabled) return Disabled;
        if (request?.ImageIds is not { } ids || ids.Count == 0 || ids.Count > 100 || ids.Any(i => i == Guid.Empty))
            return await store.ProductExistsAsync(productId, ct) ? InvalidOrder : ProductNotFound;
        var r = await store.ReorderAsync(productId, ids, ct);
        return r.Status switch
        {
            ImageWriteStatus.ProductNotFound => ProductNotFound,
            ImageWriteStatus.InvalidOrder => InvalidOrder,
            _ => new ProductImageListDto([.. r.Rows!.Select(x => x.ToDto(options))]),
        };
    }
}
