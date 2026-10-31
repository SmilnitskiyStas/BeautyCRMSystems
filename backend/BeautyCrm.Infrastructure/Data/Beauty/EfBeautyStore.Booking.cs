using BeautyCrm.Application.Features.BeautyAnalytics;
using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyCatalog;
using BeautyCrm.Application.Features.BeautyChannels;
using BeautyCrm.Application.Features.BeautyClients;
using BeautyCrm.Application.Features.BeautyCoupons;
using BeautyCrm.Application.Features.BeautyLocations;
using BeautyCrm.Application.Features.BeautyReminders;
using BeautyCrm.Application.Features.BeautyStaff;
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
    : IBookingStore, ICancellationSettingsStore, ICatalogStore, IClientStore, IAnalyticsStore, IChannelSettingsStore, ILocationStore
{
    private const string ExclusionViolation = "23P01";

    // ---------- довідники ----------

    public Task<ServiceInfo?> GetServiceAsync(Guid serviceId, CancellationToken ct) =>
        db.Services.AsNoTracking().Where(s => s.Id == serviceId)
            .Select(s => new ServiceInfo(s.Id, s.Name, s.DurationMinutes, s.IsActive)).FirstOrDefaultAsync(ct);

    public Task<LocationInfo?> GetLocationAsync(Guid locationId, CancellationToken ct) =>
        db.Locations.AsNoTracking().Where(l => l.Id == locationId)
            .Select(l => new LocationInfo(l.Id, l.Name, l.Timezone, l.IsActive)).FirstOrDefaultAsync(ct);

    /// <summary>Активні майстри закладу з графіком, затвердженими відсутностями та призначеними послугами (TASK-691).</summary>
    public async Task<IReadOnlyList<SpecialistSchedule>> GetSchedulesAsync(Guid locationId, Guid? specialistId, CancellationToken ct)
    {
        var rows = await db.SpecialistLocations.AsNoTracking()
            .Where(sl => sl.LocationId == locationId && sl.IsActive && sl.Specialist!.IsActive
                         && (specialistId == null || sl.SpecialistId == specialistId))
            .Select(sl => new
            {
                sl.SpecialistId, Timezone = sl.Location!.Timezone, sl.WorkingHours, ClosedWeekdays = sl.Location!.ClosedWeekdays,
                LocationHours = sl.Location!.WorkingHours,
            })
            .ToListAsync(ct);
        if (rows.Count == 0) return [];

        var ids = rows.Select(r => r.SpecialistId).ToList();
        var since = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-2));
        var absences = (await db.SpecialistAbsences.AsNoTracking()
                .Where(a => ids.Contains(a.SpecialistId) && a.Status == AbsenceValues.Approved && a.DateTo >= since)
                .Select(a => new { a.SpecialistId, a.DateFrom, a.DateTo }).ToListAsync(ct))
            .ToLookup(a => a.SpecialistId, a => new AbsenceSpan(a.DateFrom, a.DateTo));
        var services = (await db.SpecialistServices.AsNoTracking().Where(x => ids.Contains(x.SpecialistId))
                .Select(x => new { x.SpecialistId, x.ServiceId }).ToListAsync(ct))
            .ToLookup(x => x.SpecialistId, x => x.ServiceId);
        // §17: закриття закладу (дати) - з запасом ±2 доби на часові зони, як у відсутностей.
        var closures = (await db.LocationClosures.AsNoTracking()
                .Where(c => c.LocationId == locationId && c.DateTo >= since)
                .Select(c => new AbsenceSpan(c.DateFrom, c.DateTo)).ToListAsync(ct));

        return rows.Select(r => new SpecialistSchedule(
            r.SpecialistId, r.Timezone, r.WorkingHours, absences[r.SpecialistId].ToList(), services[r.SpecialistId].ToList(),
            r.ClosedWeekdays, closures, r.LocationHours)).ToList();
    }

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
        (await LoadPromotionsAsync(null, activeOnly: true, ct, publicOnly: true))
            .Select(p => new PromotionRule(p.Id, p.Name, p.DiscountType, p.DiscountValue, p.StartsAt, p.EndsAt,
                p.IsActive, p.LocationIds, p.ServiceIds)).ToList();

    // ---------- клієнти для запису ----------

    public async Task<Guid?> ResolveClientAsync(ClientInput client, CancellationToken ct)
    {
        if (client.Id is { } id)
            return await db.Clients.AnyAsync(c => c.Id == id && c.DeletedAt == null, ct) ? id : null;

        var phone = client.Phone!.Trim();
        // §25.7: порівняння за цифрами (спадкові номери), детермінований пріоритет оригіналу (без duplicate_of, найстарший)
        var existing = (await FindClientIdsByPhoneAsync(phone, null, 1, ct)).Select(x => (Guid?)x).FirstOrDefault();
        if (existing is not null) return existing;

        // Зберігається разом із записом одним SaveChanges (див. AddAppointmentAsync).
        var created = new Client
        {
            Id = Guid.NewGuid(), FullName = client.Name!.Trim(), Phone = phone, MarketingConsent = false, // §26.4 (TASK-779): персонал згоду не створює; лише MarketingConsentService (кнопка клієнта)
            PhoneSource = client.PhoneSource is PhoneSources.WebForm ? PhoneSources.WebForm : PhoneSources.Staff,
            Email = string.IsNullOrWhiteSpace(client.Email) ? null : client.Email.Trim(),
        };
        db.Clients.Add(created);
        return created.Id;
    }

    // ---------- записи ----------

    public async Task<StoreResult<AppointmentDto>> AddAppointmentAsync(NewAppointment a, CancellationToken ct)
    {
        var id = a.Public?.AppointmentId ?? Guid.NewGuid();
        // §22.3: план на записі (method + offsets) і дзеркало reminder_option; без плану (спадкові виклики) - з reminder_option.
        var (legacyMethod, legacyOffsets) = ReminderOffsets.FromLegacy(a.ReminderOption);
        db.Appointments.Add(new Appointment
        {
            ReminderMethod = a.Plan?.Method ?? legacyMethod, ReminderOffsets = a.Plan?.Offsets.ToArray() ?? legacyOffsets,
            Id = id, LocationId = a.LocationId, SpecialistId = a.SpecialistId, ServiceId = a.ServiceId, ClientId = a.ClientId,
            StartsAt = a.StartsAt.ToUniversalTime(), DurationMinutes = a.DurationMinutes, Status = AppointmentStatus.Pending,
            Source = EnumText<AppointmentSource>.FromDb(a.Source), PriceOriginal = a.PriceOriginal, PriceFinal = a.PriceFinal,
            PromotionId = a.PromotionId, CouponId = a.Coupon?.CouponId, CampaignId = a.Coupon?.CampaignId, ReminderOption = EnumText<ReminderOption>.FromDb(a.ReminderOption),
            PaymentMethod = EnumText<Ent.PaymentMethod>.FromDb(a.PaymentMethod),
            PublicTokenHash = a.Public?.TokenHash, IdempotencyKeyHash = a.Public?.IdempotencyKeyHash, IdempotencyRequestHash = a.Public?.RequestHash,
        });
        if (a.Plan is { } plan) AddPlanRows(id, a.ClientId, a.LocationId, plan);
        else if (a.ReminderAt is { } at)
            db.Reminders.Add(new Reminder { Id = Guid.NewGuid(), AppointmentId = id, ScheduledAt = at.ToUniversalTime() });
        if (a.Payment is { } p)
            db.Payments.Add(new Payment
            {
                Id = Guid.NewGuid(), AppointmentId = id, Amount = p.Amount, Method = EnumText<Ent.PaymentMethod>.FromDb(p.Method),
            });

        // Публічний запис: повтори одного Idempotency-Key серіалізуються advisory-локом у транзакції вставки
        // (інакше unique-індекс ключа й exclusion-обмеження слоту взаємно блокуються -> deadlock 40P01).
        // TASK-696: далі (завжди) advisory-lock майстра + повторна перевірка відсутності/послуги вже під локом, щоб запис не
        // «розминувся» з паралельним створенням/затвердженням відсутності (та потрапляв у її conflicts[]). Порядок локів
        // фіксований (ключ ідемпотентності -> майстер), тож взаємних блокувань між ними немає.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (a.Public is not null)
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({a.Public.IdempotencyKeyHash}, 0))", ct);
        await SpecialistLock.AcquireAsync(db, a.SpecialistId, ct);
        // TASK-697: FOR SHARE на заклад - паралельна деактивація/зміна часової зони (FOR UPDATE) дочекається коміту запису й побачить його.
        // TASK-701: рядок закладу тримає й зміну вихідних/закриттів (FOR UPDATE) - день перевіряється вже під блокуванням.
        if (!await LockLocationActiveAsync(a.LocationId, ct))
        {
            db.ChangeTracker.Clear();
            return StoreResult<AppointmentDto>.Blocked();
        }
        switch (await BlockReasonAsync(a.SpecialistId, a.LocationId, a.ServiceId, a.StartsAt, a.DurationMinutes, ct))
        {
            case BlockReason.LocationClosed: db.ChangeTracker.Clear(); return StoreResult<AppointmentDto>.Closed();
            case BlockReason.OutsideLocationHours: db.ChangeTracker.Clear(); return StoreResult<AppointmentDto>.OutsideHours();
            case BlockReason.SpecialistUnavailable: db.ChangeTracker.Clear(); return StoreResult<AppointmentDto>.Blocked();
        }
        if (a.Coupon is { } rd)
        {
            // §26.6: CAS погашення в тій самій транзакції; 0 рядків (вичерпано / строк / відкликано / чужий клієнт) -> відкат, запис не створюється.
            // Персональний купон: client_id = власник; max_uses = 1 робить друге використання неможливим навіть у гонці.
            var at = rd.At.ToUniversalTime();
            var cas = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE beauty_coupons SET uses_count = uses_count + 1, updated_at = {at}
                WHERE id = {rd.CouponId} AND status = 'active' AND (max_uses IS NULL OR uses_count < max_uses)
                  AND valid_from <= {at} AND {at} < valid_to AND (client_id IS NULL OR client_id = {a.ClientId})
                """, ct);
            if (cas == 0)
            {
                db.ChangeTracker.Clear();
                return StoreResult<AppointmentDto>.CouponGone();
            }
            db.CouponRedemptions.Add(new CouponRedemption
            {
                Id = Guid.NewGuid(), CouponId = rd.CouponId, AppointmentId = id, ClientId = a.ClientId, DiscountAmount = rd.DiscountAmount,
            });
        }
        switch (await TrySaveAsync(ct, concurrentRetryable: a.Public is not null))
        {
            case SaveOutcome.CouponTaken: return StoreResult<AppointmentDto>.CouponGone();
            case SaveOutcome.Overlap: return StoreResult<AppointmentDto>.Overlap();
            case SaveOutcome.Duplicate: return StoreResult<AppointmentDto>.Duplicated();
        }
        await tx.CommitAsync(ct);
        return StoreResult<AppointmentDto>.Ok((await GetAppointmentAsync(id, ct))!);
    }

    public async Task<AppointmentDto?> GetAppointmentAsync(Guid id, CancellationToken ct) =>
        (await ProjectAsync(db.Appointments.Where(a => a.Id == id), ct)).FirstOrDefault();

    public async Task<IReadOnlyList<AppointmentDto>> ListAppointmentsAsync(
        DateTimeOffset from, DateTimeOffset to, Guid? locationId, Guid? specialistId, bool includeCancelled, CancellationToken ct) =>
        await ProjectAsync(db.Appointments.Where(a => a.StartsAt >= from.ToUniversalTime() && a.StartsAt < to.ToUniversalTime()
            && (includeCancelled || a.Status != AppointmentStatus.Cancelled)
            && (locationId == null || a.LocationId == locationId) && (specialistId == null || a.SpecialistId == specialistId))
            .OrderBy(a => a.StartsAt), ct);

    public Task<StoreResult<AppointmentDto>> RescheduleAsync(Guid id, DateTimeOffset newStart, DateTimeOffset? reminderAt, CancellationToken ct) =>
        RescheduleCoreAsync(id, newStart, null, reminderAt, ct);

    public Task<StoreResult<AppointmentDto>> RescheduleAsync(Guid id, DateTimeOffset newStart, PlannedReminders plan, CancellationToken ct) =>
        RescheduleCoreAsync(id, newStart, plan, null, ct);

    private async Task<StoreResult<AppointmentDto>> RescheduleCoreAsync(
        Guid id, DateTimeOffset newStart, PlannedReminders? plan, DateTimeOffset? legacyReminderAt, CancellationToken ct)
    {
        var appt = await db.Appointments.FirstAsync(a => a.Id == id, ct);
        // TASK-696: lock майстра + повторна перевірка відсутності під локом (див. AddAppointmentAsync).
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await SpecialistLock.AcquireAsync(db, appt.SpecialistId, ct);
        if (!await LockLocationActiveAsync(appt.LocationId, ct))
        {
            db.ChangeTracker.Clear();
            return StoreResult<AppointmentDto>.Blocked();
        }
        switch (await BlockReasonAsync(appt.SpecialistId, appt.LocationId, appt.ServiceId, newStart, appt.DurationMinutes, ct))
        {
            case BlockReason.LocationClosed: db.ChangeTracker.Clear(); return StoreResult<AppointmentDto>.Closed();
            case BlockReason.OutsideLocationHours: db.ChangeTracker.Clear(); return StoreResult<AppointmentDto>.OutsideHours();
            case BlockReason.SpecialistUnavailable: db.ChangeTracker.Clear(); return StoreResult<AppointmentDto>.Blocked();
        }
        appt.StartsAt = newStart.ToUniversalTime();
        // §22.4: старі точки закриваються (replanned) умовними UPDATE ДО вставки нових - частковий UNIQUE точок не бачить дублів.
        await ClosePlanAsync(id, ReminderSkipReasons.Replanned, includePlannerSkipped: true, ct);
        if (plan is not null)
        {
            appt.ReminderMethod = plan.Method;
            appt.ReminderOffsets = plan.Offsets.ToArray();
            appt.ReminderOption = EnumText<ReminderOption>.FromDb(plan.Mirror);
            AddPlanRows(id, appt.ClientId, appt.LocationId, plan);
        }
        else if (legacyReminderAt is { } at)
            db.Reminders.Add(new Reminder { Id = Guid.NewGuid(), AppointmentId = id, ScheduledAt = at.ToUniversalTime() });

        if (await TrySaveAsync(ct) != SaveOutcome.Ok) return StoreResult<AppointmentDto>.Overlap();
        await tx.CommitAsync(ct);
        return StoreResult<AppointmentDto>.Ok((await GetAppointmentAsync(id, ct))!);
    }

    /// <summary>Блокує рядок закладу (FOR SHARE) до кінця транзакції; false, якщо заклад деактивовано (чи недоступний).</summary>
    private async Task<bool> LockLocationActiveAsync(Guid locationId, CancellationToken ct) =>
        (await db.Database.SqlQuery<bool>($"""SELECT is_active AS "Value" FROM beauty_locations WHERE id = {locationId} FOR SHARE""").ToListAsync(ct))
        .FirstOrDefault();

    private enum BlockReason { None, SpecialistUnavailable, LocationClosed, OutsideLocationHours }

    /// <summary>
    /// Під advisory-lock майстра й блокуванням рядка закладу: чи день запису тепер закритий для закладу (вихідний/закриття,
    /// TASK-701), зайнятий затвердженою відсутністю, послугу знято з майстра або майстра/заклад вимкнено (стан міг змінитися
    /// після перевірки в сервісі). Закритий день має пріоритет, як і в SlotCalculator.Check.
    /// </summary>
    private async Task<BlockReason> BlockReasonAsync(
        Guid specialistId, Guid locationId, Guid serviceId, DateTimeOffset startsAt, int durationMinutes, CancellationToken ct)
    {
        var schedule = (await GetSchedulesAsync(locationId, specialistId, ct)).FirstOrDefault();
        if (schedule is null) return BlockReason.SpecialistUnavailable;
        var localDate = SlotCalculator.LocalDate(startsAt, schedule.Timezone);
        if (schedule.IsClosedOn(localDate)) return BlockReason.LocationClosed;
        if (!schedule.Offers(serviceId) || schedule.IsAbsentOn(localDate)) return BlockReason.SpecialistUnavailable;
        // §18.1: години закладу читаються вже під FOR SHARE на рядку закладу - PUT working-hours (FOR UPDATE) їх не змінить посеред запису
        if (schedule.HasLocationHours)
        {
            var local = TimeZoneInfo.ConvertTime(startsAt, TimeZoneInfo.FindSystemTimeZoneById(schedule.Timezone));
            if (!SlotCalculator.FitsHours(schedule.LocationHoursJson, local.DayOfWeek, TimeOnly.FromDateTime(local.DateTime), durationMinutes))
                return BlockReason.OutsideLocationHours;
        }
        return BlockReason.None;
    }

    public async Task<StoreResult<AppointmentDto>?> SetStatusAsync(Guid id, string status, CancellationToken ct)
    {
        var target = EnumText<AppointmentStatus>.FromDb(status);
        if (target == AppointmentStatus.Completed) return await CompleteWithCostSnapshotAsync(id, ct);

        var appt = await db.Appointments.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (appt is null) return null;
        appt.Status = target;
        // §22.4: no_show закриває scheduled-нагадування й відкриті задачі дзвінка в тій самій транзакції (знімок собівартості не пишеться).
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        if (appt.Status is AppointmentStatus.NoShow)
            await ClosePlanAsync(id, ReminderSkipReasons.AppointmentInactive, includePlannerSkipped: false, ct);
        await tx.CommitAsync(ct);
        return StoreResult<AppointmentDto>.Ok((await GetAppointmentAsync(id, ct))!);
    }

    /// <summary>
    /// §24.4, ADR-014 §2.2: перехід у completed і знімок собівартості - ОДИН умовний UPDATE (WHERE status IN pending|confirmed),
    /// тому з двох паралельних завершень знімок пише рівно одне (друге - 0 рядків -> AlreadyClosed), а повторне completed нічого не чіпає.
    /// Ефективний рядок: заклад запису, інакше мережа; без рядка пишеться лише cost_captured_at (CHECK cost_consistency).
    /// Виплата = round(price_final * coalesce(percent, 0) / 100, 2) від ціни продажу; сума собівартості в лог не потрапляє.
    /// </summary>
    private async Task<StoreResult<AppointmentDto>?> CompleteWithCostSnapshotAsync(Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var updated = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE beauty_appointments a SET status = 'completed',
                   cost_snapshot = e.cost_amount,
                   master_percent_snapshot = e.master_percent,
                   master_payout_snapshot = CASE WHEN e.cost_amount IS NULL THEN NULL
                                                 ELSE round(a.price_final * coalesce(e.master_percent, 0) / 100, 2) END,
                   cost_captured_at = now(), updated_at = now()
            FROM beauty_appointments s
            LEFT JOIN LATERAL (SELECT c.cost_amount, c.master_percent FROM beauty_service_costs c
                               WHERE c.service_id = s.service_id AND (c.location_id = s.location_id OR c.location_id IS NULL)
                               ORDER BY c.location_id NULLS LAST LIMIT 1) e ON true
            WHERE a.id = {id} AND s.id = a.id AND a.status IN ('pending', 'confirmed')
            """, ct);
        if (updated == 0)
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return await db.Appointments.AsNoTracking().AnyAsync(a => a.Id == id, ct) ? StoreResult<AppointmentDto>.AlreadyClosed() : null;
        }
        // §22.4: completed закриває scheduled-нагадування й відкриті задачі дзвінка в тій самій транзакції.
        await ClosePlanAsync(id, ReminderSkipReasons.AppointmentInactive, includePlannerSkipped: false, ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return StoreResult<AppointmentDto>.Ok((await GetAppointmentAsync(id, ct))!);
    }

    public async Task<bool> TryClaimCancelAsync(Guid id, DateTimeOffset at, CancelOrigin origin, string? reason, CancellationToken ct)
    {
        var atUtc = at.ToUniversalTime();
        var type = origin.Type;
        var userId = type == CancelledByTypes.Staff ? origin.UserId : null; // user id лише для staff (CHECK у БД)
        return await db.Appointments
            .Where(a => a.Id == id && (a.Status == AppointmentStatus.Pending || a.Status == AppointmentStatus.Confirmed))
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, AppointmentStatus.Cancelled).SetProperty(a => a.CancelledAt, atUtc)
                .SetProperty(a => a.CancelledByType, type).SetProperty(a => a.CancelledByUserId, userId)
                .SetProperty(a => a.CancelReason, reason), ct) == 1;
    }

    public async Task ReleaseCancelAsync(Guid id, string previousStatus, CancellationToken ct)
    {
        var prev = EnumText<AppointmentStatus>.FromDb(previousStatus);
        try
        {
            await db.Appointments.Where(a => a.Id == id && a.Status == AppointmentStatus.Cancelled)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, prev).SetProperty(a => a.CancelledAt, (DateTimeOffset?)null)
                    .SetProperty(a => a.CancelledByType, (string?)null).SetProperty(a => a.CancelledByUserId, (Guid?)null)
                    .SetProperty(a => a.CancelReason, (string?)null), ct);
        }
        catch (PostgresException ex) when (ex.SqlState == ExclusionViolation)
        {
            // слот уже зайнятий іншим записом, поки повернення не вдалося: запис лишається скасованим, кошти не повернуто —
            // потребує ручної обробки (платіж залишився paid).
        }
    }

    public async Task<AppointmentDto?> MarkCancelledAsync(Guid id, DateTimeOffset at, CancellationToken ct)
    {
        var appt = await db.Appointments.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (appt is null) return null;
        appt.Status = AppointmentStatus.Cancelled;
        appt.CancelledAt = at;
        // §26.6: збій оплати при створенні (автора скасування ще немає) -> payment_failed; решта шляхів - cancelled
        var releaseReason = appt.CancelledByType is null ? "payment_failed" : "cancelled";
        appt.CancelledByType ??= CancelledByTypes.System; // збій оплати при створенні тощо; автор staff/client вже зафіксований claim-ом
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        await ClosePlanAsync(id, ReminderSkipReasons.AppointmentInactive, includePlannerSkipped: false, ct);
        await ReleaseCouponCoreAsync(id, releaseReason, at, ct);
        await tx.CommitAsync(ct);
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

    private enum SaveOutcome { Ok, Overlap, Duplicate, CouponTaken }

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
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505", ConstraintName: "ux_beauty_coupon_redemptions_active" })
        {
            db.ChangeTracker.Clear();
            return SaveOutcome.CouponTaken;
        }
        catch (Exception ex) when (concurrentRetryable && IsDeadlock(ex))
        {
            // 40P01: паралельний повтор того ж запису взаємно заблокував exclusion/unique перевірки; одна з транзакцій відкочена.
            db.ChangeTracker.Clear();
            return SaveOutcome.Duplicate;
        }
    }

    private async Task<IReadOnlyList<AppointmentDto>> ProjectAsync(IQueryable<Appointment> q, CancellationToken ct)
    {
        var rows = await q.AsNoTracking().Select(a => new
        {
            a.Id, a.LocationId, LocationName = a.Location!.Name, a.SpecialistId, SpecialistName = a.Specialist!.FullName,
            a.ServiceId, ServiceName = a.Service!.Name, a.ClientId, ClientName = a.Client!.FullName,
            a.StartsAt, a.EndsAt, a.DurationMinutes, a.Status, a.Source, a.PriceOriginal, a.PriceFinal, a.PromotionId,
            a.ReminderOption, a.ReminderMethod, a.ReminderOffsets, a.PaymentMethod, LocationTimezone = a.Location!.Timezone,
            a.CancelledAt, a.CancelledByType, a.CancelReason, a.ClientConfirmedAt, a.CouponId, a.CampaignId,
            CancelledByName = a.CancelledByUserId == null ? null
                : db.Users.Where(u => u.Id == a.CancelledByUserId).Select(u => u.FullName).FirstOrDefault(),
        }).ToListAsync(ct);
        var couponIds = rows.Where(r => r.CouponId != null).Select(r => r.CouponId!.Value).Distinct().ToList();
        var couponInfo = couponIds.Count == 0 ? [] : (await db.Coupons.AsNoTracking().Where(c => couponIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Kind, c.CodeHint, PromotionName = db.Promotions.Where(p => p.Id == c.PromotionId).Select(p => p.Name).FirstOrDefault() })
            .ToListAsync(ct)).ToDictionary(c => c.Id);
        var reminderViews = await LoadReminderViewsAsync(rows.Select(r => (r.Id, r.StartsAt, r.ReminderOption, r.ReminderMethod, r.ReminderOffsets)).ToList(), ct);
        return rows.Select(r => new AppointmentDto(
            r.Id, r.LocationId, r.LocationName, r.SpecialistId, r.SpecialistName, r.ServiceId, r.ServiceName, r.ClientId, r.ClientName,
            r.StartsAt, r.EndsAt, r.DurationMinutes, EnumText<AppointmentStatus>.ToDb(r.Status), EnumText<AppointmentSource>.ToDb(r.Source),
            r.PriceOriginal, r.PriceFinal, r.PromotionId, EnumText<ReminderOption>.ToDb(r.ReminderOption),
            r.PaymentMethod is { } m ? EnumText<Ent.PaymentMethod>.ToDb(m) : null, null, r.LocationTimezone,
            r.Status == AppointmentStatus.Cancelled ? r.CancelledAt : null,
            r.Status == AppointmentStatus.Cancelled ? new CancelledByDto(r.CancelledByType ?? CancelledByTypes.System, r.CancelledByName) : null,
            r.Status == AppointmentStatus.Cancelled ? r.CancelReason : null, reminderViews[r.Id], r.ClientConfirmedAt,
            r.CouponId is { } cid && couponInfo.TryGetValue(cid, out var ci) ? new AppointmentCouponDto(ci.Id, ci.Kind, ci.CodeHint, ci.PromotionName ?? "", CouponOutcomes.Applied) : null,
            r.CampaignId)).ToList();
    }
}
