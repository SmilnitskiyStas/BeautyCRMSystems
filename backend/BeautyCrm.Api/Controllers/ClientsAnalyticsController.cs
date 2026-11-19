using BeautyCrm.Api.Auth;
using BeautyCrm.Api.Infrastructure;
using BeautyCrm.Api.Tenancy;
using BeautyCrm.Application.Features.BeautyAnalytics;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyClients;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BeautyCrm.Api.Controllers;

[ApiController]
[Route("api/beauty/clients")]
[RequireModule("beauty_clients")]
[Authorize(Policy = AuthPolicies.Staff)]
[RequirePermission(PermissionKeys.ClientsView, PermissionLevels.Own)]
public sealed class ClientsController(ClientService clients) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default) =>
        User.ToActor() is { } actor ? Ok(await clients.ListAsync(actor, search, page, pageSize, ct)) : Unauthorized();

    /// <summary>Картка клієнта: історія записів і нотатки.</summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        User.ToActor() is { } actor ? this.ToResult(await clients.GetAsync(actor, id, ct)) : Unauthorized();

    [HttpPost]
    [RequirePermission(PermissionKeys.ClientsEdit, PermissionLevels.All)]
    public async Task<IActionResult> Create([FromBody] CreateClientRequest request, CancellationToken ct) =>
        User.ToActor() is { } actor
            ? this.ToResult(await clients.CreateAsync(actor, request, ct), c => CreatedAtAction(nameof(Get), new { id = c.Id }, c))
            : Unauthorized();

    [HttpPost("{id:guid}/notes")]
    [RequirePermission(PermissionKeys.ClientsEdit, PermissionLevels.Own)]
    public async Task<IActionResult> AddNote(Guid id, [FromBody] AddNoteRequest request, CancellationToken ct) =>
        User.ToActor() is { } actor
            ? this.ToResult(await clients.AddNoteAsync(actor, id, request, ct), n => StatusCode(StatusCodes.Status201Created, n))
            : Unauthorized();
}

[ApiController]
[Route("api/beauty/analytics")]
[RequireModule("beauty_analytics")]
[Authorize(Policy = AuthPolicies.Staff)]
[RequirePermission(PermissionKeys.RevenueView, PermissionLevels.All)] // поля собівартості - за revenue.costs >= view (CostFieldRedaction)
public sealed class AnalyticsController(AnalyticsService analytics, AnalyticsDetailsService details, AnalyticsDetailsThrottle detailsThrottle)
    : ControllerBase
{
    /// <summary>
    /// Деталі періоду (§24.6.3): date=yyyy-MM-dd або from&amp;to (до 31 дня), locationId?, cursor?, take (1..200, за замовч. 100).
    /// 422 invalid_date | invalid_range | invalid_cursor; 404 location_not_found; 429 rate_limited + Retry-After. no-store.
    /// </summary>
    [HttpGet("network/details")]
    [ProducesResponseType<DetailsReport>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Details(
        [FromQuery] string? date, [FromQuery] string? from, [FromQuery] string? to, [FromQuery] Guid? locationId,
        [FromQuery] string? cursor, [FromQuery] int? take, CancellationToken ct = default)
    {
        if (User.ToActor() is not { } actor) return Unauthorized();
        if (!detailsThrottle.TryAcquire(actor.TenantId, actor.UserId, out var retryAfter))
        {
            Response.Headers.RetryAfter = retryAfter.ToString();
            return StatusCode(StatusCodes.Status429TooManyRequests, new ApiError("rate_limited", "Too many requests. Try again later."));
        }
        return this.ToResult(await details.DetailsAsync(date, from, to, locationId, cursor, take, ct), v => this.Redacted(v, actor));
    }

    // from/to - рядки: yyyy-MM-dd = датний режим (§19.1), значення з часом = legacy (v0.19). Невалідне значення -> 422 invalid_range.
    [HttpGet("network")]
    public async Task<IActionResult> Network([FromQuery] string? from, [FromQuery] string? to, [FromQuery] bool compare = false, CancellationToken ct = default) =>
        User.ToActor() is { } actor ? this.ToResult(await analytics.NetworkAsync(from, to, compare, ct), v => this.Redacted(v, actor)) : Unauthorized();

    [HttpGet("network/series")]
    public async Task<IActionResult> Series([FromQuery] string? from, [FromQuery] string? to, CancellationToken ct = default) =>
        User.ToActor() is { } actor ? this.ToResult(await analytics.SeriesAsync(from, to, ct), v => this.Redacted(v, actor)) : Unauthorized();

    [HttpGet("locations")]
    public async Task<IActionResult> Locations([FromQuery] string? from, [FromQuery] string? to, [FromQuery] bool compare = false, CancellationToken ct = default) =>
        User.ToActor() is { } actor ? this.ToResult(await analytics.LocationsAsync(from, to, compare, ct), v => this.Redacted(v, actor)) : Unauthorized();

    [HttpGet("locations/{locationId:guid}/breakdown")]
    public async Task<IActionResult> Breakdown(Guid locationId, [FromQuery] string? from, [FromQuery] string? to, CancellationToken ct = default) =>
        this.ToResult(await analytics.BreakdownAsync(locationId, from, to, ct));

    [HttpGet("promotions")]
    public async Task<IActionResult> Promotions([FromQuery] string? from, [FromQuery] string? to, [FromQuery] bool compare = false, CancellationToken ct = default) =>
        this.ToResult(await analytics.PromotionsAsync(from, to, compare, ct));
}

/// <summary>
/// §30.9.7: власний виторг майстра. Окремий контролер, бо <c>[RequireModule("beauty_analytics")]</c> обов'язковий без винятків (Q8),
/// а право - <c>revenue.view >= own</c> (аналітика мережі - <c>= all</c>). Лише власні completed записи, без specialistId у запиті.
/// </summary>
[ApiController]
[Route("api/beauty/me")]
[RequireModule("beauty_analytics")]
[Authorize(Policy = AuthPolicies.Staff)]
[RequirePermission(PermissionKeys.RevenueView, PermissionLevels.Own)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public sealed class MyRevenueController(MyRevenueService revenue) : ControllerBase
{
    [HttpGet("revenue")]
    [ProducesResponseType<MyRevenueDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Get([FromQuery] string? from, [FromQuery] string? to, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return User.ToActor() is { } actor ? this.ToResult(await revenue.GetAsync(actor, from, to, ct)) : Unauthorized();
    }
}
