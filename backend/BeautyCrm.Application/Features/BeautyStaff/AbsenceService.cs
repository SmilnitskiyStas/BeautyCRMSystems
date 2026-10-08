using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyCommon;

namespace BeautyCrm.Application.Features.BeautyStaff;

/// <summary>
/// Відсутності працівників. Керівник створює одразу approved; працівник — лише собі, статус requested.
/// Note — чутливі дані (не логується, віддається лише керівникам і автору). Затверджена відсутність блокує
/// нові слоти (SlotCalculator), наявні записи не чіпає — керівник отримує conflicts[] і вирішує сам.
/// </summary>
public sealed class AbsenceService(IStaffStore store, TimeProvider clock, StaffOptions? options = null)
{
    private static readonly Error NotFound = Error.NotFound("absence_not_found", "Absence not found.");
    private static readonly Error SpecialistNotFound = Error.NotFound("specialist_not_found", "Specialist not found.");
    private static readonly Error Overlap = Error.Conflict("absence_overlap", "The specialist already has an absence request or absence in this period.");
    private readonly int _maxRequested = (options ?? new StaffOptions()).MaxRequestedAbsencesPerSpecialist;
    private static readonly Error InactiveProfile = Error.Forbidden("specialist_inactive", "The specialist profile is deactivated.");
    private static readonly Error ManagersOnly = Error.Forbidden("forbidden", "Only owner or admin can do this.");

    // ---------- читання ----------

    public async Task<Result<IReadOnlyList<AbsenceDto>>> ListAsync(
        Actor actor, DateOnly? from, DateOnly? to, Guid? specialistId, CancellationToken ct)
    {
        var start = from ?? DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var end = to ?? start.AddDays(31);
        if (end < start || end.DayNumber - start.DayNumber > AbsenceValues.MaxSpanDays)
            return Error.Validation("invalid_range", $"from..to must be a valid range of at most {AbsenceValues.MaxSpanDays} days.");

        var manager = StaffRoles.IsManager(actor);
        var rows = await store.ListAbsencesAsync(start, end, specialistId, ct);
        // Мінімум розкриття: працівник бачить чужі лише затверджені відсутності (тип і дати), свої — усі.
        var visible = manager
            ? rows
            : rows.Where(a => a.Status == AbsenceValues.Approved || a.SpecialistId == actor.SpecialistId || a.RequestedByUserId == actor.UserId).ToList();
        return Result<IReadOnlyList<AbsenceDto>>.Ok(visible.Select(a => ToDto(actor, a)).ToList());
    }

    // ---------- створення ----------

    public async Task<Result<AbsenceDto>> CreateAsync(Actor actor, Guid specialistId, CreateAbsenceRequest req, CancellationToken ct)
    {
        var manager = StaffRoles.IsManager(actor);
        // IDOR: працівник створює запит лише собі; про існування чужого профілю не повідомляємо.
        if (!manager && (actor.Role != Roles.Specialist || actor.SpecialistId != specialistId))
            return Error.Forbidden("forbidden", "Specialists can only request absences for themselves.");

        var type = req.Type?.Trim().ToLowerInvariant();
        if (type is null || !AbsenceValues.Types.Contains(type))
            return Error.Validation("invalid_type", "Type must be sick, vacation, day_off or other.");
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        if (req.DateTo < req.DateFrom || req.DateTo.DayNumber - req.DateFrom.DayNumber + 1 > AbsenceValues.MaxSpanDays
            || req.DateFrom < today.AddDays(-AbsenceValues.MaxSpanDays) || req.DateTo > today.AddDays(2 * AbsenceValues.MaxSpanDays))
            return Error.Validation("invalid_dates", "dateTo must not precede dateFrom; the period is at most 366 days and within a sensible range.");
        var note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim();
        if (note is { Length: > AbsenceValues.NoteMaxLength })
            return Error.Validation("invalid_note", $"Note must be at most {AbsenceValues.NoteMaxLength} characters.");

        var link = await store.GetSpecialistLinkAsync(specialistId, ct);
        if (link is null) return SpecialistNotFound;
        if (manager && !StaffRoles.CanModify(actor, link.LinkedUserRole))
            return Error.Forbidden("forbidden_role", "You cannot manage this specialist.");
        // Деактивований профіль: працівник не діє від його імені (токен міг пережити деактивацію).
        if (!manager && !link.IsActive) return InactiveProfile;

        if (await store.HasActiveAbsenceOverlapAsync(specialistId, req.DateFrom, req.DateTo, ct)) return Overlap;

        var now = clock.GetUtcNow();
        var added = await store.AddAbsenceAsync(new NewAbsence(
            specialistId, type, req.DateFrom, req.DateTo, manager ? AbsenceValues.Approved : AbsenceValues.Requested, note,
            actor.UserId, manager ? actor.UserId : null, manager ? now : null),
            manager ? null : _maxRequested, manager ? Window(req.DateFrom, req.DateTo, now) : null, ct);
        if (added.Outcome == AbsenceAddOutcome.TooManyRequests)
            return Error.Validation("too_many_requests", $"A specialist can have at most {_maxRequested} pending absence requests; wait for a decision or cancel some.");
        if (added.Outcome != AbsenceAddOutcome.Ok || added.Record is null) return Overlap;

        var created = added.Record;
        return ToDto(actor, created, manager ? ConflictsOf(created) : null);
    }

