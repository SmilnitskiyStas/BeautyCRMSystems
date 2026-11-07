using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyChatFlow;
using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BeautyCrm.Infrastructure.Data.Beauty;

/// <summary>
/// Каталог кроків потоку запису (TASK-838, §28.4): tenant-wide, лише з БД під RLS (жодного LLM). Придатність майстра - як у публічному
/// каталозі (<c>EfPublicBookingStore.EligibleSpecialistIdsAsync</c>): активний майстер + активний зв'язок із закладом + робочий інтервал у графіку,
/// а ще активний заклад, активна послуга, призначення майстра на послугу й ціна (override закладу або мережева). Усе зводиться до
/// набору «трійок» (майстер, заклад, послуга); решта методів - проєкції цього набору. Порядок стабільний (ключі закінчуються на id).
/// </summary>
public sealed class EfChatFlowCatalog(BeautyDbContext db) : IChatFlowCatalog
{
    private sealed record Triple(Guid SpecialistId, Guid LocationId, Guid ServiceId);

    private async Task<List<Triple>> TriplesAsync(CancellationToken ct)
    {
        var links = await db.SpecialistLocations.AsNoTracking()
            .Where(sl => sl.IsActive && sl.Specialist!.IsActive && sl.Location!.IsActive)
            .Select(sl => new { sl.SpecialistId, sl.LocationId, sl.WorkingHours }).ToListAsync(ct);
        links = links.Where(l => SlotCalculator.HasWorkingHours(l.WorkingHours)).ToList();
        if (links.Count == 0) return [];

        var assigned = await db.SpecialistServices.AsNoTracking().Where(x => x.Service!.IsActive)
            .Select(x => new { x.SpecialistId, x.ServiceId }).ToListAsync(ct);
        var prices = await db.ServicePrices.AsNoTracking().Select(p => new { p.ServiceId, p.LocationId }).ToListAsync(ct);
        var networkPriced = prices.Where(p => p.LocationId == null).Select(p => p.ServiceId).ToHashSet();
        var locationPriced = prices.Where(p => p.LocationId != null).Select(p => (p.ServiceId, p.LocationId!.Value)).ToHashSet();

        var bySpecialist = assigned.ToLookup(x => x.SpecialistId, x => x.ServiceId);
        var result = new List<Triple>();
        foreach (var l in links)
            foreach (var serviceId in bySpecialist[l.SpecialistId].Distinct())
                if (networkPriced.Contains(serviceId) || locationPriced.Contains((serviceId, l.LocationId)))
                    result.Add(new Triple(l.SpecialistId, l.LocationId, serviceId));
        return result.Distinct().ToList();
    }

    public async Task<IReadOnlyList<FlowServiceItem>> ListBookableServicesAsync(Guid? specialistId, Guid? locationId, CancellationToken ct)
    {
        var ids = (await TriplesAsync(ct)).Where(t => (specialistId is null || t.SpecialistId == specialistId)
                                                       && (locationId is null || t.LocationId == locationId))
            .Select(t => t.ServiceId).Distinct().ToList();
        if (ids.Count == 0) return [];
        var rows = await db.Services.AsNoTracking().Where(s => ids.Contains(s.Id))
            .Select(s => new { s.Id, s.Name, s.Category }).ToListAsync(ct);
        return rows.OrderBy(s => s.Category, StringComparer.Ordinal).ThenBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.Id)
            .Select(s => new FlowServiceItem(s.Id, s.Name, s.Category)).ToList();
    }

    public async Task<IReadOnlyList<FlowSpecialistItem>> ListSpecialistsForServiceAsync(Guid serviceId, Guid? locationId, CancellationToken ct)
    {
        var ids = (await TriplesAsync(ct)).Where(t => t.ServiceId == serviceId && (locationId is null || t.LocationId == locationId))
            .Select(t => t.SpecialistId).Distinct().ToList();
        if (ids.Count == 0) return [];
        var rows = await db.Specialists.AsNoTracking().Where(s => ids.Contains(s.Id)).Select(s => new { s.Id, s.FullName }).ToListAsync(ct);
        return rows.OrderBy(s => s.FullName, StringComparer.Ordinal).ThenBy(s => s.Id).Select(s => new FlowSpecialistItem(s.Id, s.FullName)).ToList();
    }

    public async Task<IReadOnlyList<FlowLocationItem>> ListLocationsForAsync(Guid serviceId, Guid specialistId, CancellationToken ct)
    {
        var ids = (await TriplesAsync(ct)).Where(t => t.ServiceId == serviceId && t.SpecialistId == specialistId)
            .Select(t => t.LocationId).Distinct().ToList();
        if (ids.Count == 0) return [];
        var rows = await db.Locations.AsNoTracking().Where(l => ids.Contains(l.Id))
            .Select(l => new { l.Id, l.Name, l.Address, l.Timezone, l.Latitude, l.Longitude }).ToListAsync(ct);
        return rows.OrderBy(l => l.Name, StringComparer.Ordinal).ThenBy(l => l.Id)
            .Select(l => new FlowLocationItem(l.Id, l.Name, l.Address, l.Timezone, (double?)l.Latitude, (double?)l.Longitude)).ToList();
    }

    public Task<Guid?> GetLastAppointmentLocationAsync(Guid clientId, CancellationToken ct) =>
        db.Appointments.AsNoTracking()
            .Where(a => a.ClientId == clientId
                        && (a.Status == AppointmentStatus.Pending || a.Status == AppointmentStatus.Confirmed || a.Status == AppointmentStatus.Completed))
            .OrderByDescending(a => a.StartsAt).ThenByDescending(a => a.Id)
            .Select(a => (Guid?)a.LocationId).FirstOrDefaultAsync(ct);

    public async Task<FlowNames> GetNamesAsync(Guid? serviceId, Guid? specialistId, Guid? locationId, CancellationToken ct)
    {
        string? service = null, specialist = null, location = null, address = null, tz = null;
        if (serviceId is { } sid) service = await db.Services.AsNoTracking().Where(x => x.Id == sid).Select(x => x.Name).FirstOrDefaultAsync(ct);
        if (specialistId is { } pid) specialist = await db.Specialists.AsNoTracking().Where(x => x.Id == pid).Select(x => x.FullName).FirstOrDefaultAsync(ct);
        if (locationId is { } lid)
        {
            var l = await db.Locations.AsNoTracking().Where(x => x.Id == lid).Select(x => new { x.Name, x.Address, x.Timezone }).FirstOrDefaultAsync(ct);
            (location, address, tz) = (l?.Name, l?.Address, l?.Timezone);
        }
        return new FlowNames(service, specialist, location, address, tz);
    }
}

public static class ChatFlowDataExtensions
{
    /// <summary>Каталог і сховище потоку запису. Потребує AddBeautyData і <see cref="TimeProvider"/>.</summary>
    public static IServiceCollection AddBeautyChatFlowData(this IServiceCollection services) =>
        services.AddScoped<IChatFlowCatalog, EfChatFlowCatalog>().AddScoped<IChatFlowStore, EfChatFlowStore>();
}
