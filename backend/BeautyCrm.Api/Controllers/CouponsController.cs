using BeautyCrm.Api.Auth;
using BeautyCrm.Api.Infrastructure;
using BeautyCrm.Api.Tenancy;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyCoupons;
using BeautyCrm.Application.Features.BeautyStaff;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BeautyCrm.Api.Controllers;

/// <summary>
/// Загальні коди купонів (§26.11.4, ADR-015): модуль beauty_marketing + Marketing:Enabled, owner/admin (specialist -> 403 forbidden_role
/// до читання тіла). Відповіді - Cache-Control: no-store. Персональні коди API не віддає (лише code_hint в /clients/{id}/coupons).
/// </summary>
[ApiController]
[Route("api/beauty/coupons")]
[RequireModule("beauty_marketing")]
[CouponsMarketingGate]
[Authorize(Policy = AuthPolicies.Staff)]
[ReminderRoles(Roles.Owner, Roles.Admin)]
[InboxNoStore]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public sealed class CouponsController(CouponService coupons) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<SharedCouponDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] Guid? promotionId, [FromQuery] string? kind, CancellationToken ct) =>
        kind is not null && kind != "shared"
            ? UnprocessableEntity(new ApiError("invalid_kind", "Only kind=shared is available."))
            : Ok(await coupons.ListSharedAsync(promotionId, ct));

    /// <summary>422 invalid_code | invalid_period | invalid_max_uses | promotion_not_code_only; 404 promotion_not_found; 409 coupon_code_taken.</summary>
    [HttpPost]
    [RequestSizeLimit(2 * 1024)]
    [ProducesResponseType<SharedCouponDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] CreateSharedCouponRequest request, CancellationToken ct)
    {
        var r = await coupons.CreateSharedAsync(request, this.UserId(), ct);
        if (!r.IsOk && r.Error!.Kind == BeautyCrm.Application.Features.BeautyCommon.ErrorKind.TooManyRequests) Response.Headers.RetryAfter = "60";
        return this.ToResult(r, c => StatusCode(StatusCodes.Status201Created, c));
    }

    /// <summary>Лише {status: "revoked"} (ідемпотентно). Персональні купони сюди не потрапляють (404).</summary>
    [HttpPatch("{id:guid}")]
    [RequestSizeLimit(1024)]
    public async Task<IActionResult> Patch(Guid id, [FromBody] PatchCouponRequest request, CancellationToken ct)
    {
        if (request.Status != "revoked") return UnprocessableEntity(new ApiError("invalid_status", "status must be revoked."));
        var r = await coupons.RevokeAsync(id, this.UserId(), ct);
        if (!r.IsOk && r.Error!.Kind == BeautyCrm.Application.Features.BeautyCommon.ErrorKind.TooManyRequests) Response.Headers.RetryAfter = "60";
        return r.IsOk ? Ok(new { id, status = "revoked" }) : this.ToError(r.Error!);
    }
}

/// <summary>Купони клієнта (без кодів): owner/admin, модуль beauty_clients.</summary>
[ApiController]
[Route("api/beauty/clients/{clientId:guid}/coupons")]
[RequireModule("beauty_clients")]
[Authorize(Policy = AuthPolicies.Staff)]
[ReminderRoles(Roles.Owner, Roles.Admin)]
[InboxNoStore]
public sealed class ClientCouponsController(CouponService coupons) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<ClientCouponDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> List(Guid clientId, CancellationToken ct) => this.ToResult(await coupons.ListForClientAsync(clientId, ct));
}

/// <summary>Перевірка коду персоналом (усі ролі, модуль beauty_booking). 429 при перевищенні StaffCheckPerUserPerMinute.</summary>
[ApiController]
[Route("api/beauty/coupons/check")]
[RequireModule("beauty_booking")]
[Authorize(Policy = AuthPolicies.Staff)]
[InboxNoStore]
public sealed class CouponCheckController(CouponService coupons) : ControllerBase
{
    [HttpPost]
    [RequestSizeLimit(2 * 1024)]
    [ProducesResponseType<CouponCheckResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Check([FromBody] CouponCheckRequest request, CancellationToken ct)
    {
        if (User.ToActor() is not { } actor) return Unauthorized();
        if (request.LocationId is not { } loc || request.ServiceId is not { } svc)
            return UnprocessableEntity(new ApiError("invalid_request", "locationId and serviceId are required."));
        var r = await coupons.CheckAsync(new CouponUse(CouponScope.Staff, request.Code, RateKey: actor.UserId.ToString()), loc, svc, request.ClientId, ct);
        if (!r.IsOk && r.Error!.Kind == BeautyCrm.Application.Features.BeautyCommon.ErrorKind.TooManyRequests) Response.Headers.RetryAfter = "60";
        // TASK-790 (L7): майстер (specialist) не отримує деталізованих причин - підбір і розвідка станів кодів; owner/admin бачать повні коди
        if (!r.IsOk && !StaffRoles.IsManager(actor) && IsCodeReason(r.Error!.Code))
            return this.ToError(BeautyCrm.Application.Features.BeautyCommon.Error.Conflict(CouponService.Invalid, "The code is not valid."));
        return this.ToResult(r);
    }

    private static bool IsCodeReason(string code) => code.StartsWith("coupon_", StringComparison.Ordinal) || code is "invalid_coupon_format" or "promotion_inactive";
}

/// <summary>403 marketing_disabled, якщо Marketing:Enabled=false (§26.2). Працює до прив'язки тіла.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class CouponsMarketingGateAttribute : Attribute, IActionFilter, IOrderedFilter
{
    public int Order => -2900;

    public void OnActionExecuting(ActionExecutingContext context)
    {
        var cfg = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        if (cfg.GetValue("Marketing:Enabled", false)) return;
        context.Result = new ObjectResult(new ApiError("marketing_disabled", "Marketing features are disabled.")) { StatusCode = StatusCodes.Status403Forbidden };
    }

    public void OnActionExecuted(ActionExecutedContext context)
    {
    }
}
