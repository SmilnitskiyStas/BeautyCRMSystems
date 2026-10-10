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

    // ---------- вихідні дні закладу (§17, TASK-701) ----------

    /// <summary>
    /// Щотижневі вихідні (повна заміна). 409 has_appointments_on_closed_days + conflicts[] (без даних клієнта), якщо на нові
    /// вихідні є майбутні активні записи й не передано confirm=true; зміни тоді немає.
    /// </summary>
    [HttpPut("{id:guid}/closed-weekdays")]
    [Authorize(Policy = AuthPolicies.Management)]
    [RequestSizeLimit(16 * 1024)]
    [ProducesResponseType<LocationDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> SetClosedWeekdays(Guid id, [FromBody] ClosedWeekdaysRequest request, CancellationToken ct) =>
        User.ToActor() is { } actor ? this.ToResult(await locations.SetClosedWeekdaysAsync(actor, id, request, ct)) : Unauthorized();

    /// <summary>Закриття закладу (`reason` лише керівникам). Діапазон до 366 днів; за замовчуванням від сьогодні на 366 днів.</summary>
    [HttpGet("{id:guid}/closures")]
    [ProducesResponseType<IReadOnlyList<ClosureDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> ListClosures(Guid id, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct) =>
        User.ToActor() is { } actor ? this.ToResult(await locations.ListClosuresAsync(actor, id, from, to, ct)) : Unauthorized();

    [HttpPost("{id:guid}/closures")]
    [Authorize(Policy = AuthPolicies.Management)]
    [RequestSizeLimit(16 * 1024)]
    [ProducesResponseType<ClosureDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> AddClosure(Guid id, [FromBody] ClosureRequest request, CancellationToken ct) =>
        User.ToActor() is { } actor
            ? this.ToResult(await locations.AddClosureAsync(actor, id, request, ct), c => StatusCode(StatusCodes.Status201Created, c))
            : Unauthorized();

    [HttpDelete("{id:guid}/closures/{closureId:guid}")]
    [Authorize(Policy = AuthPolicies.Management)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteClosure(Guid id, Guid closureId, CancellationToken ct) =>
        User.ToActor() is { } actor
            ? this.ToResult(await locations.DeleteClosureAsync(actor, id, closureId, ct), _ => NoContent())
            : Unauthorized();
}
