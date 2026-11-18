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

    private static ProductDto ToDto(Product p, IReadOnlyDictionary<Guid, string> suppliers, bool hasMovements) =>
        new(p.Id, p.Name, p.Category, EnumText<ProductUnit>.ToDb(p.Unit), p.Barcode, EnumText<ProductPurpose>.ToDb(p.Purpose),
            p.SupplierId is { } s && suppliers.TryGetValue(s, out var sn) ? new InventoryNamedRef(s, sn) : null,
            p.PurchasePrice, p.MinStock, p.TrackExpiry, p.IsActive, p.Note, hasMovements, p.CreatedAt, p.UpdatedAt);

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
        var moved = (await db.StockMovements.AsNoTracking().Select(m => m.ProductId).Distinct().ToListAsync(ct)).ToHashSet();
        return [.. products.Select(p => ToDto(p, suppliers, moved.Contains(p.Id)))];
    }

    public async Task<IReadOnlyDictionary<Guid, ProductDto>> GetProductsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var list = ids.ToList();
        var products = await db.Products.AsNoTracking().Where(p => list.Contains(p.Id)).ToListAsync(ct);
        var suppliers = await SupplierNamesAsync(ct);
        return products.ToDictionary(p => p.Id, p => ToDto(p, suppliers, false));
    }

    public async Task<ProductDetailDto?> GetProductAsync(Guid id, CancellationToken ct)
    {
        var p = await db.Products.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return null;
        var suppliers = await SupplierNamesAsync(ct);
        var moved = await db.StockMovements.AsNoTracking().AnyAsync(m => m.ProductId == id, ct);
        var settings = await db.ProductLocationSettings.AsNoTracking().Where(s => s.ProductId == id).OrderBy(s => s.LocationId)
            .Select(s => new LocationSettingDto(s.LocationId, s.MinStock, s.IsEnabled, s.UpdatedAt)).ToListAsync(ct);
        var d = ToDto(p, suppliers, moved);
        return new ProductDetailDto(d.Id, d.Name, d.Category, d.Unit, d.Barcode, d.Purpose, d.Supplier, d.PurchasePrice, d.MinStock, d.TrackExpiry,
            d.IsActive, d.Note, d.HasMovements, d.CreatedAt, d.UpdatedAt, settings);
    }

    public async Task<ProductDto?> GetProductByBarcodeAsync(string code, CancellationToken ct)
    {
        var p = await db.Products.AsNoTracking().FirstOrDefaultAsync(x => x.Barcode == code, ct);
        return p is null ? null : (await ListProductsDtoAsync(p, ct));
    }

    private async Task<ProductDto> ListProductsDtoAsync(Product p, CancellationToken ct) =>
        ToDto(p, await SupplierNamesAsync(ct), await db.StockMovements.AsNoTracking().AnyAsync(m => m.ProductId == p.Id, ct));

    public async Task<IReadOnlyList<string>> ListCategoriesAsync(CancellationToken ct) =>
        [.. (await db.Products.AsNoTracking().Where(p => p.Category != null && p.Category != "").Select(p => p.Category!).Distinct().ToListAsync(ct))
            .OrderBy(c => c, StringComparer.Ordinal)];

    private static void Apply(Product p, ProductData d)
    {
        p.Name = d.Name; p.Category = d.Category; p.Unit = EnumText<ProductUnit>.FromDb(d.Unit); p.Barcode = d.Barcode;
        p.Purpose = EnumText<ProductPurpose>.FromDb(d.Purpose); p.SupplierId = d.SupplierId; p.PurchasePrice = d.PurchasePrice;
        p.MinStock = d.MinStock; p.TrackExpiry = d.TrackExpiry; p.IsActive = d.IsActive; p.Note = d.Note;
    }

    public async Task<ProductWriteResult> CreateProductAsync(ProductData data, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        var p = new Product { Id = Guid.NewGuid(), UpdatedByUserId = userId, CreatedAt = now, UpdatedAt = now };
        Apply(p, data);
        db.Products.Add(p);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (IsUnique(ex, BarcodeIndex)) { db.ChangeTracker.Clear(); return new(CatalogWriteOutcome.BarcodeTaken); }
        return new(CatalogWriteOutcome.Ok, await GetProductAsync(p.Id, ct));
    }

    public async Task<ProductWriteResult> UpdateProductAsync(Guid id, ProductData data, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        var p = await db.Products.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return new(CatalogWriteOutcome.NotFound);
        Apply(p, data);
        p.UpdatedByUserId = userId;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (IsUnique(ex, BarcodeIndex)) { db.ChangeTracker.Clear(); return new(CatalogWriteOutcome.BarcodeTaken); }
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

    public async Task<IReadOnlyList<StockRaw>> ListStockAsync(Guid locationId, DateOnly today, int expiringWithinDays, CancellationToken ct)
    {
        var products = await db.Products.AsNoTracking().ToListAsync(ct);
        var levels = await db.StockLevels.AsNoTracking().Where(l => l.LocationId == locationId).ToDictionaryAsync(l => l.ProductId, l => l.Quantity, ct);
        var settings = await db.ProductLocationSettings.AsNoTracking().Where(s => s.LocationId == locationId).ToDictionaryAsync(s => s.ProductId, ct);
        var batches = (await db.StockBatches.AsNoTracking().Where(b => b.LocationId == locationId && b.Quantity > 0).ToListAsync(ct)).ToLookup(b => b.ProductId);
        var limit = today.AddDays(expiringWithinDays);

        var rows = new List<StockRaw>();
        foreach (var p in products)
        {
            var qty = levels.GetValueOrDefault(p.Id);
            settings.TryGetValue(p.Id, out var st);
            var enabled = st?.IsEnabled ?? true;
            if (!(qty > 0 || (p.IsActive && enabled))) continue;
            var bs = batches[p.Id].ToList();
            rows.Add(new StockRaw(ToRef(p), qty, p.MinStock, st?.MinStock, enabled,
                bs.Where(b => b.ExpiresOn != null).Select(b => b.ExpiresOn).Min(),
                bs.Where(b => b.ExpiresOn < today).Sum(b => b.Quantity),
                bs.Where(b => b.ExpiresOn >= today && b.ExpiresOn <= limit).Sum(b => b.Quantity),
                bs.Where(b => b.UnitCost != null).Sum(b => b.Quantity * b.UnitCost!.Value), bs.Any(b => b.UnitCost == null)));
        }
        return rows;
    }

    public async Task<IReadOnlyList<SummaryRaw>> ListSummaryAsync(IReadOnlyCollection<Guid> locationIds, CancellationToken ct)
    {
        var locs = locationIds.ToList();
        var products = await db.Products.AsNoTracking().ToListAsync(ct);
        var levels = (await db.StockLevels.AsNoTracking().Where(l => locs.Contains(l.LocationId)).ToListAsync(ct)).ToDictionary(l => (l.LocationId, l.ProductId), l => l.Quantity);
        var settings = (await db.ProductLocationSettings.AsNoTracking().Where(s => locs.Contains(s.LocationId)).ToListAsync(ct)).ToDictionary(s => (s.LocationId, s.ProductId));
        var batches = (await db.StockBatches.AsNoTracking().Where(b => locs.Contains(b.LocationId) && b.Quantity > 0).ToListAsync(ct)).ToLookup(b => (b.LocationId, b.ProductId));

        var rows = new List<SummaryRaw>();
        foreach (var p in products)
            foreach (var l in locs)
            {
                var qty = levels.GetValueOrDefault((l, p.Id));
                settings.TryGetValue((l, p.Id), out var st);
                var enabled = st?.IsEnabled ?? true;
                if (!(qty > 0 || (p.IsActive && enabled))) continue;
                var bs = batches[(l, p.Id)].ToList();
                rows.Add(new SummaryRaw(ToRef(p), l, qty, p.MinStock, st?.MinStock, enabled,
                    bs.Where(b => b.UnitCost != null).Sum(b => b.Quantity * b.UnitCost!.Value), bs.Any(b => b.UnitCost == null)));
            }
        return rows;
    }

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
                var id = Guid.Parse(after[3]);
                if (after[0] == "1")
                    q = q.Where(b => b.ExpiresOn == null && (b.ReceivedAt > receivedAt || (b.ReceivedAt == receivedAt && b.Id.CompareTo(id) > 0)));
                else
                {
                    var exp = DateOnly.ParseExact(after[1], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                    q = q.Where(b => b.ExpiresOn == null || b.ExpiresOn > exp
                        || (b.ExpiresOn == exp && (b.ReceivedAt > receivedAt || (b.ReceivedAt == receivedAt && b.Id.CompareTo(id) > 0))));
                }
            }
            var rows = await q.OrderBy(b => b.ExpiresOn == null).ThenBy(b => b.ExpiresOn).ThenBy(b => b.ReceivedAt).ThenBy(b => b.Id).Take(take).ToListAsync(ct);
            merged.AddRange(rows.Select(r => (r, today)));
        }
        var page = merged.OrderBy(x => x.B.ExpiresOn == null).ThenBy(x => x.B.ExpiresOn).ThenBy(x => x.B.ReceivedAt)
            .ThenBy(x => x.B.Id.ToString("D"), StringComparer.Ordinal).Take(take).ToList();

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
                op.Reason is { } r ? EnumText<WriteOffReason>.ToDb(r) : null, op.Note, counterpart, op.StocktakeId, Named(m.CreatedByUserId, users));
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
        return priced.Count == 0 ? null : priced.Sum(m => Math.Abs(m.Quantity) * m.UnitCost!.Value);
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
            moves[o.Id].Select(m => m.ProductId).Distinct().Count(), TotalCost(moves[o.Id], o.Kind)))];
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
            await MapMovementsAsync(moves, ct));
    }

    public async Task<KeyedOperation?> FindOperationByKeyAsync(string keyHash, CancellationToken ct)
    {
        var o = await db.StockOperations.AsNoTracking().Where(x => x.IdempotencyKeyHash == keyHash).Select(x => new { x.Id, x.IdempotencyRequestHash }).FirstOrDefaultAsync(ct);
        if (o is null) return null;
        return new KeyedOperation((await GetOperationAsync(o.Id, ct))!, o.IdempotencyRequestHash ?? "");
    }
}
