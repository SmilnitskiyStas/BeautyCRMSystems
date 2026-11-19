using BeautyCrm.Api.Infrastructure;
using BeautyCrm.Api.PublicBooking;
using BeautyCrm.Application.Features.BeautyPublicCatalog;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace BeautyCrm.Api.Controllers;

/// <summary>
/// ADR-022: ПУБЛІЧНИЙ каталог закладів (cross-tenant), без JWT і без {tenantSlug}. Явний виняток із "автентифікація за замовчуванням".
/// Читає лише через SECURITY DEFINER-функції БД; відповіді не містять tenant_id/телефонів. Маршрути `resolve` і `locations`
/// зарезервовані як slug-и tenant-ів, тож не конфліктують з api/public/{tenantSlug}/...
/// Координати nearby і місто search: лише в тілі POST, не логуються й не зберігаються; no-store.
/// </summary>
[ApiController]
[Route("api/public")]
[AllowAnonymous]
[PublicClient]
[ProducesResponseType(StatusCodes.Status429TooManyRequests)]
[ProducesResponseType(StatusCodes.Status426UpgradeRequired)]
public sealed class PublicCatalogController(PublicCatalogService catalog) : ControllerBase
{
    /// <summary>Код XXXX-XXXX або XXXXXXXX (регістр, O/I/L-аліаси). Будь-яка помилка/невідомий код — однакова 404 `not_found`.</summary>
    [HttpGet("resolve/{publicCode}")]
    [EnableRateLimiting(PublicRateLimit.Token)]
    [ProducesResponseType<PublicLocationRefDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Resolve(string publicCode, CancellationToken ct)
    {
        NoStore();
        return this.ToResult(await catalog.ResolveAsync(publicCode, ct));
    }

    /// <summary>Найближчі discoverable-заклади (до 10, радіус до 30 км). 422 invalid_coordinates / invalid_radius.</summary>
    [HttpPost("locations/nearby")]
    [EnableRateLimiting(PublicRateLimit.Nearby)]
    [RequestSizeLimit(1024)]
    [ProducesResponseType<PublicNearbyResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Nearby([FromBody] NearbyRequest request, CancellationToken ct)
    {
        NoStore();
        return this.ToResult(await catalog.NearbyAsync(request, ct));
    }

    /// <summary>Discoverable-заклади міста (точний збіг без урахування регістру, до 10). Тіло {city}; 422 invalid_city. GET-варіанта немає: місто в URL потрапляло б у логи.</summary>
    [HttpPost("locations/search")]
    [EnableRateLimiting(PublicRateLimit.Search)]
    [RequestSizeLimit(1024)]
    [ProducesResponseType<PublicSearchResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Search([FromBody] SearchRequest request, CancellationToken ct)
    {
        NoStore();
        return this.ToResult(await catalog.SearchAsync(request?.City, ct));
    }

    private void NoStore()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
    }
}
