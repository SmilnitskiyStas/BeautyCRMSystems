using BeautyCrm.Application.Features.BeautyLocations;
using BeautyCrm.Application.Features.BeautyOverview;
using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace BeautyCrm.Infrastructure.Data.Beauty;

// ILocationStore (TASK-697, §16): CRUD закладів під RLS tenant. Видалення немає (лише is_active).
public sealed partial class EfBeautyStore
{
    public async Task<IReadOnlyList<LocationDto>> ListAsync(bool includeInactive, CancellationToken ct) =>
        await db.Locations.AsNoTracking().Where(l => includeInactive || l.IsActive)
            .OrderBy(l => l.Name).ThenBy(l => l.Id)
            .Select(l => new LocationDto(l.Id, l.Name, l.Address, l.Timezone, l.IsActive, l.Phone)).ToListAsync(ct);

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

    private static LocationDto ToLocationDto(Location l) => new(l.Id, l.Name, l.Address, l.Timezone, l.IsActive, l.Phone);

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
