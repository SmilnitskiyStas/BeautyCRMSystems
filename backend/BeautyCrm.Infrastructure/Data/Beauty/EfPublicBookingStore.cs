using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyPublicBooking;
using BeautyCrm.Application.Features.BeautyStaff;
using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BeautyCrm.Infrastructure.Data.Beauty;

/// <summary>
/// Порт публічного запису над EF. Tenant задає RLS (app.tenant_id встановлюється з slug до першого запиту);
/// жодної нової політики не потрібно. Проєкції навмисно вузькі: нічого, окрім публічних полів каталогу.
/// </summary>
public sealed class EfPublicBookingStore(BeautyDbContext db) : IPublicBookingStore
{
    public async Task<IReadOnlyList<PublicLocationDto>> ListActiveLocationsAsync(CancellationToken ct) =>
        await db.Locations.AsNoTracking().Where(l => l.IsActive).OrderBy(l => l.Name)
            .Select(l => new PublicLocationDto(l.Id, l.Name, l.Address, l.Phone, l.Timezone)).ToListAsync(ct);

    /// <summary>
    /// Придатні до запису майстри закладу: активні, заклад-зв'язок активний, у графіку є робочий інтервал
    /// і призначена хоча б одна активна послуга (TASK-691). Інших публічно не показуємо.
    /// </summary>
    private async Task<List<Guid>> EligibleSpecialistIdsAsync(Guid locationId, CancellationToken ct)
    {
        var rows = await db.SpecialistLocations.AsNoTracking()
            .Where(sl => sl.LocationId == locationId && sl.IsActive && sl.Specialist!.IsActive
                         && db.SpecialistServices.Any(x => x.SpecialistId == sl.SpecialistId && x.Service!.IsActive))
            .Select(sl => new { sl.SpecialistId, sl.WorkingHours }).ToListAsync(ct);
        return rows.Where(r => SlotCalculator.HasWorkingHours(r.WorkingHours)).Select(r => r.SpecialistId).Distinct().ToList();
    }

    public async Task<IReadOnlyList<PublicSpecialistDto>> ListSpecialistsAsync(Guid locationId, Guid? serviceId, CancellationToken ct)
    {
        var ids = await EligibleSpecialistIdsAsync(locationId, ct);
        if (serviceId is { } sid)
        {
            var offering = await db.SpecialistServices.AsNoTracking().Where(x => x.ServiceId == sid && ids.Contains(x.SpecialistId))
                .Select(x => x.SpecialistId).ToListAsync(ct);
            ids = offering;
        }
        return await db.Specialists.AsNoTracking().Where(s => ids.Contains(s.Id)).OrderBy(s => s.FullName)
            .Select(s => new PublicSpecialistDto(s.Id, s.FullName, s.Position ?? s.Title, s.PhotoUrl))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<PublicServiceBase>> ListServicesAsync(Guid locationId, Guid? specialistId, CancellationToken ct)
    {
        var ids = await EligibleSpecialistIdsAsync(locationId, ct);
        if (specialistId is { } spid) ids = ids.Where(i => i == spid).ToList();
        var offered = await db.SpecialistServices.AsNoTracking().Where(x => ids.Contains(x.SpecialistId))
            .Select(x => x.ServiceId).Distinct().ToListAsync(ct);
        var services = await db.Services.AsNoTracking().Where(s => s.IsActive && offered.Contains(s.Id))
            .OrderBy(s => s.Category).ThenBy(s => s.Name).ToListAsync(ct);
        var prices = await db.ServicePrices.AsNoTracking()
            .Where(p => p.LocationId == locationId || p.LocationId == null)
            .Select(p => new { p.ServiceId, p.LocationId, p.Price }).ToListAsync(ct);
        var result = new List<PublicServiceBase>();
        foreach (var s in services)
        {
            var mine = prices.Where(p => p.ServiceId == s.Id).ToList();
            var price = mine.FirstOrDefault(p => p.LocationId == locationId)?.Price ?? mine.FirstOrDefault(p => p.LocationId == null)?.Price;
            if (price is not null)
                result.Add(new PublicServiceBase(s.Id, s.Name, s.Description, s.Category, s.DurationMinutes, price.Value));
        }
        return result;
    }

    public async Task<Guid?> FindAppointmentIdByTokenHashAsync(string tokenHash, CancellationToken ct) =>
        await db.Appointments.AsNoTracking().Where(a => a.PublicTokenHash == tokenHash)
            .Select(a => (Guid?)a.Id).FirstOrDefaultAsync(ct);

    public async Task<IdempotencyHit?> FindByIdempotencyKeyAsync(string keyHash, CancellationToken ct)
    {
        var row = await db.Appointments.AsNoTracking().Where(a => a.IdempotencyKeyHash == keyHash)
            .Select(a => new { a.Id, a.IdempotencyRequestHash }).FirstOrDefaultAsync(ct);
        return row is null ? null : new IdempotencyHit(row.Id, row.IdempotencyRequestHash ?? string.Empty);
    }

    public Task<int> CountRecentOnlineByPhoneAsync(string phone, DateTimeOffset since, CancellationToken ct) =>
        db.Appointments.AsNoTracking().CountAsync(a => a.Source == AppointmentSource.Online && a.CreatedAt > since
            && a.Client!.Phone == phone && a.Client.DeletedAt == null, ct);

    public Task<int> CountActiveOnlineByPhoneAsync(string phone, DateTimeOffset now, CancellationToken ct) =>
        db.Appointments.AsNoTracking().CountAsync(a =>
            a.Source == AppointmentSource.Online && a.StartsAt > now
            && (a.Status == AppointmentStatus.Pending || a.Status == AppointmentStatus.Confirmed)
            && a.Client!.Phone == phone && a.Client.DeletedAt == null, ct);
}

public static class PublicBookingDataExtensions
{
    /// <summary>Порт даних публічного запису. Потребує AddBeautyData.</summary>
    public static IServiceCollection AddBeautyPublicBookingData(this IServiceCollection services) =>
        services.AddScoped<IPublicBookingStore, EfPublicBookingStore>();
}
