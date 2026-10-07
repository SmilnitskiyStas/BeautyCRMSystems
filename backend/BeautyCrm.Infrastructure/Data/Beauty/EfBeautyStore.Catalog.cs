using BeautyCrm.Application.Features.BeautyCatalog;
using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Ent = BeautyCrm.Infrastructure.Data.Entities;

namespace BeautyCrm.Infrastructure.Data.Beauty;

public sealed partial class EfBeautyStore
{
    // ---------- послуги й ціни ----------

    public async Task<IReadOnlyList<ServiceDto>> ListServicesAsync(bool includeInactive, CancellationToken ct)
    {
        var services = await db.Services.AsNoTracking().Where(s => includeInactive || s.IsActive).OrderBy(s => s.Name).ToListAsync(ct);
        var network = await db.ServicePrices.AsNoTracking().Where(p => p.LocationId == null)
            .ToDictionaryAsync(p => p.ServiceId, p => p.Price, ct);
        return services.Select(s => ToDto(s, network.TryGetValue(s.Id, out var v) ? v : (decimal?)null)).ToList();
    }

    public async Task<ServiceDto?> GetServiceDtoAsync(Guid id, CancellationToken ct) =>
        (await ListServicesAsync(true, ct)).FirstOrDefault(s => s.Id == id);

    async Task<ServiceDto?> ICatalogStore.GetServiceAsync(Guid id, CancellationToken ct) => await GetServiceDtoAsync(id, ct);

    public async Task<ServiceDto> CreateServiceAsync(UpsertServiceRequest r, CancellationToken ct)
    {
        var s = new Service
        {
            Id = Guid.NewGuid(), Name = r.Name.Trim(), Description = r.Description, Category = r.Category,
            DurationMinutes = r.DurationMinutes, IsActive = r.IsActive,
        };
        db.Services.Add(s);
        await db.SaveChangesAsync(ct);
        return ToDto(s, null);
    }

    public async Task<ServiceDto?> UpdateServiceAsync(Guid id, UpsertServiceRequest r, CancellationToken ct)
    {
        var s = await db.Services.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return null;
        s.Name = r.Name.Trim(); s.Description = r.Description; s.Category = r.Category;
        s.DurationMinutes = r.DurationMinutes; s.IsActive = r.IsActive;
        await db.SaveChangesAsync(ct);
        return await GetServiceDtoAsync(id, ct);
    }

    public async Task<IReadOnlyList<ServicePriceDto>> ListPricesAsync(Guid serviceId, CancellationToken ct) =>
        await db.ServicePrices.AsNoTracking().Where(p => p.ServiceId == serviceId)
            .OrderBy(p => p.LocationId).Select(p => new ServicePriceDto(p.ServiceId, p.LocationId, p.Price)).ToListAsync(ct);

    public Task<bool> LocationExistsAsync(Guid locationId, CancellationToken ct) =>
        db.Locations.AnyAsync(l => l.Id == locationId, ct);

    public async Task<ServicePriceDto> UpsertPriceAsync(Guid serviceId, Guid? locationId, decimal price, CancellationToken ct)
    {
        var existing = await db.ServicePrices.FirstOrDefaultAsync(p => p.ServiceId == serviceId && p.LocationId == locationId, ct);
        if (existing is null)
            db.ServicePrices.Add(existing = new ServicePrice { Id = Guid.NewGuid(), ServiceId = serviceId, LocationId = locationId, Price = price });
        else existing.Price = price;
        await db.SaveChangesAsync(ct);
        return new ServicePriceDto(serviceId, locationId, price);
    }

    public async Task<IReadOnlyList<PricedService>> ListPricedServicesAsync(Guid locationId, CancellationToken ct)
    {
        var services = await db.Services.AsNoTracking().Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync(ct);
        var prices = await db.ServicePrices.AsNoTracking().Where(p => p.LocationId == locationId || p.LocationId == null).ToListAsync(ct);
        var result = new List<PricedService>();
        foreach (var s in services)
        {
            var price = prices.FirstOrDefault(p => p.ServiceId == s.Id && p.LocationId == locationId)?.Price
                        ?? prices.FirstOrDefault(p => p.ServiceId == s.Id && p.LocationId == null)?.Price;
            if (price is not null) result.Add(new PricedService(s.Id, s.Name, price.Value));
        }
        return result;
    }

