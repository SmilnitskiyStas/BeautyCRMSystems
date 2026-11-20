using BeautyCrm.Application.Features.BeautyInventory;
using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BeautyCrm.Infrastructure.Data.Beauty;

// IInventoryStore (ADR-025, §32, TASK-877): каталог, довідники й читання. Усе під RLS tenant. Операції й інвентаризації - EfBeautyStore.InventoryOps.cs.
public sealed partial class EfBeautyStore : IInventoryStore
{
    private const string BarcodeIndex = "ux_beauty_products_barcode";
    private const string SupplierNameIndex = "ux_beauty_suppliers_name";
    private const string IdempotencyIndex = "ux_beauty_stock_operations_idempotency";
    private const string OpenStocktakeIndex = "ux_beauty_stocktakes_open";

    private static bool IsUnique(Exception? ex, string index) =>
        ex is DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg } && pg.ConstraintName == index
        || ex is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: var c } && c == index;

    // ---------- довідники ----------

    public async Task<IReadOnlyList<LocationRow>> ListInventoryLocationsAsync(CancellationToken ct) =>
        await db.Locations.AsNoTracking().OrderBy(l => l.Name).ThenBy(l => l.Id)
            .Select(l => new LocationRow(l.Id, l.Name, l.IsActive, l.Timezone)).ToListAsync(ct);

    public async Task<IReadOnlyList<Guid>> SpecialistLocationIdsAsync(Guid specialistId, CancellationToken ct) =>
        await db.SpecialistLocations.AsNoTracking()
            .Where(sl => sl.SpecialistId == specialistId && sl.IsActive && db.Specialists.Any(s => s.Id == specialistId && s.IsActive))
            .Select(sl => sl.LocationId).Distinct().ToListAsync(ct);

    // ---------- мапінг ----------

    private static ProductRef ToRef(Product p) =>
        new(p.Id, p.Name, EnumText<ProductUnit>.ToDb(p.Unit), EnumText<ProductPurpose>.ToDb(p.Purpose), p.Category, p.Barcode, p.IsActive);

    private static ProductDto ToDto(Product p, IReadOnlyDictionary<Guid, string> suppliers, bool hasMovements, IReadOnlyDictionary<Guid, string> categoryPaths) =>
        new(p.Id, p.Name, p.Category, EnumText<ProductUnit>.ToDb(p.Unit), p.Barcode, EnumText<ProductPurpose>.ToDb(p.Purpose),
            p.SupplierId is { } s && suppliers.TryGetValue(s, out var sn) ? new InventoryNamedRef(s, sn) : null,
            p.PurchasePrice, p.SalePrice, p.MinStock, p.TrackExpiry, p.IsActive, p.Note, hasMovements, p.CreatedAt, p.UpdatedAt,
            p.CategoryId, p.CategoryId is { } c && categoryPaths.TryGetValue(c, out var path) ? path : null);

    private async Task<Dictionary<Guid, string>> SupplierNamesAsync(CancellationToken ct) =>
        await db.Suppliers.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name, ct);

    private async Task<Dictionary<Guid, string>> UserNamesAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return list.Count == 0 ? [] : await db.Users.AsNoTracking().Where(u => list.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
    }

    private static InventoryNamedRef? Named(Guid? id, IReadOnlyDictionary<Guid, string> names) =>
        id is { } i && names.TryGetValue(i, out var n) ? new InventoryNamedRef(i, n) : null;

    // ---------- товари ----------

    public async Task<IReadOnlyList<ProductDto>> ListProductsAsync(CancellationToken ct)
    {
        var products = await db.Products.AsNoTracking().ToListAsync(ct);
        var suppliers = await SupplierNamesAsync(ct);
        var paths = await CategoryPathsAsync(ct);
        return [.. products.Select(p => ToDto(p, suppliers, false, paths))];
    }

    /// <summary>EXISTS по індексу (tenant, product, occurred_at) лише для переданих id (сторінка), без сканування всього журналу.</summary>
    public async Task<IReadOnlySet<Guid>> ProductIdsWithMovementsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var list = ids.ToList();
        if (list.Count == 0) return new HashSet<Guid>();
        return (await db.Products.AsNoTracking().Where(p => list.Contains(p.Id) && db.StockMovements.Any(m => m.ProductId == p.Id))
            .Select(p => p.Id).ToListAsync(ct)).ToHashSet();
    }

    public async Task<IReadOnlyDictionary<Guid, ProductDto>> GetProductsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var list = ids.ToList();
        var products = await db.Products.AsNoTracking().Where(p => list.Contains(p.Id)).ToListAsync(ct);
        var suppliers = await SupplierNamesAsync(ct);
        var paths = await CategoryPathsAsync(ct);
        return products.ToDictionary(p => p.Id, p => ToDto(p, suppliers, false, paths));
    }

    public async Task<ProductDetailDto?> GetProductAsync(Guid id, CancellationToken ct)
    {
        var p = await db.Products.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return null;
        var suppliers = await SupplierNamesAsync(ct);
        var moved = await db.StockMovements.AsNoTracking().AnyAsync(m => m.ProductId == id, ct);
        var settings = await db.ProductLocationSettings.AsNoTracking().Where(s => s.ProductId == id).OrderBy(s => s.LocationId)
            .Select(s => new LocationSettingDto(s.LocationId, s.MinStock, s.IsEnabled, s.UpdatedAt)).ToListAsync(ct);
        var d = ToDto(p, suppliers, moved, await CategoryPathsAsync(ct));
        return new ProductDetailDto(d.Id, d.Name, d.Category, d.Unit, d.Barcode, d.Purpose, d.Supplier, d.PurchasePrice, d.SalePrice, d.MinStock, d.TrackExpiry,
            d.IsActive, d.Note, d.HasMovements, d.CreatedAt, d.UpdatedAt, settings, d.CategoryId, d.CategoryPath);
    }

    public async Task<ProductDto?> GetProductByBarcodeAsync(string code, CancellationToken ct)
    {
        var p = await db.Products.AsNoTracking().FirstOrDefaultAsync(x => x.Barcode == code, ct);
        return p is null ? null : (await ListProductsDtoAsync(p, ct));
    }

    private async Task<ProductDto> ListProductsDtoAsync(Product p, CancellationToken ct) =>
        ToDto(p, await SupplierNamesAsync(ct), await db.StockMovements.AsNoTracking().AnyAsync(m => m.ProductId == p.Id, ct), await CategoryPathsAsync(ct));

    public async Task<IReadOnlyList<string>> ListCategoriesAsync(CancellationToken ct) =>
        [.. (await db.Products.AsNoTracking().Where(p => p.Category != null && p.Category != "").Select(p => p.Category!).Distinct().ToListAsync(ct))
            .OrderBy(c => c, StringComparer.Ordinal)];

    private static void Apply(Product p, ProductData d)
    {
        p.Name = d.Name; p.Category = d.Category; p.Unit = EnumText<ProductUnit>.FromDb(d.Unit); p.Barcode = d.Barcode;
        p.Purpose = EnumText<ProductPurpose>.FromDb(d.Purpose); p.SupplierId = d.SupplierId; p.PurchasePrice = d.PurchasePrice; p.SalePrice = d.SalePrice; p.CategoryId = d.CategoryId;
        p.MinStock = d.MinStock; p.TrackExpiry = d.TrackExpiry; p.IsActive = d.IsActive; p.Note = d.Note;
    }

    public async Task<ProductWriteResult> CreateProductAsync(ProductData data, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        var p = new Product { Id = Guid.NewGuid(), UpdatedByUserId = userId, CreatedAt = now, UpdatedAt = now };
        Apply(p, data);
        db.Products.Add(p);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (IsUnique(ex, BarcodeIndex)) { db.ChangeTracker.Clear(); return new(CatalogWriteOutcome.BarcodeTaken); }
        catch (DbUpdateException ex) when (IsCategoryFk(ex)) { db.ChangeTracker.Clear(); return new(CatalogWriteOutcome.CategoryNotFound); }
        return new(CatalogWriteOutcome.Ok, await GetProductAsync(p.Id, ct));
    }

    /// <summary>
    /// Оновлення під блокуванням рядка товару (FOR UPDATE): зміна одиниці перевіряється проти журналу ПІД цим блокуванням, а операції з рухами
    /// беруть той самий рядок FOR SHARE (<see cref="LockProductsAsync"/>) - гонка «перше надходження vs зміна одиниці» неможлива (TASK-880 L3).
    /// </summary>
    public async Task<ProductWriteResult> UpdateProductAsync(Guid id, ProductData data, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var p = (await db.Products.FromSqlInterpolated($"SELECT * FROM beauty_products WHERE id = {id} FOR UPDATE").ToListAsync(ct)).FirstOrDefault();
        if (p is null) { await tx.RollbackAsync(ct); db.ChangeTracker.Clear(); return new(CatalogWriteOutcome.NotFound); }
        var unitChanged = p.Unit != EnumText<ProductUnit>.FromDb(data.Unit);
        var toSale = p.Purpose != ProductPurpose.Sale && EnumText<ProductPurpose>.FromDb(data.Purpose) == ProductPurpose.Sale;
        var inNorms = (unitChanged || toSale) && await ProductInNormsAsync(id, ct); // §32.16: unit_locked / purpose_locked (під блокуванням рядка товару)
        if (unitChanged && (inNorms || await db.StockMovements.AsNoTracking().AnyAsync(m => m.ProductId == id, ct)))
        {
            await tx.RollbackAsync(ct); db.ChangeTracker.Clear();
            return new(CatalogWriteOutcome.UnitLocked);
        }
        if (toSale && inNorms)
        {
            await tx.RollbackAsync(ct); db.ChangeTracker.Clear();
            return new(CatalogWriteOutcome.PurposeLocked);
        }
        Apply(p, data);
        p.UpdatedByUserId = userId;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (IsUnique(ex, BarcodeIndex)) { db.ChangeTracker.Clear(); return new(CatalogWriteOutcome.BarcodeTaken); }
        catch (DbUpdateException ex) when (IsCategoryFk(ex)) { db.ChangeTracker.Clear(); return new(CatalogWriteOutcome.CategoryNotFound); }
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return new(CatalogWriteOutcome.Ok, await GetProductAsync(id, ct));
    }

    public async Task<LocationSettingDto?> UpsertSettingAsync(Guid productId, Guid locationId, decimal? minStock, bool isEnabled, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        if (!await db.Products.AnyAsync(p => p.Id == productId, ct)) return null;
        for (var attempt = 0; ; attempt++)
        {
            var s = await db.ProductLocationSettings.FirstOrDefaultAsync(x => x.ProductId == productId && x.LocationId == locationId, ct);
            if (s is null) db.ProductLocationSettings.Add(s = new ProductLocationSetting { Id = Guid.NewGuid(), ProductId = productId, LocationId = locationId });
            s.MinStock = minStock; s.IsEnabled = isEnabled; s.UpdatedByUserId = userId;
            try
            {
                await db.SaveChangesAsync(ct);
                return new LocationSettingDto(locationId, s.MinStock, s.IsEnabled, s.UpdatedAt);
            }
            catch (DbUpdateException ex) when (attempt == 0 && ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                db.ChangeTracker.Clear(); // паралельне створення: друга спроба оновить наявний рядок
            }
        }
    }

    public async Task<bool> DeleteSettingAsync(Guid productId, Guid locationId, CancellationToken ct) =>
        await db.ProductLocationSettings.Where(s => s.ProductId == productId && s.LocationId == locationId).ExecuteDeleteAsync(ct) > 0;

    public async Task<IReadOnlyDictionary<Guid, bool>> GetEnabledMapAsync(Guid locationId, IReadOnlyCollection<Guid> productIds, CancellationToken ct)
    {
        var ids = productIds.ToList();
        return await db.ProductLocationSettings.AsNoTracking().Where(s => s.LocationId == locationId && ids.Contains(s.ProductId))
            .ToDictionaryAsync(s => s.ProductId, s => s.IsEnabled, ct);
    }

    // ---------- постачальники ----------

    private async Task<SupplierDto?> SupplierDtoAsync(Guid id, CancellationToken ct)
    {
        var s = await db.Suppliers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return null;
        var count = await db.Products.AsNoTracking().CountAsync(p => p.SupplierId == id, ct);
        return new SupplierDto(s.Id, s.Name, s.ContactName, s.Phone, s.Email, s.Note, s.IsActive, count);
    }

    public async Task<IReadOnlyList<SupplierDto>> ListSuppliersAsync(CancellationToken ct)
    {
        var counts = await db.Products.AsNoTracking().Where(p => p.SupplierId != null).GroupBy(p => p.SupplierId!.Value)
            .Select(g => new { Id = g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.N, ct);
        return [.. (await db.Suppliers.AsNoTracking().ToListAsync(ct))
            .Select(s => new SupplierDto(s.Id, s.Name, s.ContactName, s.Phone, s.Email, s.Note, s.IsActive, counts.GetValueOrDefault(s.Id)))];
    }

    public Task<SupplierDto?> GetSupplierAsync(Guid id, CancellationToken ct) => SupplierDtoAsync(id, ct);

    public async Task<SupplierWriteResult> CreateSupplierAsync(SupplierData d, DateTimeOffset now, CancellationToken ct)
    {
        var s = new Supplier { Id = Guid.NewGuid(), CreatedAt = now, UpdatedAt = now };
        ApplySupplier(s, d);
        db.Suppliers.Add(s);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (IsUnique(ex, SupplierNameIndex)) { db.ChangeTracker.Clear(); return new(CatalogWriteOutcome.NameTaken); }
        return new(CatalogWriteOutcome.Ok, await SupplierDtoAsync(s.Id, ct));
    }

    public async Task<SupplierWriteResult> UpdateSupplierAsync(Guid id, SupplierData d, DateTimeOffset now, CancellationToken ct)
    {
        var s = await db.Suppliers.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return new(CatalogWriteOutcome.NotFound);
        ApplySupplier(s, d);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (IsUnique(ex, SupplierNameIndex)) { db.ChangeTracker.Clear(); return new(CatalogWriteOutcome.NameTaken); }
        return new(CatalogWriteOutcome.Ok, await SupplierDtoAsync(id, ct));
    }

    private static void ApplySupplier(Supplier s, SupplierData d)
    {
        s.Name = d.Name; s.ContactName = d.ContactName; s.Phone = d.Phone; s.Email = d.Email; s.Note = d.Note; s.IsActive = d.IsActive;
    }

    // ---------- залишки ----------

    private sealed class Pair
    {
        public Product P = null!;
        public Guid LocationId;
        public decimal Qty;
        public decimal Shortage;
        public decimal? SettingMin;
        public bool Enabled;
    }

    private static string Like(string s) => "%" + s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

    /// <summary>Пари (товар, заклад): рівень і налаштування приєднані в БД; відсутній рівень = 0, відсутнє налаштування = увімкнено.</summary>
    private IQueryable<Pair> Pairs(IReadOnlyList<Guid> locIds) => Pairs([.. locIds], 0);

    private IQueryable<Pair> Pairs(List<Guid> locs, int _) =>
        from p in db.Products
        from loc in db.Locations.Where(x => locs.Contains(x.Id))
        join l in db.StockLevels on new { L = loc.Id, P = p.Id } equals new { L = l.LocationId, P = l.ProductId } into lj
        from l in lj.DefaultIfEmpty()
        join s in db.ProductLocationSettings on new { L = loc.Id, P = p.Id } equals new { L = s.LocationId, P = s.ProductId } into sj
        from s in sj.DefaultIfEmpty()
        select new Pair { P = p, LocationId = loc.Id, Qty = l == null ? 0m : l.Quantity, Shortage = l == null ? 0m : l.ShortageQuantity, SettingMin = s == null ? null : s.MinStock, Enabled = s == null || s.IsEnabled };

    /// <summary>Спільні фільтри каталогу (категорія, призначення, пошук) і «кандидат у залишки»; null = порожній результат (невідоме призначення).</summary>
    private static IQueryable<Pair>? FilterPairs(IQueryable<Pair> q, string? category, string? purpose, string? search)
    {
        q = q.Where(x => x.Qty > 0 || x.Shortage > 0 || (x.P.IsActive && x.Enabled));
        if (category is not null) q = q.Where(x => x.P.Category == category);
        if (purpose is not null)
        {
            if (!EnumText<ProductPurpose>.Values.Contains(purpose)) return null;
            var pu = EnumText<ProductPurpose>.FromDb(purpose);
            q = q.Where(x => x.P.Purpose == pu);
        }
        if (search is not null)
        {
            var pat = Like(search);
            q = q.Where(x => EF.Functions.ILike(x.P.Name, pat, "\\") || (x.P.Barcode != null && EF.Functions.ILike(x.P.Barcode, pat, "\\"))
                || (x.P.Category != null && EF.Functions.ILike(x.P.Category, pat, "\\")));
        }
        return q;
    }

    /// <summary>Сторінка (lower(name), id) серед відфільтрованих ключів; ключі легкі (id + name), важкі агрегати рахуються лише для сторінки.</summary>
    private static List<Guid> PageKeys(IEnumerable<(Guid Id, string Name)> keys, string[]? after, int take)
    {
        var ordered = keys.OrderBy(k => k.Name.ToLowerInvariant(), StringComparer.Ordinal).ThenBy(k => k.Id).AsEnumerable();
        if (after is not null)
        {
            var an = after[0]; var aid = Guid.Parse(after[1]); // частини перевірено DecodeCursor
            ordered = ordered.Where(k =>
            {
                var c = string.CompareOrdinal(k.Name.ToLowerInvariant(), an);
                return c > 0 || (c == 0 && k.Id.CompareTo(aid) > 0);
            });
        }
        return [.. ordered.Take(take).Select(k => k.Id)];
    }

    public async Task<IReadOnlyList<StockRaw>> ListStockAsync(StockFilter f, string[]? after, int take, CancellationToken ct)
    {
        var loc = f.LocationId; var today = f.Today; var limit = today.AddDays(f.ExpiringWithinDays);
        if (FilterPairs(Pairs([loc]), f.Category, f.Purpose, f.Search) is not { } q) return [];
        switch (f.State)
        {
            case "low": q = q.Where(x => (x.SettingMin ?? x.P.MinStock) > 0 && x.Qty - x.Shortage < (x.SettingMin ?? x.P.MinStock)); break;
            case "in_stock": q = q.Where(x => x.Qty > 0); break;
            case "out_of_stock": q = q.Where(x => x.Qty <= 0); break;
        }
        if (f.OnlyExpiring)
            q = q.Where(x => db.StockBatches.Any(b => b.LocationId == loc && b.ProductId == x.P.Id && b.Quantity > 0 && b.ExpiresOn != null && b.ExpiresOn >= today && b.ExpiresOn <= limit));

        var keys = (await q.Select(x => new { x.P.Id, x.P.Name }).ToListAsync(ct)).Select(k => (k.Id, k.Name));
        var ids = PageKeys(keys, after, take);
        if (ids.Count == 0) return [];

        var rows = (await Pairs([loc]).Where(x => ids.Contains(x.P.Id)).ToListAsync(ct)).ToDictionary(x => x.P.Id);
        var agg = (await db.StockBatches.AsNoTracking().Where(b => b.LocationId == loc && ids.Contains(b.ProductId) && b.Quantity > 0)
            .GroupBy(b => b.ProductId)
            .Select(g => new
            {
                Id = g.Key,
                Nearest = g.Min(b => b.ExpiresOn),
                Expired = g.Sum(b => b.ExpiresOn < today ? b.Quantity : 0m),
                Expiring = g.Sum(b => b.ExpiresOn >= today && b.ExpiresOn <= limit ? b.Quantity : 0m),
                Value = g.Sum(b => b.UnitCost != null ? b.Quantity * b.UnitCost.Value : 0m),
                NoCost = g.Sum(b => b.UnitCost == null ? 1 : 0),
            }).ToListAsync(ct)).ToDictionary(x => x.Id);

        return [.. ids.Select(id =>
        {
            var r = rows[id];
            var a = agg.GetValueOrDefault(id);
            return new StockRaw(ToRef(r.P), r.Qty, r.P.MinStock, r.SettingMin, r.Enabled, a?.Nearest, a?.Expired ?? 0, a?.Expiring ?? 0,
                Money(a?.Value ?? 0), (a?.NoCost ?? 0) > 0, r.Shortage);
        })];
    }

    public async Task<IReadOnlyList<SummaryRaw>> ListSummaryAsync(SummaryFilter f, string[]? after, int take, CancellationToken ct)
    {
        var locs = f.LocationIds;
        if (FilterPairs(Pairs(locs), f.Category, f.Purpose, f.Search) is not { } q) return [];
        if (f.LowOnly) q = q.Where(x => (x.SettingMin ?? x.P.MinStock) > 0 && x.Qty - x.Shortage < (x.SettingMin ?? x.P.MinStock));

        var keys = (await q.Select(x => new { x.P.Id, x.P.Name }).Distinct().ToListAsync(ct)).Select(k => (k.Id, k.Name));
        var ids = PageKeys(keys, after, take);
        if (ids.Count == 0) return [];

        var order = locs.Select((l, i) => (l, i)).ToDictionary(x => x.l, x => x.i);
        var pairs = await FilterPairs(Pairs(locs), null, null, null)!.Where(x => ids.Contains(x.P.Id)).ToListAsync(ct);
        var agg = (await db.StockBatches.AsNoTracking().Where(b => locs.Contains(b.LocationId) && ids.Contains(b.ProductId) && b.Quantity > 0)
            .GroupBy(b => new { b.LocationId, b.ProductId })
            .Select(g => new
            {
                g.Key.LocationId, g.Key.ProductId,
                Value = g.Sum(b => b.UnitCost != null ? b.Quantity * b.UnitCost.Value : 0m),
                NoCost = g.Sum(b => b.UnitCost == null ? 1 : 0),
            }).ToListAsync(ct)).ToDictionary(x => (x.LocationId, x.ProductId));

        var position = ids.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        return [.. pairs.OrderBy(r => position[r.P.Id]).ThenBy(r => order[r.LocationId]).Select(r =>
        {
            agg.TryGetValue((r.LocationId, r.P.Id), out var a);
            return new SummaryRaw(ToRef(r.P), r.LocationId, r.Qty, r.P.MinStock, r.SettingMin, r.Enabled, Money(a?.Value ?? 0), (a?.NoCost ?? 0) > 0, r.Shortage);
        })];
    }

    private static decimal Money(decimal v) => decimal.Round(v, 2, MidpointRounding.AwayFromZero);

    public async Task<IReadOnlyDictionary<Guid, BatchRef>> GetBatchRefsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var list = ids.ToList();
        return await db.StockBatches.AsNoTracking().Where(b => list.Contains(b.Id))
            .ToDictionaryAsync(b => b.Id, b => new BatchRef(b.Id, b.LocationId, b.ProductId, b.ExpiresOn), ct);
    }

    public async Task<IReadOnlyList<BatchDto>> ListBatchesAsync(BatchFilter f, string[]? after, int take, CancellationToken ct)
    {
        var merged = new List<(StockBatch B, DateOnly Today)>();
        foreach (var (ids, today) in f.Locations)
        {
            var limit = today.AddDays(f.ExpiringWithinDays);
            var q = db.StockBatches.AsNoTracking().Where(b => ids.Contains(b.LocationId));
            if (f.ProductId is { } pid) q = q.Where(b => b.ProductId == pid);
            q = f.Status switch
            {
                "expiring" => q.Where(b => b.Quantity > 0 && b.ExpiresOn != null && b.ExpiresOn >= today && b.ExpiresOn <= limit),
                "expired" => q.Where(b => b.Quantity > 0 && b.ExpiresOn != null && b.ExpiresOn < today),
                "empty" => q.Where(b => b.Quantity == 0),
                "all" => q,
                _ => q.Where(b => b.Quantity > 0),
            };
            if (after is not null)
            {
                var receivedAt = new DateTimeOffset(long.Parse(after[2], System.Globalization.CultureInfo.InvariantCulture), TimeSpan.Zero);
                var locId = Guid.Parse(after[3]);
                var id = Guid.Parse(after[4]);
                if (after[0] == "1")
                    q = q.Where(b => b.ExpiresOn == null && (b.ReceivedAt > receivedAt
                        || (b.ReceivedAt == receivedAt && (b.LocationId.CompareTo(locId) > 0 || (b.LocationId == locId && b.Id.CompareTo(id) > 0)))));
                else
                {
                    var exp = DateOnly.ParseExact(after[1], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                    q = q.Where(b => b.ExpiresOn == null || b.ExpiresOn > exp
                        || (b.ExpiresOn == exp && (b.ReceivedAt > receivedAt
                            || (b.ReceivedAt == receivedAt && (b.LocationId.CompareTo(locId) > 0 || (b.LocationId == locId && b.Id.CompareTo(id) > 0))))));
                }
            }
            var rows = await q.OrderBy(b => b.ExpiresOn == null).ThenBy(b => b.ExpiresOn).ThenBy(b => b.ReceivedAt).ThenBy(b => b.LocationId).ThenBy(b => b.Id).Take(take).ToListAsync(ct);
            merged.AddRange(rows.Select(r => (r, today)));
        }
        var page = merged.OrderBy(x => x.B.ExpiresOn == null).ThenBy(x => x.B.ExpiresOn).ThenBy(x => x.B.ReceivedAt)
            .ThenBy(x => x.B.LocationId.ToString("D"), StringComparer.Ordinal).ThenBy(x => x.B.Id.ToString("D"), StringComparer.Ordinal).Take(take).ToList();

        var productIds = page.Select(x => x.B.ProductId).Distinct().ToList();
        var products = await db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var suppliers = await SupplierNamesAsync(ct);
        return [.. page.Select(x =>
        {
            var b = x.B;
            int? days = b.ExpiresOn is { } e ? e.DayNumber - x.Today.DayNumber : null;
            var status = b.Quantity <= 0 ? "empty" : days is null ? "active" : days < 0 ? "expired" : days <= f.ExpiringWithinDays ? "expiring" : "active";
            return new BatchDto(b.Id, ToRef(products[b.ProductId]), b.LocationId, b.ReceivedAt, b.ExpiresOn, b.LotNumber, b.InitialQuantity, b.Quantity,
                b.UnitCost, Named(b.SupplierId, suppliers), b.OperationId, b.SourceBatchId, status, days);
        })];
    }

    // ---------- журнал і операції ----------

    private async Task<List<MovementDto>> MapMovementsAsync(IReadOnlyList<StockMovement> moves, CancellationToken ct)
    {
        if (moves.Count == 0) return [];
        var opIds = moves.Select(m => m.OperationId).Distinct().ToList();
        var ops = await db.StockOperations.AsNoTracking().Where(o => opIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, ct);
        var pids = moves.Select(m => m.ProductId).Distinct().ToList();
        var products = await db.Products.AsNoTracking().Where(p => pids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var users = await UserNamesAsync(moves.Where(m => m.CreatedByUserId != null).Select(m => m.CreatedByUserId!.Value), ct);
        return [.. moves.Select(m =>
        {
            var op = ops[m.OperationId];
            var counterpart = m.Type == StockMovementType.TransferOut ? op.ToLocationId : m.Type == StockMovementType.TransferIn ? op.LocationId : null;
            return new MovementDto(m.Id, m.OperationId, EnumText<StockOperationKind>.ToDb(op.Kind), EnumText<StockMovementType>.ToDb(m.Type), m.OccurredAt,
                m.LocationId, ToRef(products[m.ProductId]), m.BatchId, m.Quantity, m.UnitCost,
                op.Reason is { } r ? EnumText<WriteOffReason>.ToDb(r) : null, op.Note, counterpart, op.StocktakeId, Named(m.CreatedByUserId, users), m.IsShortage);
        })];
    }

    public async Task<IReadOnlyList<MovementDto>> ListMovementsAsync(MovementFilter f, string[]? after, int take, CancellationToken ct)
    {
        var q = db.StockMovements.AsNoTracking().Where(m => m.OccurredAt >= f.From && m.OccurredAt <= f.To);
        if (f.LocationIds is { } ids) q = q.Where(m => ids.Contains(m.LocationId));
        if (f.ProductId is { } pid) q = q.Where(m => m.ProductId == pid);
        if (f.OperationId is { } oid) q = q.Where(m => m.OperationId == oid);
        if (f.Type is { } t)
        {
            if (!EnumText<StockMovementType>.Values.Contains(t)) return [];
            var type = EnumText<StockMovementType>.FromDb(t);
            q = q.Where(m => m.Type == type);
        }
        if (after is not null)
        {
            var at = new DateTimeOffset(long.Parse(after[0], System.Globalization.CultureInfo.InvariantCulture), TimeSpan.Zero);
            var id = Guid.Parse(after[1]);
            q = q.Where(m => m.OccurredAt < at || (m.OccurredAt == at && m.Id.CompareTo(id) < 0));
        }
        var rows = await q.OrderByDescending(m => m.OccurredAt).ThenByDescending(m => m.Id).Take(take).ToListAsync(ct);
        return await MapMovementsAsync(rows, ct);
    }

    private static decimal? TotalCost(IEnumerable<StockMovement> moves, StockOperationKind kind)
    {
        var priced = moves.Where(m => m.UnitCost != null && (kind != StockOperationKind.Transfer || m.Type == StockMovementType.TransferOut)).ToList();
        return priced.Count == 0 ? null : Money(priced.Sum(m => Math.Abs(m.Quantity) * m.UnitCost!.Value));
    }

    public async Task<IReadOnlyList<OperationSummaryDto>> ListOperationsAsync(OperationFilter f, string[]? after, int take, CancellationToken ct)
    {
        var q = db.StockOperations.AsNoTracking().AsQueryable();
        if (f.LocationIds is { } ids) q = q.Where(o => ids.Contains(o.LocationId) || (o.ToLocationId != null && ids.Contains(o.ToLocationId.Value)));
        if (f.LocationId is { } lid) q = q.Where(o => o.LocationId == lid || o.ToLocationId == lid);
        if (f.Kind is { } k)
        {
            if (!EnumText<StockOperationKind>.Values.Contains(k)) return [];
            var kind = EnumText<StockOperationKind>.FromDb(k);
            q = q.Where(o => o.Kind == kind);
        }
        if (f.From is { } from) q = q.Where(o => o.CreatedAt >= from);
        if (f.To is { } to) q = q.Where(o => o.CreatedAt <= to);
        if (after is not null)
        {
            var at = new DateTimeOffset(long.Parse(after[0], System.Globalization.CultureInfo.InvariantCulture), TimeSpan.Zero);
            var id = Guid.Parse(after[1]);
            q = q.Where(o => o.CreatedAt < at || (o.CreatedAt == at && o.Id.CompareTo(id) < 0));
        }
        var ops = await q.OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id).Take(take).ToListAsync(ct);
        var opIds = ops.Select(o => o.Id).ToList();
        var moves = (await db.StockMovements.AsNoTracking().Where(m => opIds.Contains(m.OperationId)).ToListAsync(ct)).ToLookup(m => m.OperationId);
        var users = await UserNamesAsync(ops.Where(o => o.CreatedByUserId != null).Select(o => o.CreatedByUserId!.Value), ct);
        return [.. ops.Select(o => new OperationSummaryDto(o.Id, EnumText<StockOperationKind>.ToDb(o.Kind), o.LocationId, o.ToLocationId,
            o.Reason is { } r ? EnumText<WriteOffReason>.ToDb(r) : null, o.DocumentNumber, o.CreatedAt, Named(o.CreatedByUserId, users),
            moves[o.Id].Select(m => m.ProductId).Distinct().Count(), TotalCost(moves[o.Id], o.Kind), o.AppointmentId))];
    }

    public async Task<OperationDto?> GetOperationAsync(Guid id, CancellationToken ct)
    {
        var o = await db.StockOperations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (o is null) return null;
        var moves = await db.StockMovements.AsNoTracking().Where(m => m.OperationId == id).OrderBy(m => m.OccurredAt).ThenBy(m => m.Id).ToListAsync(ct);
        var users = await UserNamesAsync(o.CreatedByUserId is { } u ? [u] : [], ct);
        var suppliers = o.SupplierId is null ? [] : await SupplierNamesAsync(ct);
        return new OperationDto(o.Id, EnumText<StockOperationKind>.ToDb(o.Kind), o.LocationId, o.ToLocationId,
            o.Reason is { } r ? EnumText<WriteOffReason>.ToDb(r) : null, o.DocumentNumber, o.CreatedAt, Named(o.CreatedByUserId, users),
            moves.Select(m => m.ProductId).Distinct().Count(), TotalCost(moves, o.Kind), Named(o.SupplierId, suppliers), o.ReceivedOn, o.Note, o.StocktakeId,
            await MapMovementsAsync(moves, ct), o.AppointmentId);
    }

    public async Task<KeyedOperation?> FindOperationByKeyAsync(string keyHash, CancellationToken ct)
    {
        var o = await db.StockOperations.AsNoTracking().Where(x => x.IdempotencyKeyHash == keyHash).Select(x => new { x.Id, x.IdempotencyRequestHash }).FirstOrDefaultAsync(ct);
        if (o is null) return null;
        return new KeyedOperation((await GetOperationAsync(o.Id, ct))!, o.IdempotencyRequestHash ?? "");
    }
}
