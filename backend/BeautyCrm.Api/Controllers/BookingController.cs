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
public sealed class BookingController(
    BookingService booking, CancellationService cancellation, AppointmentAccessService access, IClientScope clientScope,
    ILogger<BookingController> log) : ControllerBase
{
    private static readonly System.Diagnostics.Metrics.Meter AccessMeter = new("BeautyCrm.Access");
    private static readonly System.Diagnostics.Metrics.Counter<long> ScopeExpandedCounter =
        AccessMeter.CreateCounter<long>("beauty.access.client_scope_expanded", description: "appointments created for a client outside the actor's clients.view scope (phone match)");

    /// <summary>Максимальний діапазон GET /appointments (~2 місяці): календар місяця з запасом на межі тижнів.</summary>
    public const int MaxRangeDays = 62;

    private static readonly ApiError NotFoundBody = new("appointment_not_found", "Appointment not found.");

    private Actor? Actor => User.ToActor();

    /// <summary>
    /// M3 (§30.6.2): <c>client.id</c> приймається лише зі scope <c>clients.view</c> актора: <c>none</c> - ніколи, <c>own</c> - клієнти
    /// власних записів (без скасованих, створених самим актором). Поза scope - як неіснуючий: 404 client_not_found.
    /// </summary>
    private async Task<bool> ClientOutOfScopeAsync(Actor actor, CreateAppointmentRequest request, CancellationToken ct) =>
        request.Client.Id is { } clientId && !await clientScope.InScopeAsync(actor, clientId, ct);

    /// <summary>
    /// Scope календаря (§30.6.2): поза scope перегляду - 404 (існування не розкривається); видимий, але поза scope керування -
    /// 403 forbidden_role {permission: calendar.manage, required: all}.
    /// </summary>
    private async Task<IActionResult?> GuardAsync(Guid id, bool manage, CancellationToken ct)
    {
        if (Actor is not { } actor) return Unauthorized();
        return await access.CheckAsync(actor, id, manage, ct) switch
        {
            AppointmentAccess.NotFound => NotFound(NotFoundBody),
            AppointmentAccess.ManageForbidden => StatusCode(StatusCodes.Status403Forbidden, new
            {
                code = "forbidden_role", message = "This action is not available for your access level.",
                permission = PermissionKeys.CalendarManage, required = PermissionLevels.All,
            }),
            _ => null,
        };
    }

    /// <summary>Вільні слоти: тривалість береться з послуги, графік — по закладу; перетини не віддаються.</summary>
    [HttpGet("slots")]
    [RequirePermission(PermissionKeys.CalendarView, PermissionLevels.Own)]
    [ProducesResponseType<IReadOnlyList<FreeSlot>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Slots(
        [FromQuery] Guid locationId, [FromQuery] Guid? specialistId, [FromQuery] Guid serviceId, [FromQuery] DateOnly date,
        CancellationToken ct) =>
        Actor is not { } actor ? Unauthorized()
        : this.ToResult(await booking.GetSlotsAsync(locationId, AppointmentAccessService.EffectiveSpecialistId(actor, specialistId), serviceId, date, ct));

    /// <summary>
    /// Календар: записи за період (за замовчуванням — найближчі 7 днів, БЕЗ скасованих; `includeCancelled=true` повертає і їх, §16).
    /// Ім'я автора скасування та причину бачать лише керівники.
    /// </summary>
    [HttpGet("appointments")]
    [RequirePermission(PermissionKeys.CalendarView, PermissionLevels.Own)]
    [ProducesResponseType<IReadOnlyList<AppointmentDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, [FromQuery] Guid? locationId, [FromQuery] Guid? specialistId,
        CancellationToken ct, [FromQuery] bool includeCancelled = false)
    {
        if (Actor is not { } actor) return Unauthorized();
        var start = from ?? new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero); // .Date без Kind дав би локальний зсув
        var end = to ?? start.AddDays(7);
        // Межа діапазону (L8): без неї один запит вичитує весь журнал записів tenant-а.
        if (end <= start) return UnprocessableEntity(new ApiError("invalid_range", "'to' must be after 'from'."));
        if (end - start > TimeSpan.FromDays(MaxRangeDays))
            return UnprocessableEntity(new ApiError("range_too_large", $"The requested range must not exceed {MaxRangeDays} days."));
        return Ok((await booking.ListAsync(start, end, locationId,
            AppointmentAccessService.EffectiveSpecialistId(actor, specialistId), ct, includeCancelled)).Select(a => a.ForList(actor)).ToList());
    }

    [HttpGet("appointments/{id:guid}")]
    [RequirePermission(PermissionKeys.CalendarView, PermissionLevels.Own)]
    [ProducesResponseType<AppointmentDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        await GuardAsync(id, manage: false, ct) ?? this.ToResult(await booking.GetAsync(id, ct), a => Ok(a.ForViewer(Actor!)));

    /// <summary>201; 409 slot_unavailable при перетині; 422 валідація; 402 payment_failed; `couponCode?` (§26.6): 409 coupon_*, 422 invalid_coupon_format, 429.</summary>
    [HttpPost("appointments")]
    [RequirePermission(PermissionKeys.CalendarManage, PermissionLevels.Own)]
    [ProducesResponseType<AppointmentDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Create([FromBody] CreateAppointmentRequest request, CancellationToken ct)
    {
        if (Actor is not { } actor) return Unauthorized();
        if (!AppointmentAccessService.CanCreateFor(actor, request.SpecialistId))
            return StatusCode(StatusCodes.Status403Forbidden, new ApiError("forbidden", "Specialists can only book for themselves."));
        if (await ClientOutOfScopeAsync(actor, request, ct)) return NotFound(new ApiError("client_not_found", "Client not found."));

        // M3: збіг за телефоном із клієнтом поза scope clients.view не розкриває збережене ім'я (запис створюється, scope розширюється)
        Guid? expandedClient = null;
        if (request.Client.Id is null && await clientScope.MatchByPhoneAsync(request.Client.Phone, ct) is { } matched
            && !await clientScope.InScopeAsync(actor, matched, ct))
            expandedClient = matched;

        return this.ToResult(await booking.CreateAsync(request, ct, null,
                string.IsNullOrWhiteSpace(request.CouponCode) ? null : new BeautyCrm.Application.Features.BeautyCoupons.CouponUse(
                    BeautyCrm.Application.Features.BeautyCoupons.CouponScope.Staff, request.CouponCode, RateKey: actor.UserId.ToString()),
                actor.UserId),
            a =>
            {
                var view = a.ForViewer(actor);
                if (expandedClient is { } clientId)
                {
                    log.LogInformation("client_scope_expanded actorUserId={ActorUserId} clientId={ClientId} appointmentId={AppointmentId}",
                        actor.UserId, clientId, a.Id);
                    ScopeExpandedCounter.Add(1);
                    view = view with { ClientName = request.Client.Name?.Trim() ?? "" };
                }
                return CreatedAtAction(nameof(Get), new { id = a.Id }, view);
            });
    }

    /// <summary>Перенос (startsAt) і/або статус (confirmed|completed|no_show).</summary>
    [HttpPatch("appointments/{id:guid}")]
    [RequirePermission(PermissionKeys.CalendarManage, PermissionLevels.Own)]
    [ProducesResponseType<AppointmentDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Patch(Guid id, [FromBody] PatchAppointmentRequest request, CancellationToken ct) =>
        await GuardAsync(id, manage: true, ct) ?? this.ToResult(await booking.PatchAsync(id, request, ct), a => Ok(a.ForViewer(Actor!)));

    /// <summary>
    /// Скасування; refundAmount рахується за налаштуваннями tenant-а (/settings/cancellation). Необов'язкове тіло `{reason}` (до 300);
    /// фіксується хто (staff + user id) і коли скасував (§16).
    /// </summary>
    [HttpPost("appointments/{id:guid}/cancel")]
    [RequirePermission(PermissionKeys.CalendarManage, PermissionLevels.Own)]
    [RequestSizeLimit(16 * 1024)]
    [ProducesResponseType<CancelResult>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Cancel(
        Guid id, CancellationToken ct, [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] CancelRequest? request = null) =>
        await GuardAsync(id, manage: true, ct) ?? this.ToResult(
            await cancellation.CancelAsync(id, ct, CancelOrigin.Staff(Actor!.UserId), request?.Reason),
            r => Ok(r.ForViewer(Actor!)));
}