    private static ServiceDto ToDto(Service s, decimal? networkPrice) =>
        new(s.Id, s.Name, s.Description, s.Category, s.DurationMinutes, s.IsActive, networkPrice);

    // ---------- акції ----------

    public Task<IReadOnlyList<PromotionDto>> ListPromotionsAsync(CancellationToken ct) => LoadPromotionsAsync(null, false, ct);

    public async Task<PromotionDto?> GetPromotionAsync(Guid id, CancellationToken ct) =>
        (await LoadPromotionsAsync(id, false, ct)).FirstOrDefault();

    public async Task<bool> ReferencesExistAsync(IReadOnlyCollection<Guid> locationIds, IReadOnlyCollection<Guid> serviceIds, CancellationToken ct)
    {
        var locs = locationIds.Distinct().ToList();
        var svcs = serviceIds.Distinct().ToList();
        return await db.Locations.CountAsync(l => locs.Contains(l.Id), ct) == locs.Count
            && await db.Services.CountAsync(s => svcs.Contains(s.Id), ct) == svcs.Count;
    }

    public async Task<PromotionDto> CreatePromotionAsync(UpsertPromotionRequest r, CancellationToken ct)
    {
        var p = new Promotion { Id = Guid.NewGuid() };
        Apply(p, r);
        db.Promotions.Add(p);
        AddJunctions(p.Id, r);
        await db.SaveChangesAsync(ct);
        return (await GetPromotionAsync(p.Id, ct))!;
    }

    public async Task<PromotionDto?> UpdatePromotionAsync(Guid id, UpsertPromotionRequest r, CancellationToken ct)
    {
        var p = await db.Promotions.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return null;
        Apply(p, r);
        db.PromotionLocations.RemoveRange(await db.PromotionLocations.Where(x => x.PromotionId == id).ToListAsync(ct));
        db.PromotionServices.RemoveRange(await db.PromotionServices.Where(x => x.PromotionId == id).ToListAsync(ct));
        AddJunctions(id, r);
        await db.SaveChangesAsync(ct);
        return await GetPromotionAsync(id, ct);
    }

    private static void Apply(Promotion p, UpsertPromotionRequest r)
    {
        p.Name = r.Name.Trim(); p.Description = r.Description;
        p.DiscountType = EnumText<DiscountType>.FromDb(r.DiscountType); p.DiscountValue = r.DiscountValue;
        p.StartsAt = r.StartsAt; p.EndsAt = r.EndsAt; p.IsActive = r.IsActive;
    }

    private void AddJunctions(Guid promotionId, UpsertPromotionRequest r)
    {
        foreach (var l in r.LocationIds ?? [])
            db.PromotionLocations.Add(new PromotionLocation { PromotionId = promotionId, LocationId = l });
        foreach (var s in r.ServiceIds ?? [])
            db.PromotionServices.Add(new Ent.PromotionService { PromotionId = promotionId, ServiceId = s });
    }

    private async Task<IReadOnlyList<PromotionDto>> LoadPromotionsAsync(Guid? id, bool activeOnly, CancellationToken ct)
    {
        var promos = await db.Promotions.AsNoTracking()
            .Where(p => (id == null || p.Id == id) && (!activeOnly || p.IsActive)).OrderBy(p => p.Name).ToListAsync(ct);
        var ids = promos.Select(p => p.Id).ToList();
        var locs = (await db.PromotionLocations.AsNoTracking().Where(x => ids.Contains(x.PromotionId))
            .Select(x => new { x.PromotionId, x.LocationId }).ToListAsync(ct)).ToLookup(x => x.PromotionId, x => x.LocationId);
        var svcs = (await db.PromotionServices.AsNoTracking().Where(x => ids.Contains(x.PromotionId))
            .Select(x => new { x.PromotionId, x.ServiceId }).ToListAsync(ct)).ToLookup(x => x.PromotionId, x => x.ServiceId);
        return promos.Select(p => new PromotionDto(p.Id, p.Name, p.Description, EnumText<DiscountType>.ToDb(p.DiscountType),
            p.DiscountValue, p.StartsAt, p.EndsAt, p.IsActive, locs[p.Id].ToList(), svcs[p.Id].ToList())).ToList();
    }
}
