using BeautyCrm.Application.Features.BeautyInventory;

namespace BeautyCrm.Tests.Beauty;

/// <summary>
/// In-memory <see cref="IInventoryStore"/> для сервісних тестів складу (TASK-877): та сама семантика операцій, що й у EF-реалізації
/// (заголовок з ідемпотентним ключем, FEFO через <see cref="FefoPlanner"/>, атомарність: нестача не змінює стан). Інвентаризації й
/// агрегатні читання перевіряються на реальній БД (InventoryApiTests).
/// </summary>
internal sealed class FakeInventoryStore : IInventoryStore
{
    public sealed class Batch
    {
        public Guid Id = Guid.NewGuid(), Loc, Prod, OpId;
        public Guid? SourceId, SupplierId;
        public DateOnly? Exp;
        public DateTimeOffset ReceivedAt;
        public decimal Qty, Initial;
        public decimal? Cost;
        public string? Lot;
    }

    public sealed record Move(Guid Id, Guid OpId, Guid Loc, Guid Prod, Guid? BatchId, string Type, decimal Qty, decimal? Cost, DateTimeOffset At);

    public sealed class Op
    {
        public OperationHeader H = null!;
        public List<Move> Moves = [];
    }

    public readonly List<LocationRow> Locations = [];
    public readonly Dictionary<Guid, List<Guid>> SpecLocations = [];
    public readonly Dictionary<Guid, ProductDto> Products = [];
    public readonly Dictionary<Guid, SupplierDto> Suppliers = [];
    /// <summary>Дерево категорій (§32.27); товари посилаються через ProductDto.CategoryId.</summary>
    public readonly Dictionary<Guid, CategoryRow> Categories = [];
    public readonly HashSet<(Guid Loc, Guid Prod)> Disabled = [];
    public readonly List<Batch> Batches = [];
    public readonly Dictionary<(Guid, Guid), decimal> Levels = [];
    public readonly List<Op> Ops = [];
    public int ListLocationCalls;

    // ---------- наповнення ----------

    public LocationRow AddLocation(string name = "Main", string tz = "Europe/Kyiv", bool active = true)
    {
        var l = new LocationRow(Guid.NewGuid(), name, active, tz);
        Locations.Add(l);
        return l;
    }

