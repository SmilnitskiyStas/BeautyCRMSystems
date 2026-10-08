namespace BeautyCrm.Application.Features.BeautyBooking;

// Значення status/source/reminder/paymentMethod — рядки контракту (beauty-contracts.md §2).
public static class BookingValues
{
    public static readonly string[] Sources = ["admin", "online", "telegram", "instagram"];
    public static readonly string[] Reminders = ["none", "1h", "2h"];
    public static readonly string[] PaymentMethods = ["card", "cash"];
    public static readonly string[] PatchableStatuses = ["confirmed", "completed", "no_show"];
}

/// <param name="Timezone">IANA-зона закладу (TASK-690): Label/startsAt слід показувати в ній, а не в зоні браузера.</param>
public sealed record FreeSlot(Guid SpecialistId, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Label,
    CancellationTerms? Cancellation = null, string? Timezone = null);

public sealed record ClientInput(Guid? Id, string? Name, string? Phone, string? Email, bool MarketingConsent = false);

public sealed record CreateAppointmentRequest(
    Guid LocationId, Guid SpecialistId, Guid ServiceId, DateTimeOffset StartsAt,
    ClientInput Client, string Reminder, string PaymentMethod, string? Source);

/// <summary>PATCH: перенос (StartsAt) і/або зміна статусу. Скасування — лише POST /cancel.</summary>
public sealed record PatchAppointmentRequest(DateTimeOffset? StartsAt, string? Status);

public sealed record AppointmentDto(
    Guid Id, Guid LocationId, string LocationName, Guid SpecialistId, string SpecialistName,
    Guid ServiceId, string ServiceName, Guid ClientId, string ClientName,
    DateTimeOffset StartsAt, DateTimeOffset EndsAt, int DurationMinutes, string Status, string Source,
    decimal PriceOriginal, decimal PriceFinal, Guid? PromotionId, string ReminderOption, string? PaymentMethod,
    CancellationTerms? Cancellation = null, string? Timezone = null);

public sealed record CancelResult(AppointmentDto Appointment, decimal RefundAmount, int RefundPercent, int FeePercent = 0);

// ---- дані для сервісу (порт IBookingStore) ----
public sealed record ServiceInfo(Guid Id, string Name, int DurationMinutes, bool IsActive);
public sealed record LocationInfo(Guid Id, string Name, string Timezone, bool IsActive);
public sealed record TimeRange(DateTimeOffset Start, DateTimeOffset End);
/// <summary>WorkingHoursJson: {"mon":[{"from":"09:00","to":"18:00"}], ...}; відсутній день = вихідний.</summary>
/// <summary>
/// Повні дні відсутності (включно, у часовій зоні закладу). Absences/ServiceIds = null — обмежень немає
/// (джерело даних їх не надає); ServiceIds порожній = майстру не призначено жодної послуги (TASK-691).
/// </summary>
public sealed record SpecialistSchedule(Guid SpecialistId, string Timezone, string? WorkingHoursJson,
    IReadOnlyList<AbsenceSpan>? Absences = null, IReadOnlyList<Guid>? ServiceIds = null)
{
    public bool Offers(Guid? serviceId) => serviceId is null || ServiceIds is null || ServiceIds.Contains(serviceId.Value);
    public bool IsAbsentOn(DateOnly localDate) => Absences?.Any(a => localDate >= a.From && localDate <= a.To) == true;
}
public sealed record AbsenceSpan(DateOnly From, DateOnly To);
public sealed record PaymentRecord(Guid Id, Guid AppointmentId, decimal Amount, string Method, string Status, string? ProviderPaymentId);

public sealed record NewPayment(string Method, decimal Amount);
public sealed record NewAppointment(
    Guid LocationId, Guid SpecialistId, Guid ServiceId, Guid ClientId, DateTimeOffset StartsAt, int DurationMinutes,
    string Source, decimal PriceOriginal, decimal PriceFinal, Guid? PromotionId, string ReminderOption,
    string PaymentMethod, DateTimeOffset? ReminderAt, NewPayment? Payment, PublicCreateOptions? Public = null);

/// <summary>Публічний запис (TASK-688): id наперед (токен виводиться з нього), хеші токена й Idempotency-Key.</summary>
public sealed record PublicCreateOptions(Guid AppointmentId, string TokenHash, string IdempotencyKeyHash, string RequestHash);

public sealed record StoreResult<T>(T? Value, bool Conflict, bool Duplicate = false, bool SpecialistBlocked = false)
{
    public static StoreResult<T> Ok(T value) => new(value, false);
    public static StoreResult<T> Overlap() => new(default, true);
    /// <summary>
    /// Під advisory-lock майстра виявлено, що день уже зайнято затвердженою відсутністю / послугу знято / майстра вимкнено
    /// (гонка з approve відсутності): запис не створюється, сервіс повертає 409 specialist_unavailable.
    /// </summary>
    public static StoreResult<T> Blocked() => new(default, false, false, true);
    /// <summary>Порушено унікальність Idempotency-Key (паралельний повтор).</summary>
    public static StoreResult<T> Duplicated() => new(default, false, true);
}
