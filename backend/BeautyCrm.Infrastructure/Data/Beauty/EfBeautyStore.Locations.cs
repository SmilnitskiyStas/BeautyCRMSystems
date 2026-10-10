using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyLocations;
using BeautyCrm.Application.Features.BeautyOverview;
using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BeautyCrm.Infrastructure.Data.Beauty;

// ILocationStore (TASK-697, §16): CRUD закладів під RLS tenant. Видалення немає (лише is_active).
public sealed partial class EfBeautyStore
{
    public async Task<IReadOnlyList<LocationDto>> ListAsync(bool includeInactive, CancellationToken ct) =>
        await db.Locations.AsNoTracking().Where(l => includeInactive || l.IsActive)
            .OrderBy(l => l.Name).ThenBy(l => l.Id)
            .Select(l => new LocationDto(l.Id, l.Name, l.Address, l.Timezone, l.IsActive, l.Phone, l.ClosedWeekdays)).ToListAsync(ct);

    public async Task<LocationWriteResult> CreateAsync(LocationChange change, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockLocationNamesAsync(ct);
        if (await NameTakenAsync(change.Name, null, ct)) return new(LocationWriteOutcome.NameTaken);
        var l = new Location { Id = Guid.NewGuid(), Name = change.Name, Address = change.Address, Phone = change.Phone, Timezone = change.Timezone, IsActive = true };
        db.Locations.Add(l);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(LocationWriteOutcome.Ok, ToLocationDto(l));
    }

    public async Task<LocationWriteResult> UpdateAsync(Guid id, LocationChange change, DateTimeOffset now, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockLocationNamesAsync(ct);
        // FOR UPDATE: паралельне створення/перенос запису (FOR SHARE на цьому рядку) або завершиться до перевірки нижче,
        // або побачить уже деактивований заклад.
        var l = (await db.Locations.FromSqlInterpolated($"SELECT * FROM beauty_locations WHERE id = {id} FOR UPDATE").ToListAsync(ct)).FirstOrDefault();
        if (l is null) return new(LocationWriteOutcome.NotFound);
        if (await NameTakenAsync(change.Name, id, ct)) return new(LocationWriteOutcome.NameTaken);

        var deactivating = l.IsActive && change.IsActive == false;
        var tzChanging = !string.Equals(l.Timezone, change.Timezone, StringComparison.Ordinal);
        if (deactivating || tzChanging)
        {
            var nowUtc = now.ToUniversalTime();
            var hasFuture = await db.Appointments.AnyAsync(a => a.LocationId == id && a.StartsAt > nowUtc
                && (a.Status == AppointmentStatus.Pending || a.Status == AppointmentStatus.Confirmed), ct);
            if (hasFuture) return new(deactivating ? LocationWriteOutcome.HasFutureAppointments : LocationWriteOutcome.TimezoneLocked);
        }

        l.Name = change.Name;
        l.Address = change.Address;
        l.Phone = change.Phone;
        l.Timezone = change.Timezone;
        if (change.IsActive is { } active) l.IsActive = active;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(LocationWriteOutcome.Ok, ToLocationDto(l));
    }

    private static LocationDto ToLocationDto(Location l) => new(l.Id, l.Name, l.Address, l.Timezone, l.IsActive, l.Phone, l.ClosedWeekdays);

    // ---------- вихідні дні закладу (TASK-701, §17) ----------

    private const int MaxConflicts = 500;

    public async Task<LocationDto?> GetAsync(Guid id, CancellationToken ct) =>
        await db.Locations.AsNoTracking().Where(l => l.Id == id)
            .Select(l => new LocationDto(l.Id, l.Name, l.Address, l.Timezone, l.IsActive, l.Phone, l.ClosedWeekdays))
            .FirstOrDefaultAsync(ct);

    /// <summary>Блокує рядок закладу FOR UPDATE (як PUT /locations): запис/перенос тримають на ньому FOR SHARE.</summary>
    private async Task<Location?> LockLocationForUpdateAsync(Guid id, CancellationToken ct) =>
        (await db.Locations.FromSqlInterpolated($"SELECT * FROM beauty_locations WHERE id = {id} FOR UPDATE").ToListAsync(ct)).FirstOrDefault();

    public async Task<ClosedWeekdaysWriteResult> SetClosedWeekdaysAsync(
        Guid id, IReadOnlyList<string> weekdays, bool confirm, DateTimeOffset now, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var l = await LockLocationForUpdateAsync(id, ct);
        if (l is null) return new(ClosureWriteOutcome.NotFound);

        // «Нові» вихідні = дні, яких ще не було; записи на вже закритих днях цією зміною не створюються.
        var added = weekdays.Except(l.ClosedWeekdays).ToHashSet();
        if (added.Count > 0)
        {
            var since = SinceDate(now);
            var closures = await db.LocationClosures.AsNoTracking().Where(c => c.LocationId == id && c.DateTo >= since)
                .Select(c => new { c.DateFrom, c.DateTo }).ToListAsync(ct);
            var conflicts = await FindClosureConflictsAsync(l, now, null, null, d =>
                added.Contains(SlotCalculator.DayKey(d.DayOfWeek)) && !closures.Any(c => d >= c.DateFrom && d <= c.DateTo), ct);
            if (conflicts.Count > 0 && !confirm) return new(ClosureWriteOutcome.NeedsConfirmation, null, conflicts);
        }

        l.ClosedWeekdays = weekdays.ToArray();
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(ClosureWriteOutcome.Ok, ToLocationDto(l));
    }

