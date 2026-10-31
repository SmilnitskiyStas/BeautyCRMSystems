using System.Text.Json.Serialization;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyReminders;
using BeautyCrm.Application.Features.BeautyStaff;

namespace BeautyCrm.Application.Features.BeautyBooking;

/// <summary>Хто скасував запис (TASK-697, §16): client (публічний токен) | staff (+ user id) | system (AI revert, збій оплати, backfill).</summary>
public static class CancelledByTypes
{
    public const string Client = "client";
    public const string Staff = "staff";
    public const string System = "system";
}

public sealed record CancelOrigin(string Type, Guid? UserId = null)
{
    public static readonly CancelOrigin Client = new(CancelledByTypes.Client);
    public static readonly CancelOrigin System = new(CancelledByTypes.System);
    public static CancelOrigin Staff(Guid userId) => new(CancelledByTypes.Staff, userId);
}

/// <summary>Необов'язкове тіло POST .../cancel (staff і публічний).</summary>
public sealed record CancelRequest(string? Reason = null);

/// <param name="Name">Ім'я користувача, що скасував (лише staff; лише керівникам).</param>
public sealed record CancelledByDto(string Type, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null);

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

/// <param name="PhoneSource">TASK-769 (§25.7): web_form (публічний запис) | staff; для нового клієнта. Наявний клієнт не змінюється.</param>
public sealed record ClientInput(Guid? Id, string? Name, string? Phone, string? Email, bool MarketingConsent = false, string? PhoneSource = null);

public sealed record CreateAppointmentRequest(
    Guid LocationId, Guid SpecialistId, Guid ServiceId, DateTimeOffset StartsAt,
    ClientInput Client, string? Reminder, string PaymentMethod, string? Source,
    decimal? ExpectedPriceFinal = null, // §21.6: розбіжність із quote -> 409 price_changed (запис не створюється)
    ReminderPlanRequest? Reminders = null,
    string? CouponCode = null); // §26.6: staff - необов'язковий код купона; публічний шлях і чат передають купон окремим параметром CouponUse // §22.6.1: явний план (staff); разом із Reminder -> 422 reminder_conflict; обидва null -> вподобання клієнта

/// <summary>PATCH: перенос (StartsAt) і/або зміна статусу. Скасування — лише POST /cancel.</summary>
public sealed record PatchAppointmentRequest(DateTimeOffset? StartsAt, string? Status);

public sealed record AppointmentDto(
    Guid Id, Guid LocationId, string LocationName, Guid SpecialistId, string SpecialistName,
    Guid ServiceId, string ServiceName, Guid ClientId, string ClientName,
    DateTimeOffset StartsAt, DateTimeOffset EndsAt, int DurationMinutes, string Status, string Source,
    decimal PriceOriginal, decimal PriceFinal, Guid? PromotionId, string ReminderOption, string? PaymentMethod,
    CancellationTerms? Cancellation = null, string? Timezone = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? CancelledAt = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CancelledByDto? CancelledBy = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CancelReason = null,
    AppointmentRemindersDto? Reminders = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? ClientConfirmedAt = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AppointmentCouponDto? Coupon = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? CampaignId = null)
{
    /// <summary>
    /// Видимість деталей скасування (§16): ім'я автора й причину бачать лише керівники; specialist бачить лише тип і час.
    /// </summary>
    public AppointmentDto ForViewer(Actor actor) => StaffRoles.IsManager(actor)
        ? this
        // TASK-790 (L8): codeHint купона (останні 3 символи коду) бачить лише owner/admin; майстру - порожній рядок (купон і результат лишаються)
        : this with
        {
            CancelledBy = CancelledBy is null ? null : new CancelledByDto(CancelledBy.Type), CancelReason = null,
            Coupon = Coupon is null ? null : Coupon with { CodeHint = "" },
        };
}

/// <summary>§22.6.1: план нагадувань запису. Points - лише для staff-DTO (публічний бачить method + offsetsMinutes).</summary>
public sealed record AppointmentRemindersDto(
    string Method, IReadOnlyList<int> OffsetsMinutes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ReminderPointDto>? Points = null);

/// <param name="Kind">message | call.</param>
/// <param name="Status">message: scheduled | sent | failed | skipped; call: open | done | skipped.</param>
public sealed record ReminderPointDto(
    string Kind, int OffsetMinutes, DateTimeOffset DueAt, string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SkipReason = null);

public sealed record CancelResult(AppointmentDto Appointment, decimal RefundAmount, int RefundPercent, int FeePercent = 0)
{
    public CancelResult ForViewer(Actor actor) => this with { Appointment = Appointment.ForViewer(actor) };
}

