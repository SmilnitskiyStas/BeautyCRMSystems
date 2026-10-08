using BeautyCrm.Api.Infrastructure;
using BeautyCrm.Api.PublicBooking;
using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyPublicBooking;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace BeautyCrm.Api.Controllers;

/// <summary>
/// ПУБЛІЧНИЙ API онлайн-запису (TASK-688): без JWT, tenant за slug з URL. Явний виняток із "автентифікація за замовчуванням".
/// Контролер лише маршрутизує; уся логіка — Application/Features/BeautyPublicBooking.
/// Невідомий slug / призупинений tenant / вимкнений модуль -> однакова 404 `not_found`.
/// </summary>
[ApiController]
[Route("api/public/{tenantSlug}")]
[AllowAnonymous]
[PublicTenant]
[ProducesResponseType(StatusCodes.Status404NotFound)]
[ProducesResponseType(StatusCodes.Status429TooManyRequests)]
public sealed class PublicBookingController(PublicBookingService service) : ControllerBase
{
    public const string IdempotencyHeader = "Idempotency-Key";
    public const string CaptchaHeader = "X-Captcha-Token";

    [HttpGet("locations")]
    [EnableRateLimiting(PublicRateLimit.Read)]
    [ProducesResponseType<IReadOnlyList<PublicLocationDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Locations(CancellationToken ct) => Ok(await service.ListLocationsAsync(ct));

    [HttpGet("locations/{locationId:guid}/specialists")]
    [EnableRateLimiting(PublicRateLimit.Read)]
    [ProducesResponseType<IReadOnlyList<PublicSpecialistDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Specialists(Guid locationId, [FromQuery] Guid? serviceId, CancellationToken ct) =>
        this.ToResult(await service.ListSpecialistsAsync(locationId, serviceId, ct));

    /// <summary>Послуги закладу: ціна з override закладу й акцією, тривалість.</summary>
    [HttpGet("locations/{locationId:guid}/services")]
    [EnableRateLimiting(PublicRateLimit.Read)]
    [ProducesResponseType<IReadOnlyList<PublicServiceDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Services(Guid locationId, [FromQuery] Guid? specialistId, CancellationToken ct) =>
        this.ToResult(await service.ListServicesAsync(locationId, specialistId, ct));

    /// <summary>Вільні слоти з умовами скасування (§11) у кожному елементі.</summary>
    [HttpGet("slots")]
    [EnableRateLimiting(PublicRateLimit.Read)]
    [ProducesResponseType<IReadOnlyList<FreeSlot>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Slots(
        [FromQuery] Guid locationId, [FromQuery] Guid serviceId, [FromQuery] DateOnly date, [FromQuery] Guid? specialistId,
        CancellationToken ct) =>
        this.ToResult(await service.GetSlotsAsync(locationId, specialistId, serviceId, date, ct));

    /// <summary>
    /// 201 (новий запис) або 200 + `Idempotent-Replayed: true` (повтор того ж Idempotency-Key). 422 валідація, 409 слот зайнятий,
    /// 402 платіж не пройшов. Заголовок `Idempotency-Key` обов'язковий; `X-Captcha-Token` — коли CAPTCHA увімкнена.
    /// </summary>
    [HttpPost("appointments")]
    [EnableRateLimiting(PublicRateLimit.Write)]
    [ProducesResponseType<PublicBookingCreatedDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<PublicBookingCreatedDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Create([FromBody] PublicCreateAppointmentRequest request, CancellationToken ct)
    {
        NoStore();
        var ctx = new PublicRequestContext(
            Request.Headers[IdempotencyHeader].FirstOrDefault(),
            Request.Headers[CaptchaHeader].FirstOrDefault() ?? request.CaptchaToken,
            HttpContext.Connection.RemoteIpAddress?.ToString());
        return this.ToResult(await service.CreateAsync(request, ctx, ct), r =>
        {
            if (!r.Replayed) return StatusCode(StatusCodes.Status201Created, r.Booking);
            Response.Headers["Idempotent-Replayed"] = "true";
            return Ok(r.Booking);
        });
    }

    /// <summary>Перегляд запису за довгим випадковим токеном. Невідомий/чужий/зіпсований токен — однакова 404.</summary>
    [HttpGet("appointments/{publicToken}")]
    [EnableRateLimiting(PublicRateLimit.Token)]
    [ProducesResponseType<PublicAppointmentDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(string publicToken, CancellationToken ct)
    {
        NoStore();
        return this.ToResult(await service.GetAsync(publicToken, ct));
    }

    /// <summary>Скасування за політикою §11; відповідь містить refundAmount/refundPercent/feePercent.</summary>
    [HttpPost("appointments/{publicToken}/cancel")]
    [EnableRateLimiting(PublicRateLimit.Write)]
    [ProducesResponseType<PublicCancelResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Cancel(string publicToken, CancellationToken ct)
    {
        NoStore();
        return this.ToResult(await service.CancelAsync(publicToken, ct));
    }

    private void NoStore()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
    }
}