    public async Task<IReadOnlyList<ClosureDto>> ListClosuresAsync(Guid locationId, DateOnly from, DateOnly to, CancellationToken ct) =>
        await db.LocationClosures.AsNoTracking()
            .Where(c => c.LocationId == locationId && c.DateTo >= from && c.DateFrom <= to)
            .OrderBy(c => c.DateFrom).ThenBy(c => c.Id)
            .Select(c => new ClosureDto(c.Id, c.DateFrom, c.DateTo, c.Reason)).ToListAsync(ct);

    public async Task<ClosureWriteResult> AddClosureAsync(
        Guid locationId, NewClosure closure, bool confirm, Guid? createdByUserId, DateTimeOffset now, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var l = await LockLocationForUpdateAsync(locationId, ct);
        if (l is null) return new(ClosureWriteOutcome.NotFound);

        var (from, to) = (closure.DateFrom, closure.DateTo);
        if (await db.LocationClosures.AnyAsync(c => c.LocationId == locationId && c.DateFrom <= to && c.DateTo >= from, ct))
            return new(ClosureWriteOutcome.Overlap);

        // Нові закриті дні = дні періоду, що ще не є вихідними за днем тижня (перетину з іншими закриттями немає).
        var weekly = l.ClosedWeekdays.ToHashSet();
        var conflicts = await FindClosureConflictsAsync(l, now, from, to,
            d => d >= from && d <= to && !weekly.Contains(SlotCalculator.DayKey(d.DayOfWeek)), ct);
        if (conflicts.Count > 0 && !confirm) return new(ClosureWriteOutcome.NeedsConfirmation, null, conflicts);

        var entity = new LocationClosure
        {
            Id = Guid.NewGuid(), LocationId = locationId, DateFrom = from, DateTo = to, Reason = closure.Reason,
            CreatedByUserId = createdByUserId,
        };
        db.LocationClosures.Add(entity);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: ExclusionViolation })
        {
            db.ChangeTracker.Clear();
            return new(ClosureWriteOutcome.Overlap);
        }
        await tx.CommitAsync(ct);
        return new(ClosureWriteOutcome.Ok, new ClosureDto(entity.Id, from, to, closure.Reason));
    }

    public async Task<bool> DeleteClosureAsync(Guid locationId, Guid closureId, CancellationToken ct) =>
        await db.LocationClosures.Where(c => c.Id == closureId && c.LocationId == locationId).ExecuteDeleteAsync(ct) > 0;

    private static DateOnly SinceDate(DateTimeOffset now) => DateOnly.FromDateTime(now.UtcDateTime.AddDays(-2));

    /// <summary>
    /// Майбутні активні записи закладу (pending/confirmed, starts_at &gt; now), чия ЛОКАЛЬНА дата в зоні закладу стає закритою
    /// (isNewlyClosed). Лише id/час/послуга/майстер - без даних клієнта. Вибірка в тій самій транзакції, що й зміна.
    /// </summary>
    private async Task<IReadOnlyList<ClosureConflictDto>> FindClosureConflictsAsync(
        Location l, DateTimeOffset now, DateOnly? from, DateOnly? to, Func<DateOnly, bool> isNewlyClosed, CancellationToken ct)
    {
        var nowUtc = now.ToUniversalTime();
        var query = db.Appointments.AsNoTracking().Where(a => a.LocationId == l.Id && a.StartsAt > nowUtc
            && (a.Status == AppointmentStatus.Pending || a.Status == AppointmentStatus.Confirmed));
        if (from is { } f && to is { } t)
        {
            // вікно початків із запасом ±1 доба на часові зони; точна перевірка - за локальною датою нижче
            var lo = new DateTimeOffset(f.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddDays(-1);
            var hi = new DateTimeOffset(t.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddDays(2);
            query = query.Where(a => a.StartsAt >= lo && a.StartsAt < hi);
        }
        var rows = await query.OrderBy(a => a.StartsAt)
            .Select(a => new { a.Id, a.StartsAt, ServiceName = a.Service!.Name, SpecialistName = a.Specialist!.FullName })
            .ToListAsync(ct);
        return rows
            .Where(r => isNewlyClosed(SlotCalculator.LocalDate(r.StartsAt, l.Timezone)))
            .Take(MaxConflicts)
            .Select(r => new ClosureConflictDto(r.Id, r.StartsAt, r.ServiceName, r.SpecialistName))
            .ToList();
    }

    /// <summary>Серіалізує зміни закладів tenant-а (унікальність імені без unique-індексу, що міг би впертись у демо-дані).</summary>
    private Task LockLocationNamesAsync(CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended(coalesce(current_setting('app.tenant_id', true), '') || ':locations', 0))", ct);

    private Task<bool> NameTakenAsync(string name, Guid? exceptId, CancellationToken ct)
    {
        var lower = name.ToLowerInvariant();
        return db.Locations.AsNoTracking().AnyAsync(l => l.Name.ToLower() == lower && (exceptId == null || l.Id != exceptId), ct);
    }
}