    public ProductDto AddProduct(string name = "Shampoo", string unit = "pcs", bool trackExpiry = false, bool active = true, decimal? price = null,
        string? barcode = null, Guid? supplierId = null)
    {
        var s = supplierId is { } sid ? new InventoryNamedRef(sid, Suppliers[sid].Name) : null;
        var p = new ProductDto(Guid.NewGuid(), name, null, unit, barcode, "both", s, price, null, null, trackExpiry, active, null, false, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        Products[p.Id] = p;
        return p;
    }

    public SupplierDto AddSupplier(string name = "Acme", bool active = true)
    {
        var s = new SupplierDto(Guid.NewGuid(), name, null, null, null, null, active, 0);
        Suppliers[s.Id] = s;
        return s;
    }

    public Batch AddBatch(Guid loc, Guid prod, decimal qty, DateOnly? exp = null, DateTimeOffset? receivedAt = null, decimal? cost = null)
    {
        var b = new Batch { Loc = loc, Prod = prod, Qty = qty, Initial = qty, Exp = exp, ReceivedAt = receivedAt ?? DateTimeOffset.UnixEpoch, Cost = cost, OpId = Guid.NewGuid() };
        Batches.Add(b);
        Levels[(loc, prod)] = Levels.GetValueOrDefault((loc, prod)) + qty;
        return b;
    }

    public decimal Level(Guid loc, Guid prod) => Levels.GetValueOrDefault((loc, prod));

    /// <summary>Інваріанти §32.4: level = Σ batches = Σ movements для кожної пари; усе &gt;= 0.</summary>
    public void AssertInvariants()
    {
        foreach (var ((loc, prod), level) in Levels)
        {
            Assert.True(level >= 0);
            Assert.Equal(level, Batches.Where(b => b.Loc == loc && b.Prod == prod).Sum(b => b.Qty));
            var moved = Ops.SelectMany(o => o.Moves).Where(m => m.Loc == loc && m.Prod == prod).Sum(m => m.Qty);
            var seeded = Batches.Where(b => b.Loc == loc && b.Prod == prod && !Ops.Any(o => o.H.Id == b.OpId)).Sum(b => b.Initial);
            Assert.Equal(level, moved + seeded);
        }
        Assert.All(Batches, b => Assert.InRange(b.Qty, 0m, b.Initial));
    }

    // ---------- довідники ----------

    public Task<IReadOnlyList<LocationRow>> ListInventoryLocationsAsync(CancellationToken ct) { ListLocationCalls++; return Task.FromResult<IReadOnlyList<LocationRow>>(Locations); }

    public Task<IReadOnlyList<Guid>> SpecialistLocationIdsAsync(Guid specialistId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Guid>>(SpecLocations.GetValueOrDefault(specialistId) ?? []);

    // ---------- каталог ----------

    public Task<IReadOnlyList<ProductDto>> ListProductsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ProductDto>>([.. Products.Values]);

    /// <summary>Скільки id перевірено на «має рух» (стеження за тим, що сервіс питає лише про сторінку).</summary>
    public readonly List<int> MovementProbeSizes = [];

    public Task<IReadOnlySet<Guid>> ProductIdsWithMovementsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        MovementProbeSizes.Add(ids.Count);
        return Task.FromResult<IReadOnlySet<Guid>>(ids.Where(id => Ops.Any(o => o.Moves.Any(m => m.Prod == id))).ToHashSet());
    }

    /// <summary>Хук гонки: викликається в UpdateProductAsync перед перевіркою «має рух» (імітація першого надходження між перевіркою сервісу й записом).</summary>
    public Action? BeforeProductUpdate;

    public Task<ProductDetailDto?> GetProductAsync(Guid id, CancellationToken ct) => Task.FromResult(Products.TryGetValue(id, out var p) ? Detail(p) : null);

    private ProductDetailDto Detail(ProductDto p) => new(p.Id, p.Name, p.Category, p.Unit, p.Barcode, p.Purpose, p.Supplier, p.PurchasePrice, p.SalePrice, p.MinStock,
        p.TrackExpiry, p.IsActive, p.Note, Ops.Any(o => o.Moves.Any(m => m.Prod == p.Id)), p.CreatedAt, p.UpdatedAt, [], p.CategoryId, PathOf(p.CategoryId));

    public string? PathOf(Guid? id)
    {
        var names = new List<string>();
        while (id is { } cur && Categories.TryGetValue(cur, out var c)) { names.Add(c.Name); id = c.ParentId; }
        names.Reverse();
        return names.Count == 0 ? null : string.Join(" › ", names);
    }

    public Task<ProductDto?> GetProductByBarcodeAsync(string code, CancellationToken ct) => Task.FromResult(Products.Values.FirstOrDefault(p => p.Barcode == code));

    public Task<IReadOnlyDictionary<Guid, ProductDto>> GetProductsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<Guid, ProductDto>>(Products.Where(kv => ids.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value));

    public Task<IReadOnlyList<string>> ListCategoriesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>([.. Products.Values.Select(p => p.Category).Where(c => !string.IsNullOrEmpty(c)).Distinct().Order(StringComparer.Ordinal)!]);

    // ---------- дерево категорій ----------

    public Task<IReadOnlyList<CategoryDto>> ListCategoryNodesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<CategoryDto>>([.. Categories.Values.Select(Node)]);

    private CategoryDto Node(CategoryRow c) => new(c.Id, c.ParentId, c.Name, c.SortOrder, c.IsArchived,
        Products.Values.Count(p => p.CategoryId == c.Id), Categories.Values.Count(x => x.ParentId == c.Id));

    public Task<CategoryRow?> GetCategoryRowAsync(Guid id, CancellationToken ct) => Task.FromResult(Categories.GetValueOrDefault(id));

    public CategoryRow AddCategory(string name, Guid? parent = null, bool archived = false, int sort = 0)
    {
        var c = new CategoryRow(Guid.NewGuid(), parent, name, sort, archived);
        Categories[c.Id] = c;
        return c;
    }

    public Task<CategoryWriteResult> WriteCategoryAsync(Func<IReadOnlyList<CategoryRow>, CategoryPlan> decide, DateTimeOffset now, CancellationToken ct)
    {
        switch (decide([.. Categories.Values]))
        {
            case CategoryReject r: return Task.FromResult(new CategoryWriteResult(CategoryWriteOutcome.Rejected, Error: r.Error));
            case CategoryInsert i:
                var c = AddCategory(i.Name, i.ParentId, false, i.SortOrder);
                return Task.FromResult(new CategoryWriteResult(CategoryWriteOutcome.Ok, Node(c)));
            case CategoryUpdate u:
                var upd = new CategoryRow(u.Id, u.ParentId, u.Name, u.SortOrder, u.IsArchived);
                Categories[u.Id] = upd;
                return Task.FromResult(new CategoryWriteResult(CategoryWriteOutcome.Ok, Node(upd)));
            default: throw new InvalidOperationException();
        }
    }

    public Task<CategoryWriteOutcome> DeleteCategoryAsync(Guid id, CancellationToken ct)
    {
        if (!Categories.ContainsKey(id)) return Task.FromResult(CategoryWriteOutcome.NotFound);
        if (Categories.Values.Any(c => c.ParentId == id) || Products.Values.Any(p => p.CategoryId == id)) return Task.FromResult(CategoryWriteOutcome.NotEmpty);
        Categories.Remove(id);
        return Task.FromResult(CategoryWriteOutcome.Ok);
    }

    public Task<ProductWriteResult> CreateProductAsync(ProductData d, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        if (d.Barcode is not null && Products.Values.Any(p => p.Barcode == d.Barcode)) return Task.FromResult(new ProductWriteResult(CatalogWriteOutcome.BarcodeTaken));
        var p = new ProductDto(Guid.NewGuid(), d.Name, d.Category, d.Unit, d.Barcode, d.Purpose,
            d.SupplierId is { } s ? new InventoryNamedRef(s, Suppliers[s].Name) : null, d.PurchasePrice, d.SalePrice, d.MinStock, d.TrackExpiry, d.IsActive, d.Note, false, now, now,
            d.CategoryId);
        Products[p.Id] = p;
        return Task.FromResult(new ProductWriteResult(CatalogWriteOutcome.Ok, Detail(p)));
    }

    public Task<ProductWriteResult> UpdateProductAsync(Guid id, ProductData d, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        if (!Products.TryGetValue(id, out var old)) return Task.FromResult(new ProductWriteResult(CatalogWriteOutcome.NotFound));
        BeforeProductUpdate?.Invoke();
        if (old.Unit != d.Unit && Ops.Any(o => o.Moves.Any(m => m.Prod == id))) return Task.FromResult(new ProductWriteResult(CatalogWriteOutcome.UnitLocked));
        if (d.Barcode is not null && Products.Values.Any(p => p.Id != id && p.Barcode == d.Barcode)) return Task.FromResult(new ProductWriteResult(CatalogWriteOutcome.BarcodeTaken));
        var p = old with
        {
            Name = d.Name, Category = d.Category, Unit = d.Unit, Barcode = d.Barcode, Purpose = d.Purpose, PurchasePrice = d.PurchasePrice, SalePrice = d.SalePrice, MinStock = d.MinStock,
            TrackExpiry = d.TrackExpiry, IsActive = d.IsActive, Note = d.Note, UpdatedAt = now, CategoryId = d.CategoryId,
            Supplier = d.SupplierId is { } s ? new InventoryNamedRef(s, Suppliers[s].Name) : null,
        };
        Products[id] = p;
        return Task.FromResult(new ProductWriteResult(CatalogWriteOutcome.Ok, Detail(p)));
    }

    public Task<LocationSettingDto?> UpsertSettingAsync(Guid productId, Guid locationId, decimal? minStock, bool isEnabled, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        if (isEnabled) Disabled.Remove((locationId, productId)); else Disabled.Add((locationId, productId));
        return Task.FromResult<LocationSettingDto?>(new LocationSettingDto(locationId, minStock, isEnabled, now));
    }

    public Task<bool> DeleteSettingAsync(Guid productId, Guid locationId, CancellationToken ct) => Task.FromResult(Disabled.Remove((locationId, productId)));

    public Task<IReadOnlyDictionary<Guid, bool>> GetEnabledMapAsync(Guid locationId, IReadOnlyCollection<Guid> productIds, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<Guid, bool>>(productIds.ToDictionary(p => p, p => !Disabled.Contains((locationId, p))));

    public Task<IReadOnlyList<SupplierDto>> ListSuppliersAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<SupplierDto>>([.. Suppliers.Values]);
    public Task<SupplierDto?> GetSupplierAsync(Guid id, CancellationToken ct) => Task.FromResult(Suppliers.GetValueOrDefault(id));

    public Task<SupplierWriteResult> CreateSupplierAsync(SupplierData d, DateTimeOffset now, CancellationToken ct)
    {
        if (Suppliers.Values.Any(s => string.Equals(s.Name, d.Name, StringComparison.OrdinalIgnoreCase))) return Task.FromResult(new SupplierWriteResult(CatalogWriteOutcome.NameTaken));
        var s = new SupplierDto(Guid.NewGuid(), d.Name, d.ContactName, d.Phone, d.Email, d.Note, d.IsActive, 0);
        Suppliers[s.Id] = s;
        return Task.FromResult(new SupplierWriteResult(CatalogWriteOutcome.Ok, s));
    }

    public Task<SupplierWriteResult> UpdateSupplierAsync(Guid id, SupplierData d, DateTimeOffset now, CancellationToken ct)
    {
        if (!Suppliers.ContainsKey(id)) return Task.FromResult(new SupplierWriteResult(CatalogWriteOutcome.NotFound));
        var s = new SupplierDto(id, d.Name, d.ContactName, d.Phone, d.Email, d.Note, d.IsActive, 0);
        Suppliers[id] = s;
        return Task.FromResult(new SupplierWriteResult(CatalogWriteOutcome.Ok, s));
    }

    // ---------- читання (мінімум для сервісних тестів) ----------

    public Task<IReadOnlyList<StockRaw>> ListStockAsync(StockFilter f, string[]? after, int take, CancellationToken ct)
    {
        var locationId = f.LocationId; var today = f.Today; var expiringWithinDays = f.ExpiringWithinDays;
        var rows = Products.Values.Select(p =>
        {
            var bs = Batches.Where(b => b.Loc == locationId && b.Prod == p.Id && b.Qty > 0).ToList();
            var r = new ProductRef(p.Id, p.Name, p.Unit, p.Purpose, p.Category, p.Barcode, p.IsActive);
            return new StockRaw(r, Level(locationId, p.Id), p.MinStock, null, !Disabled.Contains((locationId, p.Id)),
                bs.Where(b => b.Exp != null).Select(b => b.Exp).Min(), bs.Where(b => b.Exp < today).Sum(b => b.Qty),
                bs.Where(b => b.Exp >= today && b.Exp <= today.AddDays(expiringWithinDays)).Sum(b => b.Qty),
                bs.Where(b => b.Cost != null).Sum(b => b.Qty * b.Cost!.Value), bs.Any(b => b.Cost == null));
        });
        var list = rows.Where(r => (r.Quantity > 0 || (r.Product.IsActive && r.IsEnabled))
                && (f.Category is null || r.Product.Category == f.Category) && (f.Purpose is null || r.Product.Purpose == f.Purpose)
                && (f.Search is null || r.Product.Name.Contains(f.Search, StringComparison.OrdinalIgnoreCase))
                && (f.State != "in_stock" || r.Quantity > 0) && (f.State != "out_of_stock" || r.Quantity <= 0)
                && (f.State != "low" || (r.ProductMinStock > 0 && r.Quantity < r.ProductMinStock)) && (!f.OnlyExpiring || r.ExpiringQuantity > 0))
            .OrderBy(r => r.Product.Name.ToLowerInvariant(), StringComparer.Ordinal).ThenBy(r => r.Product.Id);
        var paged = after is null ? list : list.Where(r =>
        {
            var c = string.CompareOrdinal(r.Product.Name.ToLowerInvariant(), after[0]);
            return c > 0 || (c == 0 && r.Product.Id.CompareTo(Guid.Parse(after[1])) > 0);
        }).OrderBy(r => r.Product.Name.ToLowerInvariant(), StringComparer.Ordinal).ThenBy(r => r.Product.Id);
        return Task.FromResult<IReadOnlyList<StockRaw>>([.. paged.Take(take)]);
    }

    public Task<IReadOnlyList<SummaryRaw>> ListSummaryAsync(SummaryFilter filter, string[]? after, int take, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<BatchDto>> ListBatchesAsync(BatchFilter filter, string[]? after, int take, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyDictionary<Guid, BatchRef>> GetBatchRefsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<Guid, BatchRef>>(Batches.Where(b => ids.Contains(b.Id)).ToDictionary(b => b.Id, b => new BatchRef(b.Id, b.Loc, b.Prod, b.Exp)));

    public Task<IReadOnlyList<MovementDto>> ListMovementsAsync(MovementFilter filter, string[]? after, int take, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<OperationSummaryDto>> ListOperationsAsync(OperationFilter filter, string[]? after, int take, CancellationToken ct) => throw new NotSupportedException();

    private ProductRef Ref(Guid id) { var p = Products[id]; return new ProductRef(p.Id, p.Name, p.Unit, p.Purpose, p.Category, p.Barcode, p.IsActive); }

    private OperationDto Dto(Op o)
    {
        var h = o.H;
        var priced = o.Moves.Where(m => m.Cost != null && (h.Kind != "transfer" || m.Type == "transfer_out")).ToList();
        return new OperationDto(h.Id, h.Kind, h.LocationId, h.ToLocationId, h.Reason, h.DocumentNumber, h.Now, new InventoryNamedRef(h.UserId, "user"),
            o.Moves.Select(m => m.Prod).Distinct().Count(), priced.Count == 0 ? null : priced.Sum(m => Math.Abs(m.Qty) * m.Cost!.Value),
            h.SupplierId is { } s ? new InventoryNamedRef(s, Suppliers[s].Name) : null, h.ReceivedOn, h.Note, null,
            [.. o.Moves.Select(m => new MovementDto(m.Id, h.Id, h.Kind, m.Type, m.At, m.Loc, Ref(m.Prod), m.BatchId, m.Qty, m.Cost, h.Reason, h.Note,
                m.Type == "transfer_out" ? h.ToLocationId : m.Type == "transfer_in" ? h.LocationId : null, null, new InventoryNamedRef(h.UserId, "user")))]);
    }

    public Task<OperationDto?> GetOperationAsync(Guid id, CancellationToken ct) => Task.FromResult(Ops.Where(o => o.H.Id == id).Select(Dto).FirstOrDefault());

    public Task<KeyedOperation?> FindOperationByKeyAsync(string keyHash, CancellationToken ct) =>
        Task.FromResult(Ops.Where(o => o.H.KeyHash == keyHash).Select(o => new KeyedOperation(Dto(o), o.H.RequestHash)).FirstOrDefault());

    // ---------- операції ----------

    /// <summary>Хук для імітації паралельного повтору: викликається перед перевіркою ключа.</summary>
    public Action? BeforeKeyCheck;

    private Op? Begin(OperationHeader h)
    {
        BeforeKeyCheck?.Invoke();
        return Ops.Any(o => o.H.KeyHash == h.KeyHash) ? null : new Op { H = h };
    }

    public Task<StockWriteResult> PostReceiptAsync(OperationHeader h, IReadOnlyList<ReceiptLine> lines, CancellationToken ct)
    {
        if (Begin(h) is not { } op) return Task.FromResult(new StockWriteResult(StockWriteStatus.DuplicateKey));
        foreach (var l in lines)
        {
            var b = new Batch
            {
                Loc = h.LocationId, Prod = l.ProductId, OpId = h.Id, Qty = l.Quantity, Initial = l.Quantity, Exp = l.ExpiresOn, Lot = l.LotNumber, Cost = l.UnitCost,
                ReceivedAt = h.Now, SupplierId = h.SupplierId ?? Products[l.ProductId].Supplier?.Id,
            };
            Batches.Add(b);
            Levels[(h.LocationId, l.ProductId)] = Level(h.LocationId, l.ProductId) + l.Quantity;
            op.Moves.Add(new(Guid.NewGuid(), h.Id, h.LocationId, l.ProductId, b.Id, "receipt", l.Quantity, l.UnitCost, h.Now));
        }
        Ops.Add(op);
        return Task.FromResult(new StockWriteResult(StockWriteStatus.Ok, Dto(op)));
    }

    private static BatchRow Row(Batch b) => new(b.Id, b.Prod, b.Exp, b.ReceivedAt, b.Qty, b.Cost, b.Lot, b.SupplierId);

    private Task<StockWriteResult> Take(OperationHeader h, IReadOnlyList<StockLine> lines, DateOnly today, bool transfer)
    {
        if (Begin(h) is not { } op) return Task.FromResult(new StockWriteResult(StockWriteStatus.DuplicateKey));
        var explicitIds = lines.Where(l => l.BatchId != null).Select(l => l.BatchId!.Value).ToHashSet();
        var src = Batches.Where(b => b.Loc == h.LocationId && lines.Any(l => l.ProductId == b.Prod) && (b.Qty > 0 || explicitIds.Contains(b.Id))).ToList();
        var plan = FefoPlanner.Allocate(lines, [.. src.Select(Row)], skipExpired: transfer, today);
        if (plan.Shortages.Count > 0) return Task.FromResult(new StockWriteResult(StockWriteStatus.Insufficient, null, plan.Shortages));
        foreach (var t in plan.Takes)
        {
            var b = src.First(x => x.Id == t.BatchId);
            b.Qty -= t.Quantity;
            Levels[(h.LocationId, t.ProductId)] = Level(h.LocationId, t.ProductId) - t.Quantity;
            op.Moves.Add(new(Guid.NewGuid(), h.Id, h.LocationId, t.ProductId, b.Id, transfer ? "transfer_out" : "write_off", -t.Quantity, b.Cost, h.Now));
            if (!transfer) continue;
            var dst = new Batch
            {
                Loc = h.ToLocationId!.Value, Prod = t.ProductId, OpId = h.Id, SourceId = b.Id, SupplierId = b.SupplierId, ReceivedAt = b.ReceivedAt, Exp = b.Exp,
                Lot = b.Lot, Initial = t.Quantity, Qty = t.Quantity, Cost = b.Cost,
            };
            Batches.Add(dst);
            Levels[(dst.Loc, t.ProductId)] = Level(dst.Loc, t.ProductId) + t.Quantity;
            op.Moves.Add(new(Guid.NewGuid(), h.Id, dst.Loc, t.ProductId, dst.Id, "transfer_in", t.Quantity, b.Cost, h.Now));
        }
        Ops.Add(op);
        return Task.FromResult(new StockWriteResult(StockWriteStatus.Ok, Dto(op)));
    }

    public Task<StockWriteResult> PostWriteOffAsync(OperationHeader h, IReadOnlyList<StockLine> lines, DateOnly today, CancellationToken ct) => Take(h, lines, today, false);
    public Task<StockWriteResult> PostTransferAsync(OperationHeader h, IReadOnlyList<StockLine> lines, DateOnly today, CancellationToken ct) => Take(h, lines, today, true);

    // ---------- інвентаризації: лише на реальній БД ----------

    public Task<IReadOnlyList<StocktakeSummaryDto>> ListStocktakesAsync(StocktakeFilter filter, string[]? after, int take, CancellationToken ct) => throw new NotSupportedException();
    public Task<StocktakeDto?> GetStocktakeAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
    public Task<StocktakeWriteResult> CreateStocktakeAsync(Guid locationId, string? note, IReadOnlyList<Guid>? productIds, Guid userId, DateTimeOffset now, CancellationToken ct) => throw new NotSupportedException();
    public Task<StocktakeWriteResult> PutStocktakeLinesAsync(Guid id, int expectedVersion, IReadOnlyList<StocktakeLineChange> lines, Guid userId, DateTimeOffset now, CancellationToken ct) => throw new NotSupportedException();
    public Task<StocktakeWriteResult> PostStocktakeAsync(Guid id, int expectedVersion, Guid userId, DateTimeOffset now, DateOnly today, CancellationToken ct) => throw new NotSupportedException();
    public Task<StocktakeWriteResult> CancelStocktakeAsync(Guid id, int expectedVersion, Guid userId, DateTimeOffset now, CancellationToken ct) => throw new NotSupportedException();
}
