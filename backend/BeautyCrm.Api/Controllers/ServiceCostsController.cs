using BeautyCrm.Api.Auth;
using BeautyCrm.Api.Infrastructure;
using BeautyCrm.Api.Tenancy;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Infrastructure.Data.Beauty;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BeautyCrm.Api.Controllers;

/// <summary>
/// Собівартість послуг (контракт §24.6.1, ADR-014): модуль beauty_catalog, лише owner/admin (specialist -> 403 forbidden_role
/// до читання тіла). Окремий ресурс від цін: ціни читає specialist, собівартість - ні. Відповіді Cache-Control: no-store;
/// чужий service/location -> 404. Суми не логуються.
/// </summary>
[ApiController]
[Route("api/beauty")]
[RequireModule("beauty_catalog")]
[Authorize(Policy = AuthPolicies.Staff)] // автентифікація; роль перевіряє ReminderRolesAttribute, щоб віддати {code: forbidden_role}
[ReminderRoles(Roles.Owner, Roles.Admin)]
[InboxNoStore]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public sealed class ServiceCostsController(ServiceCostService costs, ServiceCostThrottle throttle) : ControllerBase
{
    private const int BodyLimit = 2 * 1024;

    [HttpGet("service-costs")]
    [ProducesResponseType<ServiceCostListDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] bool includeInactive, CancellationToken ct) =>
        User.ToActor() is { } actor ? this.ToResult(await costs.ListAsync(actor, includeInactive, ct)) : Unauthorized();

    [HttpGet("services/{id:guid}/costs")]
    [ProducesResponseType<ServiceCostsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        User.ToActor() is { } actor ? this.ToResult(await costs.GetAsync(actor, id, ct)) : Unauthorized();

    /// <summary>404 service_not_found | location_not_found; 422 invalid_cost | invalid_master_percent; 429 rate_limited.</summary>
    [HttpPut("services/{id:guid}/costs")]
    [RequestSizeLimit(BodyLimit)]
    [ProducesResponseType<PutServiceCostResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Put(Guid id, [FromBody] PutServiceCostRequest request, CancellationToken ct)
    {
        if (User.ToActor() is not { } actor) return Unauthorized();
        if (!throttle.TryAcquire(actor.TenantId, actor.UserId)) return RateLimited();
        return this.ToResult(await costs.PutAsync(actor, id, request, ct));
    }

    /// <summary>Без locationId видаляється мережевий рядок. 404 service_not_found | location_not_found | cost_not_found.</summary>
    [HttpDelete("services/{id:guid}/costs")]
    [RequestSizeLimit(BodyLimit)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Delete(Guid id, [FromQuery] Guid? locationId, CancellationToken ct)
    {
        if (User.ToActor() is not { } actor) return Unauthorized();
        if (!throttle.TryAcquire(actor.TenantId, actor.UserId)) return RateLimited();
        return this.ToResult(await costs.DeleteAsync(actor, id, locationId, ct), _ => NoContent());
    }

    private IActionResult RateLimited()
    {
        Response.Headers.RetryAfter = "60";
        return StatusCode(StatusCodes.Status429TooManyRequests, new ApiError("rate_limited", "Too many requests. Try again later."));
    }
}
