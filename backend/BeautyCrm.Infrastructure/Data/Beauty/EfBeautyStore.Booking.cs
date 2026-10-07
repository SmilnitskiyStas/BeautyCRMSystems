using BeautyCrm.Application.Features.BeautyAnalytics;
using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyCatalog;
using BeautyCrm.Application.Features.BeautyChannels;
using BeautyCrm.Application.Features.BeautyClients;
using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ent = BeautyCrm.Infrastructure.Data.Entities;

namespace BeautyCrm.Infrastructure.Data.Beauty;

/// <summary>
/// EF Core реалізація портів даних Application. Tenant ізоляція — RLS (app.tenant_id), тож запити
/// не фільтрують за tenant_id вручну. Один scoped екземпляр на запит.
/// </summary>
public sealed partial class EfBeautyStore(BeautyDbContext db, ISecretProtector secrets)
    : IBookingStore, ICancellationSettingsStore, ICatalogStore, IClientStore, IAnalyticsStore, IChannelSettingsStore
{
    private const string ExclusionViolation = "23P01";

    // ---------- довідники ----------

    public Task<ServiceInfo?> GetServiceAsync(Guid serviceId, CancellationToken ct) =>
        db.Services.AsNoTracking().Where(s => s.Id == serviceId)
            .Select(s => new ServiceInfo(s.Id, s.Name, s.DurationMinutes, s.IsActive)).FirstOrDefaultAsync(ct);

    public Task<LocationInfo?> GetLocationAsync(Guid locationId, CancellationToken ct) =>
        db.Locations.AsNoTracking().Where(l => l.Id == locationId)
            .Select(l => new LocationInfo(l.Id, l.Name, l.Timezone, l.IsActive)).FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<SpecialistSchedule>> GetSchedulesAsync(Guid locationId, Guid? specialistId, CancellationToken ct) =>
        await db.SpecialistLocations.AsNoTracking()
            .Where(sl => sl.LocationId == locationId && sl.IsActive && sl.Specialist!.IsActive
                         && (specialistId == null || sl.SpecialistId == specialistId))
            .Select(sl => new SpecialistSchedule(sl.SpecialistId, sl.Location!.Timezone, sl.WorkingHours))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<(Guid SpecialistId, TimeRange Range)>> GetBusyAsync(
        IReadOnlyCollection<Guid> specialistIds, DateTimeOffset from, DateTimeOffset to, Guid? excludeAppointmentId, CancellationToken ct)
    {
        var fromUtc = from.ToUniversalTime();
        var toUtc = to.ToUniversalTime();
        var rows = await db.Appointments.AsNoTracking()
            .Where(a => specialistIds.Contains(a.SpecialistId) && (a.Status == AppointmentStatus.Pending || a.Status == AppointmentStatus.Confirmed || a.Status == AppointmentStatus.Completed)
                        && a.StartsAt < toUtc && a.EndsAt > fromUtc && (excludeAppointmentId == null || a.Id != excludeAppointmentId))
            .Select(a => new { a.SpecialistId, a.StartsAt, a.EndsAt })
            .ToListAsync(ct);
        return rows.Select(r => (r.SpecialistId, new TimeRange(r.StartsAt, r.EndsAt))).ToList();
    }

    public async Task<decimal?> GetPriceAsync(Guid serviceId, Guid locationId, CancellationToken ct)
    {
        var prices = await db.ServicePrices.AsNoTracking()
            .Where(p => p.ServiceId == serviceId && (p.LocationId == locationId || p.LocationId == null))
            .Select(p => new { p.LocationId, p.Price }).ToListAsync(ct);
        return prices.FirstOrDefault(p => p.LocationId == locationId)?.Price ?? prices.FirstOrDefault(p => p.LocationId == null)?.Price;
    }

    public async Task<IReadOnlyList<PromotionRule>> GetPromotionRulesAsync(CancellationToken ct) =>
        (await LoadPromotionsAsync(null, activeOnly: true, ct))
            .Select(p => new PromotionRule(p.Id, p.Name, p.DiscountType, p.DiscountValue, p.StartsAt, p.EndsAt,
                p.IsActive, p.LocationIds, p.ServiceIds)).ToList();

    // ---------- клієнти для запису ----------

    public async Task<Guid?> ResolveClientAsync(ClientInput client, CancellationToken ct)
    {
        if (client.Id is { } id)
            return await db.Clients.AnyAsync(c => c.Id == id && c.DeletedAt == null, ct) ? id : null;

        var phone = client.Phone!.Trim();
        var existing = await db.Clients.Where(c => c.Phone == phone && c.DeletedAt == null)
            .Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);
        if (existing is not null) return existing;

        // Зберігається разом із записом одним SaveChanges (див. AddAppointmentAsync).
        var created = new Client
        {
            Id = Guid.NewGuid(), FullName = client.Name!.Trim(), Phone = phone, MarketingConsent = client.MarketingConsent,
            Email = string.IsNullOrWhiteSpace(client.Email) ? null : client.Email.Trim(),
        };
        db.Clients.Add(created);
        return created.Id;
    }

    // ---------- записи ----------

    public async Task<StoreResult<AppointmentDto>> AddAppointmentAsync(NewAppointment a, CancellationToken ct)
    {
        var id = a.Public?.AppointmentId ?? Guid.NewGuid();
        db.Appointments.Add(new Appointment
        {
            Id = id, LocationId = a.LocationId, SpecialistId = a.SpecialistId, ServiceId = a.ServiceId, ClientId = a.ClientId,
            StartsAt = a.StartsAt.ToUniversalTime(), DurationMinutes = a.DurationMinutes, Status = AppointmentStatus.Pending,
            Source = EnumText<AppointmentSource>.FromDb(a.Source), PriceOriginal = a.PriceOriginal, PriceFinal = a.PriceFinal,
            PromotionId = a.PromotionId, ReminderOption = EnumText<ReminderOption>.FromDb(a.ReminderOption),
            PaymentMethod = EnumText<Ent.PaymentMethod>.FromDb(a.PaymentMethod),
            PublicTokenHash = a.Public?.TokenHash, IdempotencyKeyHash = a.Public?.IdempotencyKeyHash, IdempotencyRequestHash = a.Public?.RequestHash,
        });
        if (a.ReminderAt is { } at)
            db.Reminders.Add(new Reminder { Id = Guid.NewGuid(), AppointmentId = id, ScheduledAt = at.ToUniversalTime() });
        if (a.Payment is { } p)
            db.Payments.Add(new Payment
            {
                Id = Guid.NewGuid(), AppointmentId = id, Amount = p.Amount, Method = EnumText<Ent.PaymentMethod>.FromDb(p.Method),
            });

        // Публічний запис: повтори одного Idempotency-Key серіалізуються advisory-локом у транзакції вставки
        // (інакше unique-індекс ключа й exclusion-обмеження слоту взаємно блокуються -> deadlock 40P01).
        await using var tx = a.Public is null ? null : await db.Database.BeginTransactionAsync(ct);
        if (tx is not null)
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({a.Public!.IdempotencyKeyHash}, 0))", ct);
        switch (await TrySaveAsync(ct, concurrentRetryable: a.Public is not null))
        {
            case SaveOutcome.Overlap: return StoreResult<AppointmentDto>.Overlap();
            case SaveOutcome.Duplicate: return StoreResult<AppointmentDto>.Duplicated();
        }
        if (tx is not null) await tx.CommitAsync(ct);
        return StoreResult<AppointmentDto>.Ok((await GetAppointmentAsync(id, ct))!);
    }

    public async Task<AppointmentDto?> GetAppointmentAsync(Guid id, CancellationToken ct) =>
        (await ProjectAsync(db.Appointments.Where(a => a.Id == id), ct)).FirstOrDefault();

    public async Task<IReadOnlyList<AppointmentDto>> ListAppointmentsAsync(
        DateTimeOffset from, DateTimeOffset to, Guid? locationId, Guid? specialistId, CancellationToken ct) =>
        await ProjectAsync(db.Appointments.Where(a => a.StartsAt >= from.ToUniversalTime() && a.StartsAt < to.ToUniversalTime()
            && (locationId == null || a.LocationId == locationId) && (specialistId == null || a.SpecialistId == specialistId))
            .OrderBy(a => a.StartsAt), ct);

    public async Task<StoreResult<AppointmentDto>> RescheduleAsync(Guid id, DateTimeOffset newStart, DateTimeOffset? reminderAt, CancellationToken ct)
    {
        var appt = await db.Appointments.FirstAsync(a => a.Id == id, ct);
        appt.StartsAt = newStart.ToUniversalTime();
        foreach (var r in await db.Reminders.Where(r => r.AppointmentId == id && r.Status == ReminderStatus.Scheduled).ToListAsync(ct))
            r.Status = ReminderStatus.Cancelled;
        if (reminderAt is { } at)
            db.Reminders.Add(new Reminder { Id = Guid.NewGuid(), AppointmentId = id, ScheduledAt = at.ToUniversalTime() });

        if (await TrySaveAsync(ct) != SaveOutcome.Ok) return StoreResult<AppointmentDto>.Overlap();
        return StoreResult<AppointmentDto>.Ok((await GetAppointmentAsync(id, ct))!);
    }

    public async Task<AppointmentDto?> SetStatusAsync(Guid id, string status, CancellationToken ct)
    {
        var appt = await db.Appointments.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (appt is null) return null;
        appt.Status = EnumText<AppointmentStatus>.FromDb(status);
        await db.SaveChangesAsync(ct);
        return await GetAppointmentAsync(id, ct);
    }

    public async Task<AppointmentDto?> MarkCancelledAsync(Guid id, DateTimeOffset at, CancellationToken ct)
    {
        var appt = await db.Appointments.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (appt is null) return null;
        appt.Status = AppointmentStatus.Cancelled;
        appt.CancelledAt = at;
        foreach (var r in await db.Reminders.Where(r => r.AppointmentId == id && r.Status == ReminderStatus.Scheduled).ToListAsync(ct))
            r.Status = ReminderStatus.Cancelled;
        await db.SaveChangesAsync(ct);
        return await GetAppointmentAsync(id, ct);
    }

    public async Task<PaymentRecord?> GetPaymentAsync(Guid appointmentId, CancellationToken ct)
    {
        var p = await db.Payments.AsNoTracking().Where(p => p.AppointmentId == appointmentId)
            .OrderByDescending(p => p.CreatedAt).FirstOrDefaultAsync(ct);
        return p is null ? null : new PaymentRecord(p.Id, p.AppointmentId, p.Amount,
            EnumText<Ent.PaymentMethod>.ToDb(p.Method), EnumText<PaymentStatus>.ToDb(p.Status), p.ProviderPaymentId);
    }

    public async Task UpdatePaymentAsync(Guid paymentId, string status, string? providerPaymentId, DateTimeOffset? paidAt, CancellationToken ct)
    {
        var p = await db.Payments.FirstAsync(p => p.Id == paymentId, ct);
        p.Status = EnumText<PaymentStatus>.FromDb(status);
        if (providerPaymentId is not null) { p.ProviderPaymentId = providerPaymentId; p.Provider = "payment-service"; }
        if (paidAt is not null) p.PaidAt = paidAt;
        await db.SaveChangesAsync(ct);
    }

    private static bool IsDeadlock(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException!)
            if (e is PostgresException { SqlState: "40P01" } or PostgresException { IsTransient: true }) return true;
        return false;
    }

    private enum SaveOutcome { Ok, Overlap, Duplicate }

    /// <summary>Overlap = exclusion constraint (перетин записів спеціаліста); Duplicate = унікальний Idempotency-Key.</summary>
    private async Task<SaveOutcome> TrySaveAsync(CancellationToken ct, bool concurrentRetryable = false)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return SaveOutcome.Ok;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: ExclusionViolation })
        {
            db.ChangeTracker.Clear();
            return SaveOutcome.Overlap;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505", ConstraintName: "ux_beauty_appointments_idempotency" })
        {
            db.ChangeTracker.Clear();
            return SaveOutcome.Duplicate;
        }
        catch (Exception ex) when (concurrentRetryable && IsDeadlock(ex))
        {
            // 40P01: паралельний повтор того ж запису взаємно заблокував exclusion/unique перевірки; одна з транзакцій відкочена.
            db.ChangeTracker.Clear();
            return SaveOutcome.Duplicate;
        }
    }

    private static async Task<IReadOnlyList<AppointmentDto>> ProjectAsync(IQueryable<Appointment> q, CancellationToken ct)
    {
        var rows = await q.AsNoTracking().Select(a => new
        {
            a.Id, a.LocationId, LocationName = a.Location!.Name, a.SpecialistId, SpecialistName = a.Specialist!.FullName,
            a.ServiceId, ServiceName = a.Service!.Name, a.ClientId, ClientName = a.Client!.FullName,
            a.StartsAt, a.EndsAt, a.DurationMinutes, a.Status, a.Source, a.PriceOriginal, a.PriceFinal, a.PromotionId,
            a.ReminderOption, a.PaymentMethod,
        }).ToListAsync(ct);
        return rows.Select(r => new AppointmentDto(
            r.Id, r.LocationId, r.LocationName, r.SpecialistId, r.SpecialistName, r.ServiceId, r.ServiceName, r.ClientId, r.ClientName,
            r.StartsAt, r.EndsAt, r.DurationMinutes, EnumText<AppointmentStatus>.ToDb(r.Status), EnumText<AppointmentSource>.ToDb(r.Source),
            r.PriceOriginal, r.PriceFinal, r.PromotionId, EnumText<ReminderOption>.ToDb(r.ReminderOption),
            r.PaymentMethod is { } m ? EnumText<Ent.PaymentMethod>.ToDb(m) : null)).ToList();
    }
}
