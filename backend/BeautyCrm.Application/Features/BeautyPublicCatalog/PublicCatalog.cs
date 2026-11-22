using System.Text.RegularExpressions;
using BeautyCrm.Application.Features.BeautyCommon;

namespace BeautyCrm.Application.Features.BeautyPublicCatalog;

/// <summary>
/// ADR-022: публічний каталог закладів (cross-tenant, лише через definer-функції БД). Відповідь навмисно НЕ містить
/// tenant_id, телефонів і будь-чого поза переліком полів. LogoUrl - абсолютний URL логотипу (варіант 400) або null (немає / медіа вимкнено).
/// </summary>
public sealed record PublicLocationRefDto(string PublicCode, string TenantSlug, Guid LocationId, string Name,
    string? Address, string? City, string Timezone, string? LogoUrl = null);

/// <param name="DistanceKm">Відстань до точки пошуку, км, з точністю 0.1.</param>
public sealed record PublicNearbyItemDto(string PublicCode, string TenantSlug, Guid LocationId, string Name,
    string? Address, string? City, string Timezone, decimal DistanceKm, string? LogoUrl = null);

/// <summary>Тіло POST /locations/nearby. Координати живуть лише на час запиту: не логуються й не зберігаються.</summary>
public sealed record NearbyRequest(double? Lat, double? Lng, double? RadiusKm = null);

/// <summary>Тіло POST /locations/search. Місто живе лише в тілі (не в URL), щоб не потрапляти в access-логи.</summary>
public sealed record SearchRequest(string? City);

public sealed record PublicNearbyResponse(IReadOnlyList<PublicNearbyItemDto> Items);

public sealed record PublicSearchResponse(IReadOnlyList<PublicLocationRefDto> Items);

/// <summary>Порт даних каталогу: SECURITY DEFINER-функції beauty_public_* (виконуються без tenant-контексту).</summary>
public interface IPublicCatalogStore
{
    /// <summary>Код нормалізує сама функція БД (XXXX-XXXX, O->0, I/L->1).</summary>
    Task<PublicLocationRefDto?> ResolveAsync(string code, CancellationToken ct);
    Task<IReadOnlyList<PublicNearbyItemDto>> NearbyAsync(double lat, double lng, double radiusKm, int limit, CancellationToken ct);
    Task<IReadOnlyList<PublicLocationRefDto>> SearchByCityAsync(string city, int limit, CancellationToken ct);
}

public sealed partial class PublicCatalogService(IPublicCatalogStore store)
{
    public const double MaxRadiusKm = 30;
    public const int MaxItems = 10;
    public const int CoordinateDecimals = 3;
    public const int MinCity = 2, MaxCity = 100;

    /// <summary>Єдина відповідь на будь-яку невдачу resolve (формат, невідомий код, неактивний заклад, призупинений tenant...).</summary>
    public static readonly Error NotFound = Error.NotFound("not_found", "Not found.");

    // 8 символів Crockford Base32 (з роздільником або без) у будь-якому регістрі; допускаються аліаси O/I/L (нормалізує БД). U — ні.
    [GeneratedRegex("^[0-9A-HJKMNP-TV-ZOILa-hjkmnp-tv-zoil]{4}-?[0-9A-HJKMNP-TV-ZOILa-hjkmnp-tv-zoil]{4}$")]
    private static partial Regex CodeShape();

    public async Task<Result<PublicLocationRefDto>> ResolveAsync(string? code, CancellationToken ct)
    {
        if (code is null || code.Length > 9 || !CodeShape().IsMatch(code)) return NotFound;
        return await store.ResolveAsync(code, ct) is { } hit ? hit : NotFound;
    }

    public async Task<Result<PublicNearbyResponse>> NearbyAsync(NearbyRequest? req, CancellationToken ct)
    {
        if (req?.Lat is not { } lat || req.Lng is not { } lng || !double.IsFinite(lat) || !double.IsFinite(lng)
            || lat is < -90 or > 90 || lng is < -180 or > 180)
            return Error.Validation("invalid_coordinates", "lat must be within -90..90 and lng within -180..180.");
        var radius = MaxRadiusKm;
        if (req.RadiusKm is { } r)
        {
            if (!double.IsFinite(r) || r <= 0) return Error.Validation("invalid_radius", "radiusKm must be a positive number.");
            radius = Math.Min(r, MaxRadiusKm);
        }
        // M2: точність ~110 м; повні координати не доходять до параметрів SQL (а отже й до логів PostgreSQL).
        return new PublicNearbyResponse(await store.NearbyAsync(
            Math.Round(lat, CoordinateDecimals, MidpointRounding.AwayFromZero), Math.Round(lng, CoordinateDecimals, MidpointRounding.AwayFromZero), radius, MaxItems, ct));
    }

    public async Task<Result<PublicSearchResponse>> SearchAsync(string? city, CancellationToken ct)
    {
        var c = city?.Trim();
        if (c is null || c.Length is < MinCity or > MaxCity || c.Any(char.IsControl))
            return Error.Validation("invalid_city", $"city must be {MinCity}-{MaxCity} characters.");
        return new PublicSearchResponse(await store.SearchByCityAsync(c, MaxItems, ct));
    }
}
