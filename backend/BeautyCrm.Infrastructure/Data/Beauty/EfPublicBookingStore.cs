using BeautyCrm.Application.Features.BeautyPublicBooking;
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

    public async Task<IReadOnlyList<PublicSpecialistDto>> ListSpecialistsAsync(Guid locationId, CancellationToken ct) =>
        await db.SpecialistLocations.AsNoTracking()
            .Where(sl => sl.LocationId == locationId && sl.IsActive && sl.Specialist!.IsActive)
            .OrderBy(sl => sl.Specialist!.FullName)
            .Select(sl => new PublicSpecialistDto(sl.SpecialistId, sl.Specialist!.FullName, sl.Specialist.Title, sl.Specialist.PhotoUrl))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<PublicServiceBase>> ListServicesAsync(Guid locationId, CancellationToken ct)
    {
        var services = await db.Services.AsNoTracking().Where(s => s.IsActive).OrderBy(s => s.Category).ThenBy(s => s.Name).ToListAsync(ct);
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
