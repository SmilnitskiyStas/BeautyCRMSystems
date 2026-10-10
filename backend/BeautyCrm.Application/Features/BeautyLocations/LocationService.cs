using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyCommon;
using BeautyCrm.Application.Features.BeautyOverview;
using BeautyCrm.Application.Features.BeautyStaff;

namespace BeautyCrm.Application.Features.BeautyLocations;

/// <summary>POST/PUT /locations (§16). IsActive: у PUT пропущено = без змін; у POST ігнорується (новий заклад активний).</summary>
public sealed record LocationRequest(string? Name, string? Address, string? Phone, string? Timezone, bool? IsActive = null);

/// <summary>Нормалізований (обрізаний, перевірений) вміст запису закладу.</summary>
public sealed record LocationChange(string Name, string? Address, string? Phone, string Timezone, bool? IsActive);

public enum LocationWriteOutcome { Ok, NotFound, NameTaken, HasFutureAppointments, TimezoneLocked }

public sealed record LocationWriteResult(LocationWriteOutcome Outcome, LocationDto? Location = null);

// ---- вихідні дні закладу (TASK-701, §17) ----

/// <summary>PUT /locations/{id}/closed-weekdays. Confirm=true застосовує зміну попри майбутні записи на нових вихідних.</summary>
public sealed record ClosedWeekdaysRequest(IReadOnlyList<string>? ClosedWeekdays, bool? Confirm = null);

/// <summary>POST /locations/{id}/closures: повні дні в зоні закладу, включно.</summary>
public sealed record ClosureRequest(DateOnly? DateFrom, DateOnly? DateTo, string? Reason, bool? Confirm = null);

/// <param name="Reason">Лише для owner/admin (specialist поле не бачить).</param>
public sealed record ClosureDto(Guid Id, DateOnly DateFrom, DateOnly DateTo,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason = null);

/// <summary>Запис, що потрапляє на день, який стає вихідним (БЕЗ даних клієнта).</summary>
public sealed record ClosureConflictDto(Guid AppointmentId, DateTimeOffset StartsAt, string ServiceName, string SpecialistName);

public sealed record NewClosure(DateOnly DateFrom, DateOnly DateTo, string? Reason);

public enum ClosureWriteOutcome { Ok, NotFound, NeedsConfirmation, Overlap }

public sealed record ClosedWeekdaysWriteResult(
    ClosureWriteOutcome Outcome, LocationDto? Location = null, IReadOnlyList<ClosureConflictDto>? Conflicts = null);

public sealed record ClosureWriteResult(
    ClosureWriteOutcome Outcome, ClosureDto? Closure = null, IReadOnlyList<ClosureConflictDto>? Conflicts = null);

/// <summary>
/// Порт даних закладів (§16). Перевірки унікальності імені та «майбутніх активних записів» виконує реалізація атомарно
/// (advisory-lock + блокування рядка закладу), щоб паралельний запис не розминувся з деактивацією/зміною часової зони.
/// </summary>
public interface ILocationStore
{
    Task<IReadOnlyList<LocationDto>> ListAsync(bool includeInactive, CancellationToken ct);
    Task<LocationWriteResult> CreateAsync(LocationChange change, CancellationToken ct);
    /// <param name="now">Межа «майбутніх» записів: pending/confirmed із starts_at &gt; now.</param>
    Task<LocationWriteResult> UpdateAsync(Guid id, LocationChange change, DateTimeOffset now, CancellationToken ct);

    Task<LocationDto?> GetAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Атомарно (блокування рядка закладу FOR UPDATE, як у TASK-697): нові вихідні дні тижня без confirm і з майбутніми
    /// активними записами на них -> NeedsConfirmation + conflicts, зміни немає.
    /// </summary>
    Task<ClosedWeekdaysWriteResult> SetClosedWeekdaysAsync(
        Guid id, IReadOnlyList<string> weekdays, bool confirm, DateTimeOffset now, CancellationToken ct);

    /// <summary>Закриття, що перетинають [from, to].</summary>
    Task<IReadOnlyList<ClosureDto>> ListClosuresAsync(Guid locationId, DateOnly from, DateOnly to, CancellationToken ct);

    /// <summary>Те саме блокування рядка закладу; перетин із наявним закриттям -> Overlap (раніше за підтвердження).</summary>
    Task<ClosureWriteResult> AddClosureAsync(
        Guid locationId, NewClosure closure, bool confirm, Guid? createdByUserId, DateTimeOffset now, CancellationToken ct);