    // ---------- рішення ----------

    public Task<Result<AbsenceDto>> ApproveAsync(Actor actor, Guid id, CancellationToken ct) =>
        DecideAsync(actor, id, AbsenceValues.Approved, withConflicts: true, ct);

    public Task<Result<AbsenceDto>> RejectAsync(Actor actor, Guid id, CancellationToken ct) =>
        DecideAsync(actor, id, AbsenceValues.Rejected, withConflicts: false, ct);

    private async Task<Result<AbsenceDto>> DecideAsync(Actor actor, Guid id, string target, bool withConflicts, CancellationToken ct)
    {
        if (!StaffRoles.IsManager(actor)) return ManagersOnly;
        var loaded = await LoadManagedAsync(actor, id, ct);
        if (loaded.Absence is not { } current) return loaded.Failure!;

        var now = clock.GetUtcNow();
        var updated = await store.TransitionAbsenceAsync(current.Id, [AbsenceValues.Requested], target, actor.UserId, now,
            withConflicts ? Window(current.DateFrom, current.DateTo, now) : null, ct);
        if (updated is null) return Error.Conflict("absence_not_pending", "Only a pending request can be approved or rejected.");
        return ToDto(actor, updated, withConflicts ? ConflictsOf(updated) : null);
    }

    /// <summary>Скасування: керівник (requested/approved) або автор запиту (лише поки requested).</summary>
    public async Task<Result<AbsenceDto>> CancelAsync(Actor actor, Guid id, CancellationToken ct)
    {
        var manager = StaffRoles.IsManager(actor);
        var current = await store.GetAbsenceAsync(id, ct);
        if (current is null) return NotFound;
        if (manager)
        {
            var link = await store.GetSpecialistLinkAsync(current.SpecialistId, ct);
            if (!StaffRoles.CanModify(actor, link?.LinkedUserRole)) return Error.Forbidden("forbidden_role", "You cannot manage this specialist.");
        }
        else
        {
            // чужа відсутність для не-керівника не існує (IDOR)
            if (current.RequestedByUserId != actor.UserId) return NotFound;
            if (current.Status != AbsenceValues.Requested)
                return Error.Forbidden("forbidden", "Only owner or admin can cancel an absence that is already decided.");
        }

        var updated = await store.TransitionAbsenceAsync(id,
            manager ? [AbsenceValues.Requested, AbsenceValues.Approved] : [AbsenceValues.Requested],
            AbsenceValues.Cancelled, actor.UserId, clock.GetUtcNow(), null, ct); // хто й коли скасував -> cancelled_by_user_id/cancelled_at
        return updated is null
            ? Error.Conflict("absence_closed", "The absence is already cancelled or rejected.")
            : ToDto(actor, updated);
    }

    // ---------- допоміжне ----------

    private sealed record Loaded(AbsenceRecord? Absence, Error? Failure);

    private async Task<Loaded> LoadManagedAsync(Actor actor, Guid id, CancellationToken ct)
    {
        var a = await store.GetAbsenceAsync(id, ct);
        if (a is null) return new Loaded(null, NotFound);
        var link = await store.GetSpecialistLinkAsync(a.SpecialistId, ct);
        return StaffRoles.CanModify(actor, link?.LinkedUserRole)
            ? new Loaded(a, null)
            : new Loaded(null, Error.Forbidden("forbidden_role", "You cannot manage this specialist."));
    }

    /// <summary>
    /// Вікно початків записів, які можуть потрапити на дні відсутності (запас ±1 доба на часові зони); минуле відсікається.
    /// Вибірку виконує store у тій самій транзакції, що й зміна відсутності (advisory-lock майстра).
    /// </summary>
    private static ConflictWindow Window(DateOnly from, DateOnly to, DateTimeOffset now)
    {
        var fromUtc = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddDays(-1);
        var toUtc = new DateTimeOffset(to.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddDays(2);
        return new ConflictWindow(fromUtc < now ? now : fromUtc, toUtc);
    }

    /// <summary>Кандидатів уточнюємо за ЛОКАЛЬНОЮ датою закладу.</summary>
    private static IReadOnlyList<AbsenceConflictDto> ConflictsOf(AbsenceRecord a) =>
        (a.Candidates ?? [])
            .Where(c => SlotCalculator.LocalDate(c.StartsAt, c.Timezone) is var d && d >= a.DateFrom && d <= a.DateTo)
            .OrderBy(c => c.StartsAt)
            .Select(c => new AbsenceConflictDto(c.AppointmentId, c.StartsAt, c.ServiceName))
            .ToList();

    /// <summary>Нотатку бачать лише керівники й автор запиту.</summary>
    private static AbsenceDto ToDto(Actor actor, AbsenceRecord a, IReadOnlyList<AbsenceConflictDto>? conflicts = null) => new(
        a.Id, a.SpecialistId, a.Type, a.DateFrom, a.DateTo, a.Status,
        StaffRoles.IsManager(actor) || a.RequestedByUserId == actor.UserId ? a.Note : null,
        conflicts);
}
