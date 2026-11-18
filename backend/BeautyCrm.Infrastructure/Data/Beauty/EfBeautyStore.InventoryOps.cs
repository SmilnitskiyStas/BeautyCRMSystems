using BeautyCrm.Application.Features.BeautyInventory;
using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace BeautyCrm.Infrastructure.Data.Beauty;

// IInventoryStore: операції (надходження, списання, переміщення) й інвентаризації (§32.4). Кожна операція - ОДНА транзакція (READ COMMITTED):
// заголовок -> блокування рівнів у фіксованому порядку (location_id, product_id) -> FEFO й перевірка достатності ПІД блокуванням ->
// партії, рухи, рівні. Нестача/помилка відкочує все (жодного руху, рівні без змін).
public sealed partial class EfBeautyStore
{
    /// <summary>
    /// INSERT ... ON CONFLICT DO NOTHING + SELECT ... FOR UPDATE для кожної пари у порядку (location, product). Єдине місце порядку
    /// блокувань: зустрічні переміщення A-&gt;B і B-&gt;A не дають дедлоку. Повертає відстежувані рівні.
    /// </summary>
    private async Task<Dictionary<(Guid Loc, Guid Prod), StockLevel>> LockLevelsAsync(IEnumerable<(Guid Loc, Guid Prod)> pairs, CancellationToken ct)
    {
        var result = new Dictionary<(Guid, Guid), StockLevel>();
        foreach (var (l, p) in pairs.Distinct().OrderBy(x => x.Loc).ThenBy(x => x.Prod))
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO beauty_stock_levels (tenant_id, location_id, product_id, quantity) VALUES (NULLIF(current_setting('app.tenant_id', true), '')::uuid, {l}, {p}, 0) ON CONFLICT (tenant_id, location_id, product_id) DO NOTHING", ct);
            var level = (await db.StockLevels.FromSqlInterpolated($"SELECT * FROM beauty_stock_levels WHERE location_id = {l} AND product_id = {p} FOR UPDATE").ToListAsync(ct)).Single();
            result[(l, p)] = level;
        }
        return result;
    }

    /// <summary>
    /// Рядки товарів FOR SHARE (у порядку id) - ПІСЛЯ блокування рівнів. Конфліктує з оновленням товару (FOR UPDATE у UpdateProductAsync), тож
    /// зміна одиниці/активності не може проскочити між перевіркою сервісу й записом руху (TASK-880 L3). Повертає знімок під блокуванням.
    /// </summary>
    private async Task<Dictionary<Guid, Product>> LockProductsAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var arr = ids.Distinct().OrderBy(x => x).ToArray();
        return (await db.Products.FromSqlInterpolated($"SELECT * FROM beauty_products WHERE id = ANY({arr}) ORDER BY id FOR SHARE").AsNoTracking().ToListAsync(ct))
            .ToDictionary(p => p.Id);
    }

    private async Task<Location?> LockLocationAsync(Guid id, CancellationToken ct) =>
        (await db.Locations.FromSqlInterpolated($"SELECT * FROM beauty_locations WHERE id = {id} FOR SHARE").AsNoTracking().ToListAsync(ct)).FirstOrDefault();

    /// <summary>is_enabled налаштувань товарів у закладі (рядки FOR SHARE); немає рядка = увімкнено.</summary>
    private async Task<Dictionary<Guid, bool>> LockEnabledAsync(Guid locationId, IEnumerable<Guid> productIds, CancellationToken ct)
    {
        var arr = productIds.Distinct().ToArray();
        return (await db.ProductLocationSettings.FromSqlInterpolated(
                $"SELECT * FROM beauty_product_location_settings WHERE location_id = {locationId} AND product_id = ANY({arr}) ORDER BY product_id FOR SHARE")
            .AsNoTracking().ToListAsync(ct)).ToDictionary(s => s.ProductId, s => s.IsEnabled);
    }

    private async Task<StockWriteResult> RejectAsync(IDbContextTransaction tx, string code, int? line, CancellationToken ct)
    {
        await tx.RollbackAsync(ct);
        db.ChangeTracker.Clear();
        return new(StockWriteStatus.Rejected, Code: code, Line: line);
    }

    private static bool Fractional(Product p, decimal qty) => p.Unit is ProductUnit.Pcs or ProductUnit.Pack && qty != decimal.Truncate(qty);

    private static BatchRow ToRow(StockBatch b) => new(b.Id, b.ProductId, b.ExpiresOn, b.ReceivedAt, b.Quantity, b.UnitCost, b.LotNumber, b.SupplierId);

    private StockMovement Move(StockOperation op, Guid locationId, Guid productId, Guid? batchId, StockMovementType type, decimal qty, decimal? cost, Guid? user, DateTimeOffset now)
    {
        var m = new StockMovement
        {
            Id = Guid.NewGuid(), OperationId = op.Id, LocationId = locationId, ProductId = productId, BatchId = batchId, Type = type,
            Quantity = qty, UnitCost = cost, CreatedByUserId = user, OccurredAt = now,
        };
        db.StockMovements.Add(m);
        return m;
    }

    private static StockOperation NewOperation(OperationHeader h) => new()
    {
        Id = h.Id, Kind = EnumText<StockOperationKind>.FromDb(h.Kind), LocationId = h.LocationId, ToLocationId = h.ToLocationId, SupplierId = h.SupplierId,
        DocumentNumber = h.DocumentNumber, ReceivedOn = h.ReceivedOn, Reason = h.Reason is null ? null : EnumText<WriteOffReason>.FromDb(h.Reason),
        Note = h.Note, CreatedByUserId = h.UserId, CreatedAt = h.Now, IdempotencyKeyHash = h.KeyHash, IdempotencyRequestHash = h.RequestHash,
    };

    /// <summary>Вставка заголовка (унікальний ключ ідемпотентності). true = конфлікт (паралельний повтор): транзакція буде відкочена.</summary>
    private async Task<bool> InsertHeaderAsync(OperationHeader h, CancellationToken ct)
    {
        db.StockOperations.Add(NewOperation(h));
        try { await db.SaveChangesAsync(ct); return false; }
        catch (DbUpdateException ex) when (IsUnique(ex, IdempotencyIndex)) { db.ChangeTracker.Clear(); return true; }
    }

    // ---------- надходження ----------

    public async Task<StockWriteResult> PostReceiptAsync(OperationHeader h, IReadOnlyList<ReceiptLine> lines, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await InsertHeaderAsync(h, ct)) { await tx.RollbackAsync(ct); return new(StockWriteStatus.DuplicateKey); }
        var op = await db.StockOperations.FirstAsync(o => o.Id == h.Id, ct);
        var levels = await LockLevelsAsync(lines.Select(l => (h.LocationId, l.ProductId)), ct);
        var pids = lines.Select(l => l.ProductId).Distinct().ToList();
        var products = await LockProductsAsync(pids, ct);
        var productSupplier = products.ToDictionary(p => p.Key, p => p.Value.SupplierId);

        // повтор перевірок сервісу ПІД блокуванням (TASK-880 L3) + межа numeric(14,3) для рівня (L2)
        var location = await LockLocationAsync(h.LocationId, ct);
        if (location is null || !location.IsActive) return await RejectAsync(tx, "location_inactive", null, ct);
        var enabled = await LockEnabledAsync(h.LocationId, pids, ct);
        var added = new Dictionary<Guid, decimal>();
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            if (!products.TryGetValue(l.ProductId, out var p) || !p.IsActive) return await RejectAsync(tx, "product_inactive", i, ct);
            if (enabled.TryGetValue(p.Id, out var en) && !en) return await RejectAsync(tx, "product_disabled_for_location", i, ct);
            if (Fractional(p, l.Quantity)) return await RejectAsync(tx, "invalid_quantity", i, ct);
            if (p.TrackExpiry && l.ExpiresOn is null) return await RejectAsync(tx, "expiry_required", i, ct);
            added[p.Id] = added.GetValueOrDefault(p.Id) + l.Quantity;
            if (levels[(h.LocationId, p.Id)].Quantity + added[p.Id] > InventoryService.MaxLevel) return await RejectAsync(tx, "quantity_out_of_range", i, ct);
        }

        foreach (var l in lines)
        {
            var batch = new StockBatch
            {
                Id = Guid.NewGuid(), LocationId = h.LocationId, ProductId = l.ProductId, OperationId = op.Id,
                SupplierId = h.SupplierId ?? productSupplier.GetValueOrDefault(l.ProductId), ReceivedAt = h.Now, ExpiresOn = l.ExpiresOn,
                LotNumber = l.LotNumber, InitialQuantity = l.Quantity, Quantity = l.Quantity, UnitCost = l.UnitCost, CreatedAt = h.Now, UpdatedAt = h.Now,
            };
            db.StockBatches.Add(batch);
            Move(op, h.LocationId, l.ProductId, batch.Id, StockMovementType.Receipt, l.Quantity, l.UnitCost, h.UserId, h.Now);
            levels[(h.LocationId, l.ProductId)].Quantity += l.Quantity;
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return new(StockWriteStatus.Ok, await GetOperationAsync(h.Id, ct));
    }

    // ---------- списання ----------

    public async Task<StockWriteResult> PostWriteOffAsync(OperationHeader h, IReadOnlyList<StockLine> lines, DateOnly today, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await InsertHeaderAsync(h, ct)) { await tx.RollbackAsync(ct); return new(StockWriteStatus.DuplicateKey); }
        var op = await db.StockOperations.FirstAsync(o => o.Id == h.Id, ct);
        var levels = await LockLevelsAsync(lines.Select(l => (h.LocationId, l.ProductId)), ct);
        await LockProductsAsync(lines.Select(l => l.ProductId), ct);
        var batches = await LoadBatchesAsync(h.LocationId, lines, ct);
        var plan = FefoPlanner.Allocate(lines, [.. batches.Values.Select(ToRow)], skipExpired: false, today);
        if (plan.Shortages.Count > 0) { await tx.RollbackAsync(ct); db.ChangeTracker.Clear(); return new(StockWriteStatus.Insufficient, null, plan.Shortages); }

        foreach (var t in plan.Takes)
        {
            batches[t.BatchId].Quantity -= t.Quantity;
            levels[(h.LocationId, t.ProductId)].Quantity -= t.Quantity;
            Move(op, h.LocationId, t.ProductId, t.BatchId, StockMovementType.WriteOff, -t.Quantity, t.UnitCost, h.UserId, h.Now);
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return new(StockWriteStatus.Ok, await GetOperationAsync(h.Id, ct));
    }

    /// <summary>Партії закладу для товарів рядків (quantity &gt; 0 або явно названі), відстежувані; викликати ПІСЛЯ блокування рівнів.</summary>
    private async Task<Dictionary<Guid, StockBatch>> LoadBatchesAsync(Guid locationId, IReadOnlyList<StockLine> lines, CancellationToken ct)
    {
        var pids = lines.Select(l => l.ProductId).Distinct().ToList();
        var explicitIds = lines.Where(l => l.BatchId != null).Select(l => l.BatchId!.Value).ToList();
        return await db.StockBatches.Where(b => b.LocationId == locationId && pids.Contains(b.ProductId) && (b.Quantity > 0 || explicitIds.Contains(b.Id)))
            .ToDictionaryAsync(b => b.Id, ct);
    }

    // ---------- переміщення ----------

    public async Task<StockWriteResult> PostTransferAsync(OperationHeader h, IReadOnlyList<StockLine> lines, DateOnly today, CancellationToken ct)
    {
        var to = h.ToLocationId!.Value;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await InsertHeaderAsync(h, ct)) { await tx.RollbackAsync(ct); return new(StockWriteStatus.DuplicateKey); }
        var op = await db.StockOperations.FirstAsync(o => o.Id == h.Id, ct);
        var levels = await LockLevelsAsync(lines.SelectMany(l => new[] { (h.LocationId, l.ProductId), (to, l.ProductId) }), ct);
        var pids = lines.Select(l => l.ProductId).Distinct().ToList();
        await LockProductsAsync(pids, ct);
        var dest = await LockLocationAsync(to, ct);
        if (dest is null || !dest.IsActive) return await RejectAsync(tx, "location_inactive", null, ct);
        var enabled = await LockEnabledAsync(to, pids, ct);
        foreach (var l in lines)
            if (enabled.TryGetValue(l.ProductId, out var en) && !en) return await RejectAsync(tx, "product_disabled_for_location", l.Line, ct);
        var batches = await LoadBatchesAsync(h.LocationId, lines, ct);
        var plan = FefoPlanner.Allocate(lines, [.. batches.Values.Select(ToRow)], skipExpired: true, today);
        if (plan.Shortages.Count > 0) { await tx.RollbackAsync(ct); db.ChangeTracker.Clear(); return new(StockWriteStatus.Insufficient, null, plan.Shortages); }
        var moved = new Dictionary<Guid, decimal>();
        foreach (var t in plan.Takes)
        {
            moved[t.ProductId] = moved.GetValueOrDefault(t.ProductId) + t.Quantity;
            if (levels[(to, t.ProductId)].Quantity + moved[t.ProductId] > InventoryService.MaxLevel) return await RejectAsync(tx, "quantity_out_of_range", t.Line, ct);
        }

        foreach (var t in plan.Takes)
        {
            var src = batches[t.BatchId];
            src.Quantity -= t.Quantity;
            levels[(h.LocationId, t.ProductId)].Quantity -= t.Quantity;
            Move(op, h.LocationId, t.ProductId, src.Id, StockMovementType.TransferOut, -t.Quantity, src.UnitCost, h.UserId, h.Now);

            var dst = new StockBatch
            {
                Id = Guid.NewGuid(), LocationId = to, ProductId = t.ProductId, OperationId = op.Id, SourceBatchId = src.Id, SupplierId = src.SupplierId,
                ReceivedAt = src.ReceivedAt, ExpiresOn = src.ExpiresOn, LotNumber = src.LotNumber, InitialQuantity = t.Quantity, Quantity = t.Quantity,
                UnitCost = src.UnitCost, CreatedAt = h.Now, UpdatedAt = h.Now,
            };
            db.StockBatches.Add(dst);
            levels[(to, t.ProductId)].Quantity += t.Quantity;
            Move(op, to, t.ProductId, dst.Id, StockMovementType.TransferIn, t.Quantity, src.UnitCost, h.UserId, h.Now);
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return new(StockWriteStatus.Ok, await GetOperationAsync(h.Id, ct));
    }

    // ---------- інвентаризації ----------

    private async Task<StocktakeDto> BuildStocktakeAsync(Stocktake s, CancellationToken ct)
    {
        var lines = await db.StocktakeLines.AsNoTracking().Where(l => l.StocktakeId == s.Id).ToListAsync(ct);
        var pids = lines.Select(l => l.ProductId).ToList();
        var products = await db.Products.AsNoTracking().Where(p => pids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var live = s.Status == StocktakeStatus.Draft
            ? await db.StockLevels.AsNoTracking().Where(l => l.LocationId == s.LocationId && pids.Contains(l.ProductId)).ToDictionaryAsync(l => l.ProductId, l => l.Quantity, ct)
            : [];
        var users = await UserNamesAsync([s.CreatedByUserId, .. s.PostedByUserId is { } p ? new[] { p } : []], ct);
        var dtoLines = lines.Select(l =>
        {
            decimal? expected = s.Status == StocktakeStatus.Draft ? live.GetValueOrDefault(l.ProductId) : l.ExpectedQuantity;
            decimal? diff = l.CountedQuantity is { } c && expected is { } e ? c - e : null;
            return new StocktakeLineDto(ToRef(products[l.ProductId]), expected, l.CountedQuantity, diff, l.SurplusExpiresOn);
        }).OrderBy(l => l.Product.Name.ToLowerInvariant(), StringComparer.Ordinal).ThenBy(l => l.Product.Id).ToList();
        return new StocktakeDto(s.Id, s.LocationId, EnumText<StocktakeStatus>.ToDb(s.Status), s.CreatedAt, Named(s.CreatedByUserId, users)!, s.PostedAt,
            lines.Count, lines.Count(l => l.CountedQuantity != null), s.Note, s.Version, Named(s.PostedByUserId, users), s.CancelledAt, dtoLines);
    }

    public async Task<StocktakeDto?> GetStocktakeAsync(Guid id, CancellationToken ct)
    {
        var s = await db.Stocktakes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return s is null ? null : await BuildStocktakeAsync(s, ct);
    }

    public async Task<IReadOnlyList<StocktakeSummaryDto>> ListStocktakesAsync(StocktakeFilter f, string[]? after, int take, CancellationToken ct)
    {
        var q = db.Stocktakes.AsNoTracking().AsQueryable();
        if (f.LocationIds is { } ids) q = q.Where(s => ids.Contains(s.LocationId));
        if (f.LocationId is { } lid) q = q.Where(s => s.LocationId == lid);
        if (f.Status is { } st) { var status = EnumText<StocktakeStatus>.FromDb(st); q = q.Where(s => s.Status == status); }
        if (after is not null)
        {
            var at = new DateTimeOffset(long.Parse(after[0], System.Globalization.CultureInfo.InvariantCulture), TimeSpan.Zero);
            var id = Guid.Parse(after[1]);
            q = q.Where(s => s.CreatedAt < at || (s.CreatedAt == at && s.Id.CompareTo(id) < 0));
        }
        var rows = await q.OrderByDescending(s => s.CreatedAt).ThenByDescending(s => s.Id).Take(take).ToListAsync(ct);
        var sids = rows.Select(r => r.Id).ToList();
        var counts = (await db.StocktakeLines.AsNoTracking().Where(l => sids.Contains(l.StocktakeId))
            .GroupBy(l => l.StocktakeId).Select(g => new { g.Key, Lines = g.Count(), Counted = g.Count(l => l.CountedQuantity != null) }).ToListAsync(ct))
            .ToDictionary(x => x.Key);
        var users = await UserNamesAsync(rows.Select(r => r.CreatedByUserId), ct);
        return [.. rows.Select(r => new StocktakeSummaryDto(r.Id, r.LocationId, EnumText<StocktakeStatus>.ToDb(r.Status), r.CreatedAt,
            Named(r.CreatedByUserId, users)!, r.PostedAt, counts.TryGetValue(r.Id, out var c) ? c.Lines : 0, c?.Counted ?? 0))];
    }

    public async Task<StocktakeWriteResult> CreateStocktakeAsync(Guid locationId, string? note, IReadOnlyList<Guid>? productIds, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await db.Stocktakes.AsNoTracking().Where(s => s.LocationId == locationId && s.Status == StocktakeStatus.Draft).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct) is { } open)
            return new(StocktakeWriteStatus.InProgress, OtherId: open);

        List<Guid> ids;
        if (productIds is { Count: > 0 })
        {
            ids = [.. productIds.Distinct()];
            var found = await db.Products.AsNoTracking().CountAsync(p => ids.Contains(p.Id), ct);
            if (found != ids.Count) return new(StocktakeWriteStatus.ProductNotFound);
        }
        else
        {
            var disabled = await db.ProductLocationSettings.AsNoTracking().Where(s => s.LocationId == locationId && !s.IsEnabled).Select(s => s.ProductId).ToListAsync(ct);
            var withStock = await db.StockLevels.AsNoTracking().Where(l => l.LocationId == locationId && l.Quantity != 0).Select(l => l.ProductId).ToListAsync(ct);
            ids = await db.Products.AsNoTracking().Where(p => (p.IsActive && !disabled.Contains(p.Id)) || withStock.Contains(p.Id)).Select(p => p.Id).ToListAsync(ct);
        }
        if (ids.Count > InventoryService.MaxStocktakeLines) return new(StocktakeWriteStatus.TooManyLines);

        var st = new Stocktake { Id = Guid.NewGuid(), LocationId = locationId, Note = note, CreatedByUserId = userId, CreatedAt = now };
        db.Stocktakes.Add(st);
        foreach (var pid in ids) db.StocktakeLines.Add(new StocktakeLine { Id = Guid.NewGuid(), StocktakeId = st.Id, ProductId = pid, CreatedAt = now, UpdatedAt = now });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (IsUnique(ex, OpenStocktakeIndex))
        {
            db.ChangeTracker.Clear();
            await tx.RollbackAsync(ct);
            var other = await db.Stocktakes.AsNoTracking().Where(s => s.LocationId == locationId && s.Status == StocktakeStatus.Draft).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);
            return new(StocktakeWriteStatus.InProgress, OtherId: other);
        }
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return new(StocktakeWriteStatus.Ok, await GetStocktakeAsync(st.Id, ct));
    }

    /// <summary>Блокує заголовок; повертає помилку статусу (NotFound/Closed/VersionConflict) або null.</summary>
    private async Task<(Stocktake? St, StocktakeWriteResult? Fail)> LockStocktakeAsync(Guid id, int expectedVersion, CancellationToken ct)
    {
        var st = (await db.Stocktakes.FromSqlInterpolated($"SELECT * FROM beauty_stocktakes WHERE id = {id} FOR UPDATE").ToListAsync(ct)).FirstOrDefault();
        if (st is null) return (null, new(StocktakeWriteStatus.NotFound));
        if (st.Status != StocktakeStatus.Draft) return (null, new(StocktakeWriteStatus.Closed));
        if (st.Version != expectedVersion) return (null, new(StocktakeWriteStatus.VersionConflict, CurrentVersion: st.Version));
        return (st, null);
    }

    public async Task<StocktakeWriteResult> PutStocktakeLinesAsync(Guid id, int expectedVersion, IReadOnlyList<StocktakeLineChange> changes, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (st, fail) = await LockStocktakeAsync(id, expectedVersion, ct);
        if (fail is not null) { await tx.RollbackAsync(ct); db.ChangeTracker.Clear(); return fail; }

        var pids = changes.Select(c => c.ProductId).ToList();
        var existingProducts = (await db.Products.AsNoTracking().Where(p => pids.Contains(p.Id)).Select(p => p.Id).ToListAsync(ct)).ToHashSet();
        for (var i = 0; i < changes.Count; i++)
            if (!existingProducts.Contains(changes[i].ProductId)) { await tx.RollbackAsync(ct); db.ChangeTracker.Clear(); return new(StocktakeWriteStatus.ProductNotFound, Line: i); }

        var lines = await db.StocktakeLines.Where(l => l.StocktakeId == id).ToDictionaryAsync(l => l.ProductId, ct);
        foreach (var c in changes)
        {
            if (!lines.TryGetValue(c.ProductId, out var l))
            {
                lines[c.ProductId] = l = new StocktakeLine { Id = Guid.NewGuid(), StocktakeId = id, ProductId = c.ProductId, CreatedAt = now };
                db.StocktakeLines.Add(l);
            }
            l.CountedQuantity = c.CountedQuantity; l.SurplusExpiresOn = c.SurplusExpiresOn; l.UpdatedByUserId = userId;
        }
        if (lines.Count > InventoryService.MaxStocktakeLines) { await tx.RollbackAsync(ct); db.ChangeTracker.Clear(); return new(StocktakeWriteStatus.TooManyLines); }
        st!.Version++;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return new(StocktakeWriteStatus.Ok, await GetStocktakeAsync(id, ct));
    }

    public async Task<StocktakeWriteResult> CancelStocktakeAsync(Guid id, int expectedVersion, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (st, fail) = await LockStocktakeAsync(id, expectedVersion, ct);
        if (fail is not null) { await tx.RollbackAsync(ct); db.ChangeTracker.Clear(); return fail; }
        st!.Status = StocktakeStatus.Cancelled; st.CancelledAt = now; st.CancelledByUserId = userId; st.Version++;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return new(StocktakeWriteStatus.Ok, await GetStocktakeAsync(id, ct));
    }

    public async Task<StocktakeWriteResult> PostStocktakeAsync(Guid id, int expectedVersion, Guid userId, DateTimeOffset now, DateOnly today, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (st, fail) = await LockStocktakeAsync(id, expectedVersion, ct);
        if (fail is not null) { await tx.RollbackAsync(ct); db.ChangeTracker.Clear(); return fail; }

        var lines = await db.StocktakeLines.Where(l => l.StocktakeId == id && l.CountedQuantity != null).ToListAsync(ct);
        if (lines.Count == 0) { await tx.RollbackAsync(ct); db.ChangeTracker.Clear(); return new(StocktakeWriteStatus.Empty); }

        var loc = st!.LocationId;
        var pids = lines.Select(l => l.ProductId).ToList();
        var levels = await LockLevelsAsync(pids.Select(p => (loc, p)), ct);
        var products = await LockProductsAsync(pids, ct);
        var batches = await db.StockBatches.Where(b => b.LocationId == loc && pids.Contains(b.ProductId) && b.Quantity > 0).ToDictionaryAsync(b => b.Id, ct);

        var shortLines = new List<StockLine>();
        var surplus = new List<(StocktakeLine L, decimal Diff)>();
        foreach (var l in lines.OrderBy(l => l.ProductId))
        {
            var level = levels[(loc, l.ProductId)].Quantity;
            l.ExpectedQuantity = level;
            var diff = l.CountedQuantity!.Value - level;
            if (diff < 0) shortLines.Add(new StockLine(shortLines.Count, l.ProductId, -diff, null));
            else if (diff > 0)
            {
                if (products[l.ProductId].TrackExpiry && l.SurplusExpiresOn is null)
                {
                    await tx.RollbackAsync(ct); db.ChangeTracker.Clear();
                    return new(StocktakeWriteStatus.ExpiryRequired, ProductId: l.ProductId);
                }
                if (l.SurplusExpiresOn is { } sx && sx < today)
                {
                    await tx.RollbackAsync(ct); db.ChangeTracker.Clear();
                    return new(StocktakeWriteStatus.ExpiryInPast, ProductId: l.ProductId);
                }
                if (level + diff > InventoryService.MaxLevel)
                {
                    await tx.RollbackAsync(ct); db.ChangeTracker.Clear();
                    return new(StocktakeWriteStatus.QuantityOutOfRange, ProductId: l.ProductId);
                }
                surplus.Add((l, diff));
            }
        }

        StockOperation? op = null;
        if (shortLines.Count > 0 || surplus.Count > 0)
        {
            op = new StockOperation { Id = Guid.NewGuid(), Kind = StockOperationKind.Stocktake, LocationId = loc, StocktakeId = id, CreatedByUserId = userId, CreatedAt = now };
            db.StockOperations.Add(op);
        }
        if (shortLines.Count > 0)
        {
            var plan = FefoPlanner.Allocate(shortLines, [.. batches.Values.Select(ToRow)], skipExpired: false, today);
            if (plan.Shortages.Count > 0) { await tx.RollbackAsync(ct); db.ChangeTracker.Clear(); return new(StocktakeWriteStatus.Insufficient); }
            foreach (var t in plan.Takes)
            {
                batches[t.BatchId].Quantity -= t.Quantity;
                levels[(loc, t.ProductId)].Quantity -= t.Quantity;
                Move(op!, loc, t.ProductId, t.BatchId, StockMovementType.StocktakeAdjust, -t.Quantity, t.UnitCost, userId, now);
            }
        }
        foreach (var (l, diff) in surplus)
        {
            var p = products[l.ProductId];
            var b = new StockBatch
            {
                Id = Guid.NewGuid(), LocationId = loc, ProductId = p.Id, OperationId = op!.Id, SupplierId = p.SupplierId, ReceivedAt = now, ExpiresOn = l.SurplusExpiresOn,
                InitialQuantity = diff, Quantity = diff, UnitCost = p.PurchasePrice, CreatedAt = now, UpdatedAt = now,
            };
            db.StockBatches.Add(b);
            levels[(loc, p.Id)].Quantity += diff;
            Move(op, loc, p.Id, b.Id, StockMovementType.StocktakeAdjust, diff, p.PurchasePrice, userId, now);
        }

        st.Status = StocktakeStatus.Posted; st.PostedAt = now; st.PostedByUserId = userId; st.Version++;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return new(StocktakeWriteStatus.Ok, await GetStocktakeAsync(id, ct), op is null ? null : await GetOperationAsync(op.Id, ct));
    }
}
