using BeautyCrm.Application.Features.BeautyBooking;

namespace BeautyCrm.Tests.Beauty;

/// <summary>In-memory IBookingStore; емулює exclusion constraint БД (перетин активних записів спеціаліста).</summary>
internal sealed class FakeBookingStore : IBookingStore
{
    public static readonly Guid Location = Guid.Parse("11111111-0000-0000-0000-000000000001");
    public static readonly Guid Specialist = Guid.Parse("22222222-0000-0000-0000-000000000001");
    public static readonly Guid Service90 = Guid.Parse("33333333-0000-0000-0000-000000000001");
    public static readonly Guid Client = Guid.Parse("44444444-0000-0000-0000-000000000001");

    public const string Mon9To18 = """{"mon":[{"from":"09:00","to":"18:00"}],"tue":[{"from":"09:00","to":"18:00"}]}""";

    public int ServiceDuration = 90;
    public decimal? Price = 1000m;
    public string Timezone = "UTC";
    public string? WorkingHours = Mon9To18;
    public bool SpecialistAtLocation = true;
    /// <summary>TASK-691: затверджені відсутності й призначені послуги (null = без обмежень).</summary>
    public List<AbsenceSpan>? Absences;
    public List<Guid>? AssignedServices;
    public List<PromotionRule> Promotions = [];
    public List<AppointmentDto> Appointments = [];
    public List<(Guid AppointmentId, DateTimeOffset At)> Reminders = [];
    public Dictionary<Guid, PaymentRecord> Payments = new();
    public bool ClientExists = true;

    public Task<ServiceInfo?> GetServiceAsync(Guid id, CancellationToken ct) =>
        Task.FromResult<ServiceInfo?>(id == Service90 ? new ServiceInfo(id, "Манікюр", ServiceDuration, true) : null);

    public Task<LocationInfo?> GetLocationAsync(Guid id, CancellationToken ct) =>
        Task.FromResult<LocationInfo?>(id == Location ? new LocationInfo(id, "Центр", Timezone, true) : null);