    Task<bool> DeleteClosureAsync(Guid locationId, Guid closureId, CancellationToken ct);
}

public sealed partial class LocationService(ILocationStore store, TimeProvider clock)
{
    public const int MaxName = 200, MaxAddress = 500, MaxPhone = 32, MaxTimezone = 64;
    public const int MaxClosureReason = 200, MaxClosureDays = 366;

    private static readonly string[] Weekdays = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];

    // IANA: Area/Location[/Sub] або UTC. Windows-ідентифікатори ("FLE Standard Time") не приймаємо — на Linux їх немає.
    [GeneratedRegex(@"^(UTC|[A-Za-z]+(/[A-Za-z0-9_+\-]+)+)$")]
    private static partial Regex IanaShape();

    /// <summary>Керівники бачать усе й можуть просити неактивні; specialist лише читає (includeInactive ігнорується).</summary>
    public async Task<IReadOnlyList<LocationDto>> ListAsync(Actor actor, bool includeInactive, CancellationToken ct) =>
        await store.ListAsync(includeInactive && StaffRoles.IsManager(actor), ct);

    public async Task<Result<LocationDto>> CreateAsync(Actor actor, LocationRequest req, CancellationToken ct)
    {
        if (!StaffRoles.IsManager(actor)) return Forbidden;
        var change = Validate(req);
        if (!change.IsOk) return change.Error!;
        return Map(await store.CreateAsync(change.Value! with { IsActive = true }, ct));
    }

    public async Task<Result<LocationDto>> UpdateAsync(Actor actor, Guid id, LocationRequest req, CancellationToken ct)
    {
        if (!StaffRoles.IsManager(actor)) return Forbidden;
        var change = Validate(req);
        if (!change.IsOk) return change.Error!;
        return Map(await store.UpdateAsync(id, change.Value!, clock.GetUtcNow(), ct));
    }

    // ---------- вихідні дні (§17) ----------

    public async Task<Result<LocationDto>> SetClosedWeekdaysAsync(Actor actor, Guid id, ClosedWeekdaysRequest req, CancellationToken ct)
    {
        if (!StaffRoles.IsManager(actor)) return Forbidden;
        if (req.ClosedWeekdays is null) return Error.Validation("invalid_closed_weekdays", "closedWeekdays is required.");
        var set = new HashSet<string>();
        foreach (var raw in req.ClosedWeekdays)
        {
            var d = raw?.Trim().ToLowerInvariant();
            if (d is null || !Weekdays.Contains(d))
                return Error.Validation("invalid_closed_weekdays", "closedWeekdays must contain only mon, tue, wed, thu, fri, sat, sun.");
            set.Add(d);
        }
        var ordered = Weekdays.Where(set.Contains).ToList(); // канонічний порядок тижня, без дублікатів

        var r = await store.SetClosedWeekdaysAsync(id, ordered, req.Confirm == true, clock.GetUtcNow(), ct);
        return r.Outcome switch
        {
            ClosureWriteOutcome.Ok => r.Location!,
            ClosureWriteOutcome.NotFound => LocationNotFound,
            _ => ClosedDayConflict(r.Conflicts),
        };
    }

    /// <summary>Закриття закладу в діапазоні (за замовч. від сьогодні в зоні закладу на 366 днів). Причину бачать лише керівники.</summary>
    public async Task<Result<IReadOnlyList<ClosureDto>>> ListClosuresAsync(
        Actor actor, Guid locationId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        if (await store.GetAsync(locationId, ct) is not { } location) return LocationNotFound;
        var start = from ?? SlotCalculator.LocalDate(clock.GetUtcNow(), location.Timezone);
        var end = to ?? start.AddDays(MaxClosureDays - 1);
        if (end < start || end.DayNumber - start.DayNumber + 1 > MaxClosureDays)
            return Error.Validation("invalid_range", $"from..to must be a valid range of at most {MaxClosureDays} days.");
        var rows = await store.ListClosuresAsync(locationId, start, end, ct);
        return Result<IReadOnlyList<ClosureDto>>.Ok(
            StaffRoles.IsManager(actor) ? rows : rows.Select(c => c with { Reason = null }).ToList());
    }

    public async Task<Result<ClosureDto>> AddClosureAsync(Actor actor, Guid locationId, ClosureRequest req, CancellationToken ct)
    {
        if (!StaffRoles.IsManager(actor)) return Forbidden;
        if (await store.GetAsync(locationId, ct) is not { } location) return LocationNotFound;

        if (req.DateFrom is not { } from || req.DateTo is not { } to)
            return Error.Validation("invalid_dates", "dateFrom and dateTo are required.");
        var today = SlotCalculator.LocalDate(clock.GetUtcNow(), location.Timezone);
        if (to < from || to.DayNumber - from.DayNumber + 1 > MaxClosureDays || from < today.AddYears(-1) || to > today.AddYears(2))
            return Error.Validation("invalid_dates",
                $"dateTo must not precede dateFrom; the period is at most {MaxClosureDays} days, not older than a year and not later than 2 years ahead.");
        var reason = string.IsNullOrWhiteSpace(req.Reason) ? null : req.Reason.Trim();
        if (reason is { Length: > MaxClosureReason })
            return Error.Validation("invalid_reason", $"Reason must be at most {MaxClosureReason} characters.");

        var r = await store.AddClosureAsync(locationId, new NewClosure(from, to, reason), req.Confirm == true, actor.UserId, clock.GetUtcNow(), ct);
        return r.Outcome switch
        {
            ClosureWriteOutcome.Ok => r.Closure!,
            ClosureWriteOutcome.NotFound => LocationNotFound,
            ClosureWriteOutcome.Overlap => Error.Conflict("closure_overlap", "The period overlaps an existing closure of this location."),
            _ => ClosedDayConflict(r.Conflicts),
        };
    }

    public async Task<Result<bool>> DeleteClosureAsync(Actor actor, Guid locationId, Guid closureId, CancellationToken ct)
    {
        if (!StaffRoles.IsManager(actor)) return Forbidden;
        return await store.DeleteClosureAsync(locationId, closureId, ct)
            ? true
            : Error.NotFound("closure_not_found", "Closure not found.");
    }

    private static readonly Error LocationNotFound = Error.NotFound("location_not_found", "Location not found.");

    private static Error ClosedDayConflict(IReadOnlyList<ClosureConflictDto>? conflicts) => Error.ConflictWith(
        "has_appointments_on_closed_days",
        "There are upcoming pending or confirmed appointments on the days that become closed; move or cancel them, or confirm the change.",
        conflicts ?? []);

    private static readonly Error Forbidden = Error.Forbidden("forbidden_role", "Only owner or admin can manage locations.");

    private static Result<LocationDto> Map(LocationWriteResult r) => r.Outcome switch
    {
        LocationWriteOutcome.Ok => r.Location!,
        LocationWriteOutcome.NotFound => Error.NotFound("location_not_found", "Location not found."),
        LocationWriteOutcome.NameTaken => Error.Conflict("location_name_taken", "A location with this name already exists."),
        LocationWriteOutcome.HasFutureAppointments => Error.Conflict("has_future_appointments",
            "The location has upcoming pending or confirmed appointments; move or cancel them first."),
        _ => Error.Conflict("timezone_locked", "The time zone cannot be changed while the location has upcoming pending or confirmed appointments."),
    };

    private static Result<LocationChange> Validate(LocationRequest req)
    {
        var name = req.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > MaxName)
            return Error.Validation("invalid_name", $"Name is required (max {MaxName}).");
        var address = string.IsNullOrWhiteSpace(req.Address) ? null : req.Address.Trim();
        if (address is { Length: > MaxAddress }) return Error.Validation("invalid_address", $"Address is too long (max {MaxAddress}).");
        var phone = string.IsNullOrWhiteSpace(req.Phone) ? null : req.Phone.Trim();
        if (phone is { Length: > MaxPhone }) return Error.Validation("invalid_phone", $"Phone is too long (max {MaxPhone}).");
        var tz = req.Timezone?.Trim();
        if (!IsValidTimezone(tz)) return Error.Validation("invalid_timezone", "Timezone must be a valid IANA zone, e.g. Europe/Kyiv.");
        return new LocationChange(name, address, phone, tz!, req.IsActive);
    }

    /// <summary>Валідна IANA-зона, яку знає середовище виконання (TimeZoneInfo).</summary>
    public static bool IsValidTimezone(string? tz)
    {
        if (string.IsNullOrEmpty(tz) || tz.Length > MaxTimezone || !IanaShape().IsMatch(tz)) return false;
        try { TimeZoneInfo.FindSystemTimeZoneById(tz); return true; }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { return false; }
    }
}