// ---- дані для сервісу (порт IBookingStore) ----
public sealed record ServiceInfo(Guid Id, string Name, int DurationMinutes, bool IsActive);
public sealed record LocationInfo(Guid Id, string Name, string Timezone, bool IsActive);
public sealed record TimeRange(DateTimeOffset Start, DateTimeOffset End);
/// <summary>WorkingHoursJson: {"mon":[{"from":"09:00","to":"18:00"}], ...}; відсутній день = вихідний.</summary>
/// <summary>
/// Повні дні відсутності (включно, у часовій зоні закладу). Absences/ServiceIds = null — обмежень немає
/// (джерело даних їх не надає); ServiceIds порожній = майстру не призначено жодної послуги (TASK-691).
/// </summary>
/// <remarks>
/// ClosedWeekdays/Closures — вихідні закладу (TASK-701, §17): день закритий, якщо його день тижня (mon..sun) у ClosedWeekdays
/// або дата потрапляє в Closures; закритий день перекриває графік майстра. null = обмежень немає.
/// LocationHoursJson — години роботи закладу (TASK-703, §18.1, формат §9): ефективний час = графік ∩ години закладу;
/// null/порожньо = години не задано, обмеження немає.
/// </remarks>
public sealed record SpecialistSchedule(Guid SpecialistId, string Timezone, string? WorkingHoursJson,
    IReadOnlyList<AbsenceSpan>? Absences = null, IReadOnlyList<Guid>? ServiceIds = null,
    IReadOnlyList<string>? ClosedWeekdays = null, IReadOnlyList<AbsenceSpan>? Closures = null, string? LocationHoursJson = null)
{
    public bool HasLocationHours => !string.IsNullOrWhiteSpace(LocationHoursJson);
    public bool Offers(Guid? serviceId) => serviceId is null || ServiceIds is null || ServiceIds.Contains(serviceId.Value);
    public bool IsAbsentOn(DateOnly localDate) => Absences?.Any(a => localDate >= a.From && localDate <= a.To) == true;
    public bool IsClosedOn(DateOnly localDate) =>
        ClosedWeekdays?.Contains(SlotCalculator.DayKey(localDate.DayOfWeek)) == true
        || Closures?.Any(c => localDate >= c.From && localDate <= c.To) == true;
}
public sealed record AbsenceSpan(DateOnly From, DateOnly To);
public sealed record PaymentRecord(Guid Id, Guid AppointmentId, decimal Amount, string Method, string Status, string? ProviderPaymentId);

public sealed record NewPayment(string Method, decimal Amount);
public sealed record NewAppointment(
    Guid LocationId, Guid SpecialistId, Guid ServiceId, Guid ClientId, DateTimeOffset StartsAt, int DurationMinutes,
    string Source, decimal PriceOriginal, decimal PriceFinal, Guid? PromotionId, string ReminderOption,
    string PaymentMethod, DateTimeOffset? ReminderAt, NewPayment? Payment, PublicCreateOptions? Public = null,
    PlannedReminders? Plan = null, NewCouponRedemption? Coupon = null); // §22.4: план нагадувань (null = спадковий шлях: лише ReminderAt)

/// <summary>Публічний запис (TASK-688): id наперед (токен виводиться з нього), хеші токена й Idempotency-Key. TokenHash = null для запису з чату (§21.6).</summary>
public sealed record PublicCreateOptions(Guid AppointmentId, string? TokenHash, string IdempotencyKeyHash, string RequestHash);

public sealed record StoreResult<T>(T? Value, bool Conflict, bool Duplicate = false, bool SpecialistBlocked = false, bool LocationClosed = false,
    bool OutsideLocationHours = false, bool AppointmentClosed = false, bool CouponUnavailable = false)
{
    /// <summary>CAS погашення купона не спрацював (вичерпано / строк / відкликано / повторне використання клієнтом, §26.6): запис не створено.</summary>
    public static StoreResult<T> CouponGone() => new(default, false, CouponUnavailable: true);
    /// <summary>Умовний UPDATE переходу в completed (§24.4) не знайшов запис у pending|confirmed: паралельне завершення/скасування - 409 appointment_closed.</summary>
    public static StoreResult<T> AlreadyClosed() => new(default, false, AppointmentClosed: true);
    /// <summary>Під блокуванням закладу виявлено, що день запису тепер закритий (вихідний/закриття) - 409 location_closed.</summary>
    public static StoreResult<T> Closed() => new(default, false, false, false, true);
    /// <summary>Під блокуванням закладу виявлено, що запис не вміщується в нові години закладу (§18.1) - 409 outside_location_hours.</summary>
    public static StoreResult<T> OutsideHours() => new(default, false, false, false, false, true);
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
