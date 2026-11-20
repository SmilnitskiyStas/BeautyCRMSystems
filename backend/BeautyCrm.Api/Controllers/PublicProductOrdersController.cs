using System.Text.Json;
using BeautyCrm.Api.Infrastructure;
using BeautyCrm.Api.PublicBooking;
using BeautyCrm.Application.Features.BeautyProductOrders;
using BeautyCrm.Application.Features.BeautyPublicBooking;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Err = BeautyCrm.Application.Features.BeautyCommon.Error;

namespace BeautyCrm.Api.Controllers;

/// <summary>
/// ПУБЛІЧНИЙ API замовлень товарів (ADR-026, §33 P1-P5): без JWT, tenant за slug (явний виняток із «автентифікація за замовчуванням»).
/// Порядок перевірок §33.1: rate limit -> версія застосунку (426) -> tenant -> вмикання (404 product_orders_disabled) -> тіло -> бізнес-правила.
/// Усі відповіді no-store; маршрути з токеном - ще no-referrer. Токен замовлення в логи не потрапляє. Контролер лише маршрутизує.
/// </summary>
[ApiController]
[Route("api/public/{tenantSlug}")]
[AllowAnonymous]
[PublicClient]
[PublicTenant]
[ProducesResponseType(StatusCodes.Status404NotFound)]
[ProducesResponseType(StatusCodes.Status426UpgradeRequired)]
[ProducesResponseType(StatusCodes.Status429TooManyRequests)]
public sealed class PublicProductOrdersController(ProductOrderService service) : ControllerBase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>P1: каталог товарів закладу для замовлення (лише продажні, з ціною; без кількостей на складі).</summary>
    [HttpGet("locations/{locationId}/products")]
    [EnableRateLimiting(PublicRateLimit.Read)]
    [ProducesResponseType<PublicProductCatalogDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Catalog(string locationId, CancellationToken ct)
    {
        NoStore();
        return this.ToResult(await service.CatalogAsync(Guid.TryParse(locationId, out var id) ? id : null, ct));
    }

    /// <summary>P2: 201 {publicToken, order} або 200 + Idempotent-Replayed при повторі того самого Idempotency-Key.</summary>
    [HttpPost("product-orders")]
    [EnableRateLimiting(PublicRateLimit.Write)]
    [RequestSizeLimit(8 * 1024)]
    [ProducesResponseType<PublicProductOrderCreatedDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<PublicProductOrderCreatedDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Create(CancellationToken ct)
    {
        NoStore();
        // вмикання перевіряється ДО розбору тіла (§33.1), тож 404 product_orders_disabled не залежить від валідності JSON
        if (!service.IsEnabled) return this.ToError(Err.NotFound(ProductOrderService.Disabled, "Product orders are not available."));
        CreateProductOrderRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<CreateProductOrderRequest>(Request.Body, Json, ct);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            request = null;
        }
        if (request is null) return this.ToError(Err.Validation("invalid_request", "Invalid request."));

        var ctx = new PublicRequestContext(
            Request.Headers[PublicBookingController.IdempotencyHeader].FirstOrDefault(),
            Request.Headers[PublicBookingController.CaptchaHeader].FirstOrDefault() ?? request.CaptchaToken,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            PublicClientInfo.KindOf(HttpContext),
            Request.Headers[PublicClientInfo.IntegrityHeader].FirstOrDefault());
        return this.ToResult(await service.CreateAsync(request, ctx, ct), r =>
        {
            if (!r.Replayed) return StatusCode(StatusCodes.Status201Created, r.Created);
            Response.Headers["Idempotent-Replayed"] = "true";
            return Ok(r.Created);
        });
    }

    /// <summary>P3: перегляд за токеном; працює й коли функцію вимкнено. Невідомий/чужий/зіпсований токен - однакова 404.</summary>
    [HttpGet("product-orders/{publicToken}")]
    [EnableRateLimiting(PublicRateLimit.Token)]
    [ProducesResponseType<PublicProductOrderDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(string publicToken, CancellationToken ct)
    {
        NoStore();
        return this.ToResult(await service.GetAsync(publicToken, ct));
    }

    /// <summary>P4: оплата (V1 - mock, тіло ігнорується). 402 payment_failed, 409 stock_unavailable|payment_in_progress|order_expired|order_closed, 422 payment_attempts_exceeded.</summary>
    [HttpPost("product-orders/{publicToken}/pay")]
    [EnableRateLimiting(PublicRateLimit.Write)]
    [RequestSizeLimit(8 * 1024)]
    [ProducesResponseType<PublicProductOrderEnvelope>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Pay(string publicToken, CancellationToken ct)
    {
        NoStore();
        return this.ToResult(await service.PayAsync(publicToken, ct), Envelope);
    }

    /// <summary>P5: скасування клієнтом (лише неоплачене).</summary>
    [HttpPost("product-orders/{publicToken}/cancel")]
    [EnableRateLimiting(PublicRateLimit.Write)]
    [RequestSizeLimit(8 * 1024)]
    [ProducesResponseType<PublicProductOrderEnvelope>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Cancel(string publicToken, CancellationToken ct)
    {
        NoStore();
        return this.ToResult(await service.CancelAsync(publicToken, ct), Envelope);
    }

    private IActionResult Envelope(PublicProductOrderResult r)
    {
        if (r.Replayed) Response.Headers["Idempotent-Replayed"] = "true";
        return Ok(new PublicProductOrderEnvelope(r.Order));
    }

    private void NoStore()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
    }
}
