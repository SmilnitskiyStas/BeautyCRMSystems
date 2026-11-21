using BeautyCrm.Api.Auth;
using BeautyCrm.Api.Infrastructure;
using BeautyCrm.Api.Tenancy;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyCommon;
using BeautyCrm.Application.Features.BeautyInventory;
using BeautyCrm.Application.Features.BeautyProductOrders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BeautyCrm.Api.Controllers;

/// <summary>
/// CRM замовлень товарів (ADR-026, контракт §33.13): модуль beauty_inventory, права inventory.view (читання) / inventory.manage (дії), scope закладів як у складі.
/// Від <c>ProductOrders:Enabled</c> не залежить. Усі відповіді no-store; ліміт запису - <c>Inventory:WritePerUserPerMinute</c>. Контролер лише маршрутизує.
/// </summary>
[ApiController]
[Route("api/beauty/inventory/product-orders")]
[RequireModule(BeautyModules.Inventory)]
[Authorize(Policy = AuthPolicies.Staff)]
[RequirePermission(PermissionKeys.InventoryView, PermissionLevels.Own)]
[InboxNoStore]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public sealed class InventoryProductOrdersController(ProductOrderAdminService service, InventoryThrottle throttle) : ControllerBase
{
    private async Task<IActionResult> Run<T>(Func<Actor, Task<Result<T>>> call, bool write = false)
    {
        if (User.ToActor() is not { } actor) return Unauthorized();
        if (write && !throttle.TryAcquire(actor.TenantId, actor.UserId))
        {
            Response.Headers.RetryAfter = "60";
            return StatusCode(StatusCodes.Status429TooManyRequests, new ApiError("rate_limited", "Too many requests. Try again later."));
        }
        return this.ToResult(await call(actor));
    }

    [HttpGet]
    [ProducesResponseType<Page<ProductOrderSummaryDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public Task<IActionResult> List([FromQuery] string? status, [FromQuery] Guid? locationId, [FromQuery] string? from, [FromQuery] string? to,
        [FromQuery] string? number, [FromQuery] string? cursor, [FromQuery] int? take, CancellationToken ct) =>
        Run(a => service.ListAsync(a, new ProductOrderListQuery(status, locationId, from, to, number, cursor, take), ct));

    [HttpGet("counts")]
    [ProducesResponseType<ProductOrderCountsDto>(StatusCodes.Status200OK)]
    public Task<IActionResult> Counts(CancellationToken ct) => Run(a => service.CountsAsync(a, ct));

    [HttpGet("{id:guid}")]
    [ProducesResponseType<ProductOrderDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Get(Guid id, CancellationToken ct) => Run(a => service.GetAsync(a, id, ct));

    /// <summary>paid -> fulfilled (повтор на fulfilled = 200 noop). 409 order_not_paid | refund_in_progress.</summary>
    [HttpPost("{id:guid}/fulfill")]
    [RequirePermission(PermissionKeys.InventoryManage, PermissionLevels.Own)]
    [RequestSizeLimit(8 * 1024)]
    [ProducesResponseType<ProductOrderDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public Task<IActionResult> Fulfill(Guid id, CancellationToken ct) => Run(a => service.FulfillAsync(a, id, ct), write: true);

    /// <summary>Неоплачене -> cancelled (staff); оплачене -> повернення 100% + sale_return -> refunded. 402 refund_failed; 409 order_fulfilled | order_closed | payment_in_progress | refund_in_progress.</summary>
    [HttpPost("{id:guid}/cancel")]
    [RequirePermission(PermissionKeys.InventoryManage, PermissionLevels.Own)]
    [RequestSizeLimit(8 * 1024)]
    [ProducesResponseType<ProductOrderCancelDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status402PaymentRequired)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public Task<IActionResult> Cancel(Guid id, [FromBody] CancelProductOrderRequest? request, CancellationToken ct) =>
        Run(a => service.CancelAsync(a, id, request, ct), write: true);

    /// <summary>Завершує завислу оплату (лише paymentInProgress, старшу за PaymentStaleSeconds). 409 nothing_to_reconcile | payment_in_progress.</summary>
    [HttpPost("{id:guid}/reconcile-payment")]
    [RequirePermission(PermissionKeys.InventoryManage, PermissionLevels.Own)]
    [RequestSizeLimit(8 * 1024)]
    [ProducesResponseType<ProductOrderDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public Task<IActionResult> Reconcile(Guid id, CancellationToken ct) => Run(a => service.ReconcilePaymentAsync(a, id, ct), write: true);
}
