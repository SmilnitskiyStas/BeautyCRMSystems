namespace BeautyCrm.Application.Features.BeautyBooking;

/// <summary>Порт даних для запису/слотів. Реалізація — Infrastructure (EF Core, RLS за tenant).</summary>
public interface IBookingStore
{
    Task<ServiceInfo?> GetServiceAsync(Guid serviceId, CancellationToken ct);
    Task<LocationInfo?> GetLocationAsync(Guid locationId, CancellationToken ct);
    Task<IReadOnlyList<SpecialistSchedule>> GetSchedulesAsync(Guid locationId, Guid? specialistId, CancellationToken ct);
    /// <summary>Активні записи спеціалістів (по всіх закладах), що перетинають [from, to).</summary>
    Task<IReadOnlyList<(Guid SpecialistId, TimeRange Range)>> GetBusyAsync(
        IReadOnlyCollection<Guid> specialistIds, DateTimeOffset from, DateTimeOffset to, Guid? excludeAppointmentId, CancellationToken ct);
    /// <summary>Ціна закладу (override) або мережева.</summary>
    Task<decimal?> GetPriceAsync(Guid serviceId, Guid locationId, CancellationToken ct);
    Task<IReadOnlyList<PromotionRule>> GetPromotionRulesAsync(CancellationToken ct);
    /// <summary>Існуючий клієнт за id/телефоном або новий. Null — переданий id не знайдено.</summary>
    Task<Guid?> ResolveClientAsync(ClientInput client, CancellationToken ct);
    /// <summary>Атомарно: запис + нагадування + платіж. Overlap (exclusion constraint) -> Conflict.</summary>
    Task<StoreResult<AppointmentDto>> AddAppointmentAsync(NewAppointment appointment, CancellationToken ct);
    Task<AppointmentDto?> GetAppointmentAsync(Guid id, CancellationToken ct);
    /// <param name="includeCancelled">false (за замовч. для календаря, §16) — без скасованих; записи лишаються в БД.</param>
    Task<IReadOnlyList<AppointmentDto>> ListAppointmentsAsync(
        DateTimeOffset from, DateTimeOffset to, Guid? locationId, Guid? specialistId, bool includeCancelled, CancellationToken ct);
    /// <summary>Переносить запис і переплановує нагадування (reminderAt null = без нагадування).</summary>
    Task<StoreResult<AppointmentDto>> RescheduleAsync(Guid id, DateTimeOffset newStart, DateTimeOffset? reminderAt, CancellationToken ct);
    Task<AppointmentDto?> SetStatusAsync(Guid id, string status, CancellationToken ct);
    /// <summary>
    /// Атомарний claim скасування (умовний UPDATE ... WHERE status IN ('pending','confirmed')): true лише для ОДНОГО з паралельних
    /// викликів. Виконується ПЕРЕД поверненням коштів, щоб гонка двох cancel не повернула кошти двічі.
    /// </summary>
    /// <remarks>У тому ж UPDATE фіксується, хто скасував (cancelled_by_*), і причина (§16).</remarks>
    Task<bool> TryClaimCancelAsync(Guid id, DateTimeOffset at, CancelOrigin origin, string? reason, CancellationToken ct);
    /// <summary>Компенсація невдалого повернення коштів: status = previousStatus, cancelled_at і cancelled_by_*/reason = null (лише якщо ще cancelled).</summary>
    Task ReleaseCancelAsync(Guid id, string previousStatus, CancellationToken ct);
    /// <summary>status = cancelled, cancelled_at, нагадування -> cancelled. Якщо автор ще не зафіксований — system.</summary>
    Task<AppointmentDto?> MarkCancelledAsync(Guid id, DateTimeOffset at, CancellationToken ct);
    Task<PaymentRecord?> GetPaymentAsync(Guid appointmentId, CancellationToken ct);
    Task UpdatePaymentAsync(Guid paymentId, string status, string? providerPaymentId, DateTimeOffset? paidAt, CancellationToken ct);
}
