using BeautyCrm.Api.Auth;
using BeautyCrm.Api.Infrastructure;
using BeautyCrm.Api.Tenancy;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyCommon;
using BeautyCrm.Application.Features.BeautyInventory;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BeautyCrm.Api.Controllers;

/// <summary>
/// Сповіщення персоналу складу (ADR-025 §11, §32.19): модуль beauty_inventory, inventory.view &gt;= own зі scope закладу (поза scope - 404
/// alert_not_found). Прочитане спільне для tenant; resolved ставить лише система. Без ПД клієнтів. no-store. Логіка - в <see cref="InventoryAlertsService"/>.
/// </summary>
[ApiController]
[Route("api/beauty/inventory/alerts")]
[RequireModule(BeautyModules.Inventory)]
[Authorize(Policy = AuthPolicies.Staff)]
[RequirePermission(PermissionKeys.InventoryView, PermissionLevels.Own)]
[InboxNoStore]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public sealed class InventoryAlertsController(InventoryAlertsService alerts, InventoryThrottle throttle) : ControllerBase
{
    private IActionResult RateLimited()
    {
        Response.Headers.RetryAfter = "60";
        return StatusCode(StatusCodes.Status429TooManyRequests, new ApiError("rate_limited", "Too many requests. Try again later."));
    }

    [HttpGet]
    [ProducesResponseType<Page<StaffAlertDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] string? kind, [FromQuery] Guid? locationId, [FromQuery] string? cursor,
        [FromQuery] int? take, CancellationToken ct) =>
        User.ToActor() is { } a ? this.ToResult(await alerts.ListAsync(a, new StaffAlertListQuery(status, kind, locationId, cursor, take), ct)) : Unauthorized();

    [HttpGet("unread-count")]
    [ProducesResponseType<AlertCountDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> UnreadCount(CancellationToken ct) =>
        User.ToActor() is { } a ? this.ToResult(await alerts.UnreadCountAsync(a, ct)) : Unauthorized();

    [HttpPost("{id:guid}/read")]
    [ProducesResponseType<StaffAlertDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Read(Guid id, CancellationToken ct)
    {
        if (User.ToActor() is not { } a) return Unauthorized();
        if (!throttle.TryAcquire(a.TenantId, a.UserId)) return RateLimited();
        return this.ToResult(await alerts.MarkReadAsync(a, id, ct));
    }

    [HttpPost("read-all")]
    [ProducesResponseType<AlertsUpdatedDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> ReadAll([FromQuery] Guid? locationId, CancellationToken ct)
    {
        if (User.ToActor() is not { } a) return Unauthorized();
        if (!throttle.TryAcquire(a.TenantId, a.UserId)) return RateLimited();
        return this.ToResult(await alerts.MarkAllReadAsync(a, locationId, ct));
    }
}
