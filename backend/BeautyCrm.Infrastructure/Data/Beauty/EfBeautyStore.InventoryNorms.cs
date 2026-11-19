using BeautyCrm.Application.Features.BeautyInventory;
using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BeautyCrm.Infrastructure.Data.Beauty;

// IInventoryNormsStore (ADR-025 §11, §32.16, TASK-884): норми матеріалів послуг. Усе під RLS tenant. ListInventoryLocationsAsync,
// SpecialistLocationIdsAsync, GetProductsAsync, GetEnabledMapAsync спільні з IInventoryStore (EfBeautyStore.Inventory.cs).
public sealed partial class EfBeautyStore : IInventoryNormsStore
{
    private const string MaterialScopeIndex = "ux_beauty_service_material_sets_scope";

    public async Task<IReadOnlyDictionary<Guid, ServiceRow>> GetServicesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var list = ids.ToList();
        if (list.Count == 0) return new Dictionary<Guid, ServiceRow>();
        return await db.Services.AsNoTracking().Where(s => list.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => new ServiceRow(s.Id, s.Name, s.IsActive), ct);
    }

    /// <summary>Чи входить товар у будь-яку норму (для unit_locked / purpose_locked, §32.16); викликається під блокуванням рядка товару.</summary>
    private Task<bool> ProductInNormsAsync(Guid productId, CancellationToken ct) =>
        db.ServiceMaterials.AsNoTracking().AnyAsync(m => m.ProductId == productId, ct);

    public async Task<IReadOnlyList<MaterialSetRow>> ListMaterialSetsAsync(Guid? serviceId, CancellationToken ct)
    {
        var sets = await db.ServiceMaterialSets.AsNoTracking().Where(s => serviceId == null || s.ServiceId == serviceId).ToListAsync(ct);
        if (sets.Count == 0) return [];
        var lines = await (from m in db.ServiceMaterials.AsNoTracking()
                           join s in db.ServiceMaterialSets.AsNoTracking() on m.SetId equals s.Id
                           where serviceId == null || s.ServiceId == serviceId
                           select new { m.SetId, m.ProductId, m.Quantity }).ToListAsync(ct);
        var bySet = lines.ToLookup(l => l.SetId);
        var users = await UserNamesAsync(sets.Select(s => s.UpdatedByUserId), ct);
        return [.. sets.Select(s => new MaterialSetRow(s.ServiceId, s.LocationId, s.UpdatedAt, s.UpdatedByUserId,
            users.GetValueOrDefault(s.UpdatedByUserId), [.. bySet[s.Id].Select(l => new MaterialLineRow(l.ProductId, l.Quantity))]))];
    }

    public async Task<MaterialPutResult> PutMaterialSetAsync(Guid serviceId, Guid? locationId, IReadOnlyList<MaterialLineRow> lines, Guid userId,
        DateTimeOffset now, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            async Task<MaterialPutResult> Reject(MaterialPutStatus status, string? code = null, int? line = null)
            {
                await tx.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                return new(status, null, code, line);
            }

            if (!await db.Services.AnyAsync(s => s.Id == serviceId, ct)) return await Reject(MaterialPutStatus.ServiceNotFound);
            if (locationId is { } lid && !await db.Locations.AnyAsync(l => l.Id == lid, ct)) return await Reject(MaterialPutStatus.LocationNotFound);

            // товари FOR SHARE (конфліктують з UpdateProductAsync FOR UPDATE) і повторна перевірка під блокуванням
            var ids = lines.Select(l => l.ProductId).Distinct().OrderBy(x => x).ToArray();
            var products = ids.Length == 0
                ? new Dictionary<Guid, Product>()
                : (await db.Products.FromSqlInterpolated($"SELECT * FROM beauty_products WHERE id = ANY({ids}) ORDER BY id FOR SHARE").AsNoTracking().ToListAsync(ct))
                    .ToDictionary(p => p.Id);
            var enabled = new Dictionary<Guid, bool>();
            if (locationId is { } loc && ids.Length > 0)
                enabled = (await db.ProductLocationSettings.FromSqlInterpolated(
                        $"SELECT * FROM beauty_product_location_settings WHERE location_id = {loc} AND product_id = ANY({ids}) ORDER BY product_id FOR SHARE")
                    .AsNoTracking().ToListAsync(ct)).ToDictionary(s => s.ProductId, s => s.IsEnabled);
            for (var i = 0; i < lines.Count; i++)
            {
                if (!products.TryGetValue(lines[i].ProductId, out var p)) return await Reject(MaterialPutStatus.Rejected, "product_not_found", i);
                if (!p.IsActive || p.Purpose == ProductPurpose.Sale) return await Reject(MaterialPutStatus.Rejected, "product_not_for_salon_use", i);
                if (enabled.TryGetValue(p.Id, out var on) && !on) return await Reject(MaterialPutStatus.Rejected, "product_disabled_for_location", i);
            }

            // заголовок: блокування наявного рядка або створення (гонка двох перших створень - UNIQUE, одна повторна спроба)
            var set = locationId is null
                ? (await db.ServiceMaterialSets.FromSqlInterpolated(
                    $"SELECT * FROM beauty_service_material_sets WHERE service_id = {serviceId} AND location_id IS NULL FOR UPDATE").ToListAsync(ct)).FirstOrDefault()
                : (await db.ServiceMaterialSets.FromSqlInterpolated(
                    $"SELECT * FROM beauty_service_material_sets WHERE service_id = {serviceId} AND location_id = {locationId.Value} FOR UPDATE").ToListAsync(ct)).FirstOrDefault();
            try
            {
                if (set is null)
                    db.ServiceMaterialSets.Add(set = new ServiceMaterialSet { Id = Guid.NewGuid(), ServiceId = serviceId, LocationId = locationId, UpdatedByUserId = userId, CreatedAt = now, UpdatedAt = now });
                else { set.UpdatedByUserId = userId; set.UpdatedAt = now; }
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (attempt == 0 && IsUnique(ex, MaterialScopeIndex))
            {
                await tx.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                continue;
            }

            var setId = set.Id;
            await db.ServiceMaterials.Where(m => m.SetId == setId).ExecuteDeleteAsync(ct);
            foreach (var l in lines) db.ServiceMaterials.Add(new ServiceMaterial { Id = Guid.NewGuid(), SetId = setId, ProductId = l.ProductId, Quantity = l.Quantity });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            var updatedAt = set.UpdatedAt;
            db.ChangeTracker.Clear();
            var name = (await UserNamesAsync([userId], ct)).GetValueOrDefault(userId);
            return new(MaterialPutStatus.Ok, new MaterialSetRow(serviceId, locationId, updatedAt, userId, name, [.. lines]));
        }
    }

    public async Task<bool> DeleteMaterialSetAsync(Guid serviceId, Guid? locationId, CancellationToken ct) =>
        await db.ServiceMaterialSets.Where(s => s.ServiceId == serviceId && s.LocationId == locationId).ExecuteDeleteAsync(ct) > 0;
}
