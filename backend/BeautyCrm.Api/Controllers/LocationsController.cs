using BeautyCrm.Api.Auth;
using BeautyCrm.Api.Infrastructure;
using BeautyCrm.Api.Tenancy;
using BeautyCrm.Application.Features.BeautyLocations;
using BeautyCrm.Application.Features.BeautyOverview;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BeautyCrm.Api.Controllers;

/// <summary>
/// Заклади (§16, TASK-697): читають усі ролі персоналу, змінюють owner/admin. Видалення немає — лише деактивація.
/// Контролер лише маршрутизує: правила (унікальність імені, timezone, has_future_appointments) — у Application/BeautyLocations.
/// </summary>
[ApiController]
[Route("api/beauty/locations")]
[RequireModule("beauty_booking")]
[Authorize(Policy = AuthPolicies.Staff)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public sealed class LocationsController(LocationService locations) : ControllerBase
{
    /// <summary>За замовчуванням лише активні; `includeInactive=true` враховується тільки для owner/admin.</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<LocationDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] bool includeInactive = false, CancellationToken ct = default) =>
        User.ToActor() is { } actor ? Ok(await locations.ListAsync(actor, includeInactive, ct)) : Unauthorized();

    [HttpPost]
    [Authorize(Policy = AuthPolicies.Management)]
    [RequestSizeLimit(16 * 1024)]
    [ProducesResponseType<LocationDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Create([FromBody] LocationRequest request, CancellationToken ct) =>
        User.ToActor() is { } actor
            ? this.ToResult(await locations.CreateAsync(actor, request, ct), l => StatusCode(StatusCodes.Status201Created, l))
            : Unauthorized();

    [HttpPut("{id:guid}")]
    [Authorize(Policy = AuthPolicies.Management)]
    [RequestSizeLimit(16 * 1024)]
    [ProducesResponseType<LocationDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Update(Guid id, [FromBody] LocationRequest request, CancellationToken ct) =>
        User.ToActor() is { } actor ? this.ToResult(await locations.UpdateAsync(actor, id, request, ct)) : Unauthorized();
}