    public Task<IReadOnlyList<SpecialistSchedule>> GetSchedulesAsync(Guid locationId, Guid? specialistId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SpecialistSchedule>>(
            SpecialistAtLocation && locationId == Location && (specialistId is null || specialistId == Specialist)
                ? [new SpecialistSchedule(Specialist, Timezone, WorkingHours, Absences, AssignedServices)] : []);

    public Task<IReadOnlyList<(Guid SpecialistId, TimeRange Range)>> GetBusyAsync(
        IReadOnlyCollection<Guid> ids, DateTimeOffset from, DateTimeOffset to, Guid? exclude, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<(Guid, TimeRange)>>(Appointments
            .Where(a => ids.Contains(a.SpecialistId) && a.Status != "cancelled" && a.Id != exclude && a.StartsAt < to && a.EndsAt > from)
            .Select(a => (a.SpecialistId, new TimeRange(a.StartsAt, a.EndsAt))).ToList());

    public Task<decimal?> GetPriceAsync(Guid serviceId, Guid locationId, CancellationToken ct) => Task.FromResult(Price);
    public Task<IReadOnlyList<PromotionRule>> GetPromotionRulesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PromotionRule>>(Promotions);

    public Task<Guid?> ResolveClientAsync(ClientInput c, CancellationToken ct) =>
        Task.FromResult<Guid?>(ClientExists ? c.Id ?? Client : null);

    public Task<StoreResult<AppointmentDto>> AddAppointmentAsync(NewAppointment a, CancellationToken ct)
    {
        var end = a.StartsAt.AddMinutes(a.DurationMinutes);
        if (Appointments.Any(x => x.SpecialistId == a.SpecialistId && x.Status != "cancelled" && x.StartsAt < end && x.EndsAt > a.StartsAt))
            return Task.FromResult(StoreResult<AppointmentDto>.Overlap());
        var dto = new AppointmentDto(Guid.NewGuid(), a.LocationId, "Центр", a.SpecialistId, "Марина", a.ServiceId, "Манікюр",
            a.ClientId, "Клієнт", a.StartsAt, end, a.DurationMinutes, "pending", a.Source, a.PriceOriginal, a.PriceFinal,
            a.PromotionId, a.ReminderOption, a.PaymentMethod);
        Appointments.Add(dto);
        if (a.ReminderAt is { } r) Reminders.Add((dto.Id, r));
        if (a.Payment is { } p) Payments[dto.Id] = new PaymentRecord(Guid.NewGuid(), dto.Id, p.Amount, p.Method, "pending", null);
        return Task.FromResult(StoreResult<AppointmentDto>.Ok(dto));
    }

    public Task<AppointmentDto?> GetAppointmentAsync(Guid id, CancellationToken ct) =>
        Task.FromResult(Appointments.FirstOrDefault(a => a.Id == id));

    public Task<IReadOnlyList<AppointmentDto>> ListAppointmentsAsync(DateTimeOffset from, DateTimeOffset to, Guid? loc, Guid? spec, bool includeCancelled, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AppointmentDto>>(Appointments.Where(a => a.StartsAt >= from && a.StartsAt < to && (includeCancelled || a.Status != "cancelled")).ToList());

    public Task<StoreResult<AppointmentDto>> RescheduleAsync(Guid id, DateTimeOffset newStart, DateTimeOffset? reminderAt, CancellationToken ct)
    {
        var a = Appointments.First(x => x.Id == id);
        var end = newStart.AddMinutes(a.DurationMinutes);
        if (Appointments.Any(x => x.Id != id && x.SpecialistId == a.SpecialistId && x.Status != "cancelled" && x.StartsAt < end && x.EndsAt > newStart))
            return Task.FromResult(StoreResult<AppointmentDto>.Overlap());
        var moved = a with { StartsAt = newStart, EndsAt = end };
        Appointments[Appointments.IndexOf(a)] = moved;
        Reminders.RemoveAll(r => r.AppointmentId == id);
        if (reminderAt is { } r) Reminders.Add((id, r));
        return Task.FromResult(StoreResult<AppointmentDto>.Ok(moved));
    }

    public Task<AppointmentDto?> SetStatusAsync(Guid id, string status, CancellationToken ct) => Replace(id, a => a with { Status = status });

    public CancelOrigin? LastCancelOrigin { get; private set; }

    public Task<bool> TryClaimCancelAsync(Guid id, DateTimeOffset at, CancelOrigin origin, string? reason, CancellationToken ct)
    {
        lock (Appointments)
        {
            var a = Appointments.FirstOrDefault(x => x.Id == id);
            if (a is null || a.Status is not ("pending" or "confirmed")) return Task.FromResult(false);
            Appointments[Appointments.IndexOf(a)] = a with
            {
                Status = "cancelled", CancelledAt = at, CancelledBy = new CancelledByDto(origin.Type), CancelReason = reason,
            };
            LastCancelOrigin = origin;
            return Task.FromResult(true);
        }
    }

    public Task ReleaseCancelAsync(Guid id, string previousStatus, CancellationToken ct)
    {
        lock (Appointments)
        {
            var a = Appointments.FirstOrDefault(x => x.Id == id);
            if (a is { Status: "cancelled" })
                Appointments[Appointments.IndexOf(a)] = a with { Status = previousStatus, CancelledAt = null, CancelledBy = null, CancelReason = null };
        }
        return Task.CompletedTask;
    }

    public Task<AppointmentDto?> MarkCancelledAsync(Guid id, DateTimeOffset at, CancellationToken ct)
    {
        Reminders.RemoveAll(r => r.AppointmentId == id);
        return Replace(id, a => a with { Status = "cancelled", CancelledAt = a.CancelledAt ?? at, CancelledBy = a.CancelledBy ?? new CancelledByDto("system") });
    }

    private Task<AppointmentDto?> Replace(Guid id, Func<AppointmentDto, AppointmentDto> f)
    {
        var a = Appointments.FirstOrDefault(x => x.Id == id);
        if (a is null) return Task.FromResult<AppointmentDto?>(null);
        var n = f(a);
        Appointments[Appointments.IndexOf(a)] = n;
        return Task.FromResult<AppointmentDto?>(n);
    }

    public Task<PaymentRecord?> GetPaymentAsync(Guid appointmentId, CancellationToken ct) =>
        Task.FromResult(Payments.GetValueOrDefault(appointmentId));

    public Task UpdatePaymentAsync(Guid paymentId, string status, string? providerPaymentId, DateTimeOffset? paidAt, CancellationToken ct)
    {
        var kv = Payments.First(p => p.Value.Id == paymentId);
        Payments[kv.Key] = kv.Value with { Status = status, ProviderPaymentId = providerPaymentId ?? kv.Value.ProviderPaymentId };
        return Task.CompletedTask;
    }

    /// <summary>Готовий запис для тестів скасування.</summary>
    public AppointmentDto Seed(DateTimeOffset start, string status = "confirmed", string method = "card", decimal paid = 1000m, string paymentStatus = "paid")
    {
        var dto = new AppointmentDto(Guid.NewGuid(), Location, "Центр", Specialist, "Марина", Service90, "Манікюр", Client, "Клієнт",
            start, start.AddMinutes(90), 90, status, "online", paid, paid, null, "none", method);
        Appointments.Add(dto);
        if (paid > 0) Payments[dto.Id] = new PaymentRecord(Guid.NewGuid(), dto.Id, paid, method, paymentStatus, "prov_1");
        return dto;
    }
}

internal sealed class FakeClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class SpyPayments : IPaymentService
{
    public bool ChargeOk = true, RefundOk = true;
    public List<decimal> Charges = [];
    public List<decimal> Refunds = [];
    public List<Guid> RefundKeys = [];
    /// <summary>Викликається всередині RefundAsync (імітує паралельний cancel під час повернення коштів).</summary>
    public Func<Task>? DuringRefund;
    public Task<PaymentResult> ChargeAsync(Guid id, decimal amount, CancellationToken ct)
    {
        Charges.Add(amount);
        return Task.FromResult(ChargeOk ? new PaymentResult(true, "p1", null) : new PaymentResult(false, null, "card_declined"));
    }
    public async Task<RefundResult> RefundAsync(Guid id, decimal amount, CancellationToken ct)
    {
        Refunds.Add(amount);
        RefundKeys.Add(id);
        if (DuringRefund is not null) await DuringRefund();
        return RefundOk ? new RefundResult(true, "r1", null) : new RefundResult(false, null, "provider_down");
    }
}

internal sealed class FakeCancellationSettingsStore : ICancellationSettingsStore
{
    public CancellationSettings? Saved;
    public int Saves;
    public Task<CancellationSettings?> GetCancellationSettingsAsync(CancellationToken ct) => Task.FromResult(Saved);
    public Task SaveCancellationSettingsAsync(CancellationSettings s, CancellationToken ct)
    {
        Saved = s;
        Saves++;
        return Task.CompletedTask;
    }
}

internal static class FakeSettings
{
    /// <summary>Сервіс налаштувань; без аргументу — значення за замовчуванням (рядка немає).</summary>
    public static CancellationSettingsService Service(CancellationSettings? saved = null) =>
        new(new FakeCancellationSettingsStore { Saved = saved });
}
