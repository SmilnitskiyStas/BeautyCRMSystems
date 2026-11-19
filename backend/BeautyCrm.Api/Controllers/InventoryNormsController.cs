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
/// Норми матеріалів послуг (ADR-025 §11, §32.16): модуль beauty_inventory, читання inventory.view &gt;= own (override закладів - лише scope),
/// зміна - owner/admin + inventory.manage = all. Лише маршрутизація: уся логіка в <see cref="InventoryNormsService"/>. no-store.
/// </summary>
[ApiController]
[Route("api/beauty/inventory")]
[RequireModule(BeautyModules.Inventory)]
[Authorize(Policy = AuthPolicies.Staff)]
[RequirePermission(PermissionKeys.InventoryView, PermissionLevels.Own)]
[InboxNoStore]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public sealed class InventoryNormsController(InventoryNormsService norms, InventoryThrottle throttle) : ControllerBase
{
    private const int Body = 16 * 1024;

    private IActionResult RateLimited()
    {
        Response.Headers.RetryAfter = "60";
        return StatusCode(StatusCodes.Status429TooManyRequests, new ApiError("rate_limited", "Too many requests. Try again later."));
    }

    [HttpGet("service-materials")]
    [ProducesResponseType<ServiceMaterialsList>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] bool includeInactive, CancellationToken ct) =>
        User.ToActor() is { } a ? this.ToResult(await norms.ListAsync(a, includeInactive, ct)) : Unauthorized();

    [HttpGet("services/{serviceId:guid}/materials")]
    [ProducesResponseType<ServiceMaterialsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid serviceId, CancellationToken ct) =>
        User.ToActor() is { } a ? this.ToResult(await norms.GetAsync(a, serviceId, ct)) : Unauthorized();

    /// <summary>404 service_not_found | location_not_found | product_not_found; 422 invalid_lines | too_many_lines | duplicate_line | invalid_quantity | product_not_for_salon_use | product_disabled_for_location.</summary>
    [HttpPut("services/{serviceId:guid}/materials")]
    [RequirePermission(PermissionKeys.InventoryManage, PermissionLevels.All)]
    [RequestSizeLimit(Body)]
    [ProducesResponseType<MaterialSetDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Put(Guid serviceId, [FromBody] PutMaterialsRequest request, CancellationToken ct)
    {
        if (User.ToActor() is not { } a) return Unauthorized();
        if (!throttle.TryAcquire(a.TenantId, a.UserId)) return RateLimited();
        return this.ToResult(await norms.PutAsync(a, serviceId, request, ct));
    }

    /// <summary>Без locationId видаляється мережевий набір. 404 service_not_found | location_not_found | materials_not_found.</summary>
    [HttpDelete("services/{serviceId:guid}/materials")]
    [RequirePermission(PermissionKeys.InventoryManage, PermissionLevels.All)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Delete(Guid serviceId, [FromQuery] Guid? locationId, CancellationToken ct)
    {
        if (User.ToActor() is not { } a) return Unauthorized();
        if (!throttle.TryAcquire(a.TenantId, a.UserId)) return RateLimited();
        return this.ToResult(await norms.DeleteAsync(a, serviceId, locationId, ct), _ => NoContent());
    }
}
