using System.Text.RegularExpressions;
using BeautyCrm.Application.Features.BeautyAuth;
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
}

public sealed partial class LocationService(ILocationStore store, TimeProvider clock)
{
    public const int MaxName = 200, MaxAddress = 500, MaxPhone = 32, MaxTimezone = 64;

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
