using BeautyCrm.Api.Auth;
using BeautyCrm.Api.Infrastructure;
using BeautyCrm.Api.Tenancy;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyStaff;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BeautyCrm.Api.Controllers;

/// <summary>
/// Керування працівниками (§13): профілі, призначені послуги, графік, запрошення. Читання — увесь персонал
/// (specialist бачить довідник без телефону), зміни — лише owner/admin. Контролер лише маршрутизує.
/// </summary>
[ApiController]
[Route("api/beauty/specialists")]
[RequireModule("beauty_booking")]
[Authorize(Policy = AuthPolicies.Staff)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public sealed class StaffController(StaffService staff, StaffInviteService invites) : ControllerBase
{
    private const int SmallBody = 64 * 1024;

    [HttpGet]
    [ProducesResponseType<IReadOnlyList<SpecialistDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct) =>
        User.ToActor() is { } actor ? Ok(await staff.ListAsync(actor, ct)) : Unauthorized();

    [HttpGet("{id:guid}")]
    [ProducesResponseType<SpecialistDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        User.ToActor() is { } actor ? this.ToResult(await staff.GetAsync(actor, id, ct)) : Unauthorized();

    /// <summary>201 SpecialistDto; 422 валідація (invalid_name, invalid_working_hours, service_not_found, ...).</summary>
    [HttpPost]
    [Authorize(Policy = AuthPolicies.Management)]
    [RequestSizeLimit(SmallBody)]
    [ProducesResponseType<SpecialistDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Create([FromBody] CreateSpecialistRequest request, CancellationToken ct) =>
        User.ToActor() is { } actor
            ? this.ToResult(await staff.CreateAsync(actor, request, ct), s => CreatedAtAction(nameof(Get), new { id = s.Id }, s))
            : Unauthorized();

    /// <summary>Повна заміна name/phone/position; isActive = false деактивує (записи лишаються, слоти зникають).</summary>
    [HttpPut("{id:guid}")]
    [Authorize(Policy = AuthPolicies.Management)]
    [RequestSizeLimit(SmallBody)]
    [ProducesResponseType<SpecialistDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateSpecialistRequest request, CancellationToken ct) =>
        User.ToActor() is { } actor ? this.ToResult(await staff.UpdateAsync(actor, id, request, ct)) : Unauthorized();

    /// <summary>Повна заміна переліку призначених послуг.</summary>
    [HttpPut("{id:guid}/services")]
    [Authorize(Policy = AuthPolicies.Management)]
    [RequestSizeLimit(SmallBody)]
    [ProducesResponseType<SpecialistDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SetServices(Guid id, [FromBody] SetSpecialistServicesRequest request, CancellationToken ct) =>
        User.ToActor() is { } actor ? this.ToResult(await staff.SetServicesAsync(actor, id, request, ct)) : Unauthorized();

    /// <summary>Графік у закладі (формат §9); 422 invalid_working_hours.</summary>
    [HttpPut("{id:guid}/schedule")]
    [Authorize(Policy = AuthPolicies.Management)]
    [RequestSizeLimit(SmallBody)]
    [ProducesResponseType<SpecialistDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SetSchedule(Guid id, [FromBody] SetScheduleRequest request, CancellationToken ct) =>
        User.ToActor() is { } actor ? this.ToResult(await staff.SetScheduleAsync(actor, id, request, ct)) : Unauthorized();

    /// <summary>Додати заклад майстру (опційно з графіком). 200 SpecialistDto; 409 location_already_assigned; 404 location_not_found.</summary>
    [HttpPost("{id:guid}/locations")]
    [Authorize(Policy = AuthPolicies.Management)]
    [RequestSizeLimit(SmallBody)]
    [ProducesResponseType<SpecialistDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AssignLocation(Guid id, [FromBody] AssignLocationRequest request, CancellationToken ct) =>
        User.ToActor() is { } actor ? this.ToResult(await staff.AssignLocationAsync(actor, id, request, ct)) : Unauthorized();

    /// <summary>Прибрати заклад (графік зберігається неактивним). 409 has_future_appointments; 404 location_not_assigned.</summary>
    [HttpDelete("{id:guid}/locations/{locationId:guid}")]
    [Authorize(Policy = AuthPolicies.Management)]
    [ProducesResponseType<SpecialistDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RemoveLocation(Guid id, Guid locationId, CancellationToken ct) =>
        User.ToActor() is { } actor ? this.ToResult(await staff.RemoveLocationAsync(actor, id, locationId, ct)) : Unauthorized();

    /// <summary>
    /// Запрошення на вхід (існуючий потік invites, role=specialist); 201 + одноразовий токен (Cache-Control: no-store).
    /// Нове запрошення відкликає попереднє pending для цього профілю; 409 specialist_inactive для деактивованого.
    /// </summary>
    [HttpPost("{id:guid}/invite")]
    [Authorize(Policy = AuthPolicies.Management)]
    [RequestSizeLimit(SmallBody)]
    [ProducesResponseType<InviteCreatedDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Invite(Guid id, [FromBody] InviteSpecialistRequest request, CancellationToken ct) =>
        User.ToActor() is { } actor
            ? this.ToResult(await invites.InviteAsync(actor, id, request, ct), r => this.InviteCreated(r))
            : Unauthorized();
}

/// <summary>
/// Відсутності працівників (§13). Тип бачить увесь персонал; note — лише owner/admin і автор запиту.
/// Створення: керівник — будь-кому (approved), specialist — лише собі (requested).
/// </summary>
[ApiController]
[Route("api/beauty")]
[RequireModule("beauty_booking")]
[Authorize(Policy = AuthPolicies.Staff)]
[ServiceFilter(typeof(ActiveSpecialistFilter))] // specialist з деактивованим профілем -> 403 specialist_inactive
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public sealed class AbsencesController(AbsenceService absences, AbsenceThrottle throttle) : ControllerBase
{
    private const int SmallBody = 64 * 1024;

    [HttpGet("absences")]
    [ProducesResponseType<IReadOnlyList<AbsenceDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] Guid? specialistId, CancellationToken ct) =>
        User.ToActor() is { } actor ? this.ToResult(await absences.ListAsync(actor, from, to, specialistId, ct)) : Unauthorized();

    /// <summary>
    /// 201 AbsenceDto (+ conflicts[] для керівників); 409 absence_overlap; 403 для чужого профілю (specialist) і для
    /// деактивованого; 422 too_many_requests (ліміт активних requested); 429 rate_limited (ліміт на користувача).
    /// </summary>
    [HttpPost("specialists/{id:guid}/absences")]
    [RequestSizeLimit(SmallBody)]
    [ProducesResponseType<AbsenceDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Create(Guid id, [FromBody] CreateAbsenceRequest request, CancellationToken ct)
    {
        if (User.ToActor() is not { } actor) return Unauthorized();
        if (!throttle.TryAcquire(actor.TenantId, actor.UserId))
            return StatusCode(StatusCodes.Status429TooManyRequests, new ApiError("rate_limited", "Too many requests. Try again later."));
        return this.ToResult(await absences.CreateAsync(actor, id, request, ct), a => StatusCode(StatusCodes.Status201Created, a));
    }

    [HttpPost("absences/{id:guid}/approve")]
    [Authorize(Policy = AuthPolicies.Management)]
    [ProducesResponseType<AbsenceDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Approve(Guid id, CancellationToken ct) =>
        User.ToActor() is { } actor ? this.ToResult(await absences.ApproveAsync(actor, id, ct)) : Unauthorized();

    [HttpPost("absences/{id:guid}/reject")]
    [Authorize(Policy = AuthPolicies.Management)]
    [ProducesResponseType<AbsenceDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Reject(Guid id, CancellationToken ct) =>
        User.ToActor() is { } actor ? this.ToResult(await absences.RejectAsync(actor, id, ct)) : Unauthorized();

    /// <summary>Керівник або автор запиту (поки він requested); чужа відсутність для не-керівника = 404.</summary>
    [HttpPost("absences/{id:guid}/cancel")]
    [ProducesResponseType<AbsenceDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct) =>
        User.ToActor() is { } actor ? this.ToResult(await absences.CancelAsync(actor, id, ct)) : Unauthorized();
}
