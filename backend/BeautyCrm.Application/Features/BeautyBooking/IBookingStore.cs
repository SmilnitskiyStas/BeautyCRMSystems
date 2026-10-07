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
    Task<IReadOnlyList<AppointmentDto>> ListAppointmentsAsync(
        DateTimeOffset from, DateTimeOffset to, Guid? locationId, Guid? specialistId, CancellationToken ct);
    /// <summary>Переносить запис і переплановує нагадування (reminderAt null = без нагадування).</summary>
    Task<StoreResult<AppointmentDto>> RescheduleAsync(Guid id, DateTimeOffset newStart, DateTimeOffset? reminderAt, CancellationToken ct);
    Task<AppointmentDto?> SetStatusAsync(Guid id, string status, CancellationToken ct);
    /// <summary>status = cancelled, cancelled_at, нагадування -> cancelled.</summary>
    Task<AppointmentDto?> MarkCancelledAsync(Guid id, DateTimeOffset at, CancellationToken ct);
    Task<PaymentRecord?> GetPaymentAsync(Guid appointmentId, CancellationToken ct);
    Task UpdatePaymentAsync(Guid paymentId, string status, string? providerPaymentId, DateTimeOffset? paidAt, CancellationToken ct);
}
