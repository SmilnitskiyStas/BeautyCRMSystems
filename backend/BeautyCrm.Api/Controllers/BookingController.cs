using BeautyCrm.Api.Auth;
using BeautyCrm.Api.Infrastructure;
using BeautyCrm.Api.Tenancy;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyBooking;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BeautyCrm.Api.Controllers;

/// <summary>Слоти й записи. Контролер лише маршрутизує: уся логіка в Application/Features/BeautyBooking.</summary>
[ApiController]
[Route("api/beauty")]
[RequireModule("beauty_booking")]
[Authorize(Policy = AuthPolicies.Staff)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public sealed class BookingController(BookingService booking, CancellationService cancellation, AppointmentAccessService access) : ControllerBase
{
    private static readonly ApiError NotFoundBody = new("appointment_not_found", "Appointment not found.");

    private Actor? Actor => User.ToActor();

    /// <summary>Спеціаліст бачить лише власні записи: чужий запис = 404 (існування не розкривається).</summary>
    private async Task<IActionResult?> GuardAsync(Guid id, CancellationToken ct) =>
        Actor is not { } actor ? Unauthorized()
        : await access.CanAccessAsync(actor, id, ct) ? null : NotFound(NotFoundBody);

    /// <summary>Вільні слоти: тривалість береться з послуги, графік — по закладу; перетини не віддаються.</summary>
    [HttpGet("slots")]
    [ProducesResponseType<IReadOnlyList<FreeSlot>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Slots(
        [FromQuery] Guid locationId, [FromQuery] Guid? specialistId, [FromQuery] Guid serviceId, [FromQuery] DateOnly date,
        CancellationToken ct) =>
        Actor is not { } actor ? Unauthorized()
        : this.ToResult(await booking.GetSlotsAsync(locationId, AppointmentAccessService.EffectiveSpecialistId(actor, specialistId), serviceId, date, ct));

    /// <summary>Календар: записи за період (за замовчуванням — найближчі 7 днів).</summary>
    [HttpGet("appointments")]
    [ProducesResponseType<IReadOnlyList<AppointmentDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, [FromQuery] Guid? locationId, [FromQuery] Guid? specialistId,
        CancellationToken ct)
    {
        if (Actor is not { } actor) return Unauthorized();
        var start = from ?? new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero); // .Date без Kind дав би локальний зсув
        return Ok(await booking.ListAsync(start, to ?? start.AddDays(7), locationId,
            AppointmentAccessService.EffectiveSpecialistId(actor, specialistId), ct));
    }

    [HttpGet("appointments/{id:guid}")]
    [ProducesResponseType<AppointmentDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        await GuardAsync(id, ct) ?? this.ToResult(await booking.GetAsync(id, ct));

    /// <summary>201; 409 slot_unavailable при перетині; 422 валідація; 402 payment_failed.</summary>
    [HttpPost("appointments")]
    [ProducesResponseType<AppointmentDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Create([FromBody] CreateAppointmentRequest request, CancellationToken ct) =>
        Actor is not { } actor ? Unauthorized()
        : !AppointmentAccessService.CanCreateFor(actor, request.SpecialistId)
            ? StatusCode(StatusCodes.Status403Forbidden, new ApiError("forbidden", "Specialists can only book for themselves."))
            : this.ToResult(await booking.CreateAsync(request, ct), a => CreatedAtAction(nameof(Get), new { id = a.Id }, a));

    /// <summary>Перенос (startsAt) і/або статус (confirmed|completed|no_show).</summary>
    [HttpPatch("appointments/{id:guid}")]
    [ProducesResponseType<AppointmentDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Patch(Guid id, [FromBody] PatchAppointmentRequest request, CancellationToken ct) =>
        await GuardAsync(id, ct) ?? this.ToResult(await booking.PatchAsync(id, request, ct));

    /// <summary>Скасування; refundAmount рахується за налаштуваннями tenant-а (/settings/cancellation).</summary>
    [HttpPost("appointments/{id:guid}/cancel")]
    [ProducesResponseType<CancelResult>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct) =>
        await GuardAsync(id, ct) ?? this.ToResult(await cancellation.CancelAsync(id, ct));
}
