using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyCommon;
using Microsoft.Extensions.Logging;

namespace BeautyCrm.Application.Features.BeautyInventory;

public sealed partial class InventoryService
{
    // ---------- ідемпотентність (§32.4) ----------

    private static Result<string> KeyHash(string? key)
    {
        if (string.IsNullOrEmpty(key)) return Error.Validation("idempotency_key_required", "Idempotency-Key header is required.");
        if (key.Length > 128 || key.Any(c => c < 0x21 || c > 0x7E)) return Error.Validation("invalid_idempotency_key", "Idempotency-Key is invalid.");
        return Sha(key);
    }

    private static string Sha(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    private static string RequestHash(string kind, object req) => Sha(kind + "|" + JsonSerializer.Serialize(req));

    private static readonly Error KeyReused = Error.Validation("idempotency_key_reused", "This Idempotency-Key was already used with a different request.");

    /// <summary>Повтор: те саме тіло -> Ok(replayed); інше (або операція поза scope) -> idempotency_key_reused; ключа немає -> null.</summary>
    private async Task<Result<OperationResult>?> ReplayAsync(Actor actor, string keyHash, string requestHash, CancellationToken ct)
    {
        if (await store.FindOperationByKeyAsync(keyHash, ct) is not { } found) return null;
        if (found.RequestHash != requestHash) return KeyReused;
        var scope = await ViewScopeAsync(actor, ct);
        if (!(scope.Contains(found.Operation.LocationId) || (found.Operation.ToLocationId is { } t && scope.Contains(t)))) return KeyReused;
        LogOperation(found.Operation, actor.UserId, true);
        return new OperationResult(Present(actor, found.Operation, scope), true);
    }

    private void LogOperation(OperationDto op, Guid user, bool replayed) =>
        logger?.LogInformation("inventory_operation kind={Kind} op={Op} location={Location} to={To} lines={Lines} user={User} replayed={Replayed}",
            op.Kind, op.Id, op.LocationId, op.ToLocationId?.ToString() ?? "-", op.ProductCount, user, replayed);

    private async Task<Result<OperationResult>> FinishAsync(Actor actor, StockWriteResult w, string keyHash, string requestHash, CancellationToken ct)
    {
        switch (w.Status)
        {
            case StockWriteStatus.Ok:
                LogOperation(w.Operation!, actor.UserId, false);
                return new OperationResult(Present(actor, w.Operation!, await ViewScopeAsync(actor, ct)), false);
            case StockWriteStatus.DuplicateKey:
                return await ReplayAsync(actor, keyHash, requestHash, ct) ?? KeyReused;
            case StockWriteStatus.Rejected:
                return V(w.Code!, RejectMessage(w.Code!), w.Line);
            default:
                return InsufficientStock(w.Shortages!);
        }
    }

    /// <summary>Коди відмов, що їх сховище повертає з перевірок ПІД блокуванням (повтор перевірок сервісу, див. TASK-880 L3).</summary>
    private static string RejectMessage(string code) => code switch
    {
        "quantity_out_of_range" => "Resulting stock is above the maximum allowed.",
        "product_inactive" => "Product is inactive.",
        "product_disabled_for_location" => "Product is disabled for this location.",
        "location_inactive" => "Location is inactive.",
        "invalid_quantity" => "Quantity must be a whole number for this unit.",
        "expiry_required" => "Expiry date is required.",
        "invalid_expiry" => "Expiry date is in the past.",
        _ => "Request rejected.",
    };

    private static Error InsufficientStock(IReadOnlyList<StockShortage> shortages) =>
        new(ErrorKind.Validation, "insufficient_stock", "Not enough stock.", null, new Dictionary<string, object?>
        {
            ["lines"] = shortages.Select(s => new { line = s.Line, productId = s.ProductId, batchId = s.BatchId, requested = s.Requested, available = s.Available }).ToList(),
        });

    // ---------- спільне ----------

    private async Task<Result<(LocationRow Loc, DateOnly Today)>> ManagedLocationAsync(Actor actor, Guid? id, Scope scope, IReadOnlyDictionary<Guid, LocationRow> locs)
    {
        if (id is not { } lid || !scope.Contains(lid) || !locs.TryGetValue(lid, out var loc)) return LocationNotFound;
        await Task.CompletedTask;
        return (loc, SlotCalculator.LocalDate(clock.GetUtcNow(), loc.Timezone));
    }

    private Result<IReadOnlyDictionary<Guid, ProductDto>> RequireProducts(IReadOnlyDictionary<Guid, ProductDto> found, IEnumerable<(int Line, Guid? ProductId)> lines)
    {
        foreach (var (line, pid) in lines)
            if (pid is not { } id || !found.ContainsKey(id)) return WithLine(ProductNotFound, line);
        return Result<IReadOnlyDictionary<Guid, ProductDto>>.Ok(found);
    }

    // ---------- надходження ----------

    public async Task<Result<OperationResult>> ReceiveAsync(Actor actor, string? idempotencyKey, ReceiptRequest req, CancellationToken ct)
    {
        if (NeedManage(actor) is { } f) return f;
        var key = KeyHash(idempotencyKey);
        if (!key.IsOk) return key.Error!;
        var reqHash = RequestHash("receipt", req);
        if (await ReplayAsync(actor, key.Value!, reqHash, ct) is { } replay) return replay;

        var lines = req.Lines;
        if (lines?.Any(l => l.UnitCost is not null) == true && NeedCostsEdit(actor) is { } c) return c;

        // форма
        if (lines is null || lines.Count == 0) return V("invalid_lines", "At least one line is required.");
        if (lines.Count > MaxLines) return V("too_many_lines", $"At most {MaxLines} lines.");
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            if (l.ProductId is null) return V("invalid_lines", "productId is required.", i);
            if (l.Quantity is not { } q || !ValidStockQuantity(q, null, false)) return V("invalid_quantity", "Quantity is invalid.", i);
            if (l.UnitCost is { } uc && !ValidMoney(uc)) return V("invalid_price", "Price must be 0..1000000 with at most 2 decimals.", i);
            if (l.ExpiresOn is not null && !TryDate(l.ExpiresOn, out _)) return V("invalid_date", "expiresOn must be yyyy-MM-dd.", i);
            if (l.LotNumber is { Length: > 64 }) return V("invalid_lot_number", "Lot number is too long (max 64).", i);
        }
        var doc = NullIfEmpty(req.DocumentNumber);
        if (doc is { Length: > 64 }) return V("invalid_document_number", "Document number is too long (max 64).");
        var note = NullIfEmpty(req.Note);
        if (note is { Length: > 500 }) return V("invalid_note", "Note is too long (max 500).");
        DateOnly? receivedOn = null;
        if (req.ReceivedOn is not null)
        {
            if (!TryDate(req.ReceivedOn, out var ro)) return V("invalid_date", "receivedOn must be yyyy-MM-dd.");
            receivedOn = ro;
        }

        // заклад і scope
        var scope = await ManageScopeAsync(actor, ct);
        var locs = await LocationsAsync(ct);
        var loc = await ManagedLocationAsync(actor, req.LocationId, scope, locs);
        if (!loc.IsOk) return loc.Error!;
        var today = loc.Value.Today;
        if (receivedOn is { } r && (r > today || r < today.AddDays(-365))) return V("invalid_date", "receivedOn is out of the allowed range.");

        // товари / постачальник
        var ids = lines.Select(l => l.ProductId!.Value).Distinct().ToList();
        var found = await store.GetProductsAsync(ids, ct);
        var prods = RequireProducts(found, lines.Select((l, i) => (i, l.ProductId)));
        if (!prods.IsOk) return prods.Error!;
        SupplierDto? supplier = null;
        if (req.SupplierId is { } sid && (supplier = await store.GetSupplierAsync(sid, ct)) is null) return SupplierNotFound;

        // активність і дозвіл
        if (!loc.Value.Loc.IsActive) return Error.Validation("location_inactive", "Location is inactive.");
        if (supplier is { IsActive: false }) return Error.Validation("supplier_inactive", "Supplier is inactive.");
        var enabled = await store.GetEnabledMapAsync(loc.Value.Loc.Id, ids, ct);
        for (var i = 0; i < lines.Count; i++)
        {
            var p = found[lines[i].ProductId!.Value];
            if (!p.IsActive) return V("product_inactive", "Product is inactive.", i);
            if (enabled.TryGetValue(p.Id, out var en) && !en) return V("product_disabled_for_location", "Product is disabled for this location.", i);
        }
        var built = new List<ReceiptLine>();
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            var p = found[l.ProductId!.Value];
            if (!ValidStockQuantity(l.Quantity!.Value, p.Unit, false)) return V("invalid_quantity", "Quantity must be a whole number for this unit.", i);
            DateOnly? exp = l.ExpiresOn is null ? null : DateOnly.ParseExact(l.ExpiresOn, "yyyy-MM-dd");
            if (exp is null && p.TrackExpiry) return V("expiry_required", "Expiry date is required.", i);
            if (exp is { } e && e < today) return V("invalid_expiry", "Expiry date is in the past.", i);
            built.Add(new ReceiptLine(p.Id, l.Quantity.Value, l.UnitCost ?? p.PurchasePrice, exp, NullIfEmpty(l.LotNumber)));
        }

        var header = new OperationHeader(Guid.NewGuid(), "receipt", loc.Value.Loc.Id, null, req.SupplierId, doc, receivedOn, null, note,
            actor.UserId, clock.GetUtcNow(), key.Value!, reqHash);
        return await FinishAsync(actor, await store.PostReceiptAsync(header, built, ct), key.Value!, reqHash, ct);
    }

    // ---------- списання / переміщення ----------

    private Result<IReadOnlyList<StockLine>> ValidateStockLines(IReadOnlyList<StockLineRequest>? lines)
    {
        if (lines is null || lines.Count == 0) return V("invalid_lines", "At least one line is required.");
        if (lines.Count > MaxLines) return V("too_many_lines", $"At most {MaxLines} lines.");
        var seen = new HashSet<(Guid, Guid?)>();
        var list = new List<StockLine>();
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            if (l.ProductId is not { } pid) return V("invalid_lines", "productId is required.", i);
            if (l.Quantity is not { } q || !ValidStockQuantity(q, null, false)) return V("invalid_quantity", "Quantity is invalid.", i);
            if (!seen.Add((pid, l.BatchId))) return V("duplicate_line", "Duplicate line.", i);
            list.Add(new StockLine(i, pid, q, l.BatchId));
        }
        return Result<IReadOnlyList<StockLine>>.Ok(list);
    }

    /// <summary>Товари (404), цілі кількості для pcs|pack, явні партії (404 поза закладом/товаром).</summary>
    private async Task<Result<IReadOnlyDictionary<Guid, BatchRef>>> ResolveLinesAsync(
        IReadOnlyList<StockLine> lines, Guid locationId, CancellationToken ct)
    {
        var found = await store.GetProductsAsync(lines.Select(l => l.ProductId).Distinct().ToList(), ct);
        foreach (var l in lines)
            if (!found.ContainsKey(l.ProductId)) return WithLine(ProductNotFound, l.Line);
        var batchIds = lines.Where(l => l.BatchId is not null).Select(l => l.BatchId!.Value).Distinct().ToList();
        var refs = batchIds.Count == 0 ? new Dictionary<Guid, BatchRef>() : new Dictionary<Guid, BatchRef>(await store.GetBatchRefsAsync(batchIds, ct));
        foreach (var l in lines)
            if (l.BatchId is { } b && (!refs.TryGetValue(b, out var r) || r.LocationId != locationId || r.ProductId != l.ProductId))
                return WithLine(Error.NotFound("batch_not_found", "Batch not found."), l.Line);
        foreach (var l in lines)
            if (!ValidStockQuantity(l.Quantity, found[l.ProductId].Unit, false)) return V("invalid_quantity", "Quantity must be a whole number for this unit.", l.Line);
        return Result<IReadOnlyDictionary<Guid, BatchRef>>.Ok(refs);
    }

    public async Task<Result<OperationResult>> WriteOffAsync(Actor actor, string? idempotencyKey, WriteOffRequest req, CancellationToken ct)
    {
        if (NeedManage(actor) is { } f) return f;
        var key = KeyHash(idempotencyKey);
        if (!key.IsOk) return key.Error!;
        var reqHash = RequestHash("write_off", req);
        if (await ReplayAsync(actor, key.Value!, reqHash, ct) is { } replay) return replay;

        var reason = req.Reason?.Trim();
        if (reason is null || !Reasons.Contains(reason, StringComparer.Ordinal)) return V("invalid_reason", "Reason is invalid.");
        var note = NullIfEmpty(req.Note);
        if (note is { Length: > 500 }) return V("invalid_note", "Note is too long (max 500).");
        if (note is null && reason is "correction" or "other") return Error.Validation("note_required", "A note is required for this reason.");
        var lines = ValidateStockLines(req.Lines);
        if (!lines.IsOk) return lines.Error!;

        var scope = await ManageScopeAsync(actor, ct);
        var loc = await ManagedLocationAsync(actor, req.LocationId, scope, await LocationsAsync(ct));
        if (!loc.IsOk) return loc.Error!;
        var resolved = await ResolveLinesAsync(lines.Value!, loc.Value.Loc.Id, ct);
        if (!resolved.IsOk) return resolved.Error!;

        var header = new OperationHeader(Guid.NewGuid(), "write_off", loc.Value.Loc.Id, null, null, null, null, reason, note,
            actor.UserId, clock.GetUtcNow(), key.Value!, reqHash);
        return await FinishAsync(actor, await store.PostWriteOffAsync(header, lines.Value!, loc.Value.Today, ct), key.Value!, reqHash, ct);
    }

    public async Task<Result<OperationResult>> TransferAsync(Actor actor, string? idempotencyKey, TransferRequest req, CancellationToken ct)
    {
        if (NeedManage(actor) is { } f) return f;
        var key = KeyHash(idempotencyKey);
        if (!key.IsOk) return key.Error!;
        var reqHash = RequestHash("transfer", req);
        if (await ReplayAsync(actor, key.Value!, reqHash, ct) is { } replay) return replay;

        var note = NullIfEmpty(req.Note);
        if (note is { Length: > 500 }) return V("invalid_note", "Note is too long (max 500).");
        if (req.FromLocationId is not null && req.FromLocationId == req.ToLocationId) return Error.Validation("same_location", "Choose a different destination location.");
        var lines = ValidateStockLines(req.Lines);
        if (!lines.IsOk) return lines.Error!;

        var scope = await ManageScopeAsync(actor, ct);
        var locs = await LocationsAsync(ct);
        var from = await ManagedLocationAsync(actor, req.FromLocationId, scope, locs);
        if (!from.IsOk) return from.Error!;
        var to = await ManagedLocationAsync(actor, req.ToLocationId, scope, locs);
        if (!to.IsOk) return to.Error!;
        var resolved = await ResolveLinesAsync(lines.Value!, from.Value.Loc.Id, ct);
        if (!resolved.IsOk) return resolved.Error!;

        foreach (var l in lines.Value!)
            if (l.BatchId is { } b && FefoPlanner.IsExpired(resolved.Value![b].ExpiresOn, from.Value.Today))
                return V("batch_expired", "The batch is expired and cannot be transferred.", l.Line);
        if (!to.Value.Loc.IsActive) return Error.Validation("location_inactive", "Location is inactive.");
        var ids = lines.Value!.Select(l => l.ProductId).Distinct().ToList();
        var enabled = await store.GetEnabledMapAsync(to.Value.Loc.Id, ids, ct);
        foreach (var l in lines.Value!)
            if (enabled.TryGetValue(l.ProductId, out var en) && !en) return V("product_disabled_for_location", "Product is disabled for this location.", l.Line);

        var header = new OperationHeader(Guid.NewGuid(), "transfer", from.Value.Loc.Id, to.Value.Loc.Id, null, null, null, null, note,
            actor.UserId, clock.GetUtcNow(), key.Value!, reqHash);
        return await FinishAsync(actor, await store.PostTransferAsync(header, lines.Value!, from.Value.Today, ct), key.Value!, reqHash, ct);
    }

    // ---------- інвентаризації ----------

    public async Task<Result<Page<StocktakeSummaryDto>>> ListStocktakesAsync(Actor actor, StocktakeListQuery q, CancellationToken ct)
    {
        if (NeedView(actor) is { } f) return f;
        var status = q.Status?.ToLowerInvariant();
        if (status is not (null or "draft" or "posted" or "cancelled")) return V("invalid_status", "status is invalid.");
        var scope = await ViewScopeAsync(actor, ct);
        if (q.LocationId is { } l && (!scope.Contains(l) || !(await LocationsAsync(ct)).ContainsKey(l))) return LocationNotFound;
        var hash = ParamHash("stocktakes", q.LocationId, status);
        var after = DecodeCursor(q.Cursor, hash, "tg");
        if (!after.IsOk) return after.Error!;
        IReadOnlyList<Guid>? ids = scope.All ? null : [.. scope.Ids];
        var n = Take(q.Take);
        List<StocktakeSummaryDto> rows = ids is { Count: 0 } ? [] : [.. await store.ListStocktakesAsync(new StocktakeFilter(ids, q.LocationId, status), after.Value, n + 1, ct)];
        var hasMore = rows.Count > n;
        if (hasMore) rows.RemoveAt(n);
        return new Page<StocktakeSummaryDto>(rows, hasMore, hasMore ? EncodeCursor(hash, TimeCursorPart(rows[^1].CreatedAt), rows[^1].Id.ToString()) : null);
    }

    private static readonly Error StocktakeNotFound = Error.NotFound("stocktake_not_found", "Stocktake not found.");

    public async Task<Result<StocktakeDto>> GetStocktakeAsync(Actor actor, Guid id, CancellationToken ct)
    {
        if (NeedView(actor) is { } f) return f;
        var scope = await ViewScopeAsync(actor, ct);
        if (await store.GetStocktakeAsync(id, ct) is not { } s || !scope.Contains(s.LocationId)) return StocktakeNotFound;
        return s;
    }

    public async Task<Result<StocktakeDto>> CreateStocktakeAsync(Actor actor, StocktakeCreateRequest req, CancellationToken ct)
    {
        if (NeedManage(actor) is { } f) return f;
        var note = NullIfEmpty(req.Note);
        if (note is { Length: > 500 }) return V("invalid_note", "Note is too long (max 500).");
        if (req.ProductIds is { Count: > MaxStocktakeLines }) return V("too_many_lines", $"At most {MaxStocktakeLines} lines.");
        var scope = await ManageScopeAsync(actor, ct);
        var loc = await ManagedLocationAsync(actor, req.LocationId, scope, await LocationsAsync(ct));
        if (!loc.IsOk) return loc.Error!;
        var r = await store.CreateStocktakeAsync(loc.Value.Loc.Id, note, req.ProductIds?.Distinct().ToList(), actor.UserId, clock.GetUtcNow(), ct);
        return MapStocktake(actor, r) is { } ok ? ok : r.Status switch
        {
            StocktakeWriteStatus.InProgress => new Error(ErrorKind.Conflict, "stocktake_in_progress", "A stocktake is already in progress.", null,
                new Dictionary<string, object?> { ["stocktakeId"] = r.OtherId }),
            StocktakeWriteStatus.ProductNotFound => ProductNotFound,
            StocktakeWriteStatus.TooManyLines => V("too_many_lines", "Too many lines; pass productIds."),
            _ => LocationNotFound,
        };
    }

    private static Result<StocktakeDto>? MapStocktake(Actor actor, StocktakeWriteResult r) => r.Status == StocktakeWriteStatus.Ok ? Result<StocktakeDto>.Ok(r.Stocktake!) : null;

    private Error? StocktakeFailure(StocktakeWriteResult r) => r.Status switch
    {
        StocktakeWriteStatus.NotFound => StocktakeNotFound,
        StocktakeWriteStatus.Closed => Error.Conflict("stocktake_closed", "The stocktake is already posted or cancelled."),
        StocktakeWriteStatus.VersionConflict => new Error(ErrorKind.Conflict, "stocktake_version_conflict", "The stocktake was changed by another user.", null,
            new Dictionary<string, object?> { ["currentVersion"] = r.CurrentVersion }),
        StocktakeWriteStatus.ProductNotFound => r.Line is { } l ? WithLine(ProductNotFound, l) : ProductNotFound,
        StocktakeWriteStatus.TooManyLines => V("too_many_lines", $"At most {MaxStocktakeLines} lines."),
        StocktakeWriteStatus.Empty => Error.Validation("stocktake_empty", "Count at least one line."),
        StocktakeWriteStatus.ExpiryRequired => new Error(ErrorKind.Validation, "expiry_required", "Expiry date is required.", null,
            new Dictionary<string, object?> { ["productId"] = r.ProductId }),
        StocktakeWriteStatus.Insufficient => Error.Validation("insufficient_stock", "Not enough stock."),
        StocktakeWriteStatus.ExpiryInPast => new Error(ErrorKind.Validation, "invalid_expiry", "Expiry date is in the past.", null,
            new Dictionary<string, object?> { ["productId"] = r.ProductId }),
        StocktakeWriteStatus.QuantityOutOfRange => new Error(ErrorKind.Validation, "quantity_out_of_range", "Resulting stock is above the maximum allowed.", null,
            new Dictionary<string, object?> { ["productId"] = r.ProductId }),
        _ => null,
    };

    private async Task<Error?> StocktakeScopeAsync(Actor actor, Guid id, CancellationToken ct)
    {
        var scope = await ManageScopeAsync(actor, ct);
        return await store.GetStocktakeAsync(id, ct) is { } s && scope.Contains(s.LocationId) ? null : StocktakeNotFound;
    }

    public async Task<Result<StocktakeDto>> PutStocktakeLinesAsync(Actor actor, Guid id, StocktakeLinesRequest req, CancellationToken ct)
    {
        if (NeedManage(actor) is { } f) return f;
        if (req.ExpectedVersion is not { } ver) return Error.Validation("expected_version_required", "expectedVersion is required.");
        var lines = req.Lines;
        if (lines is null || lines.Count == 0) return V("invalid_lines", "At least one line is required.");
        if (lines.Count > MaxCountLines) return V("too_many_lines", $"At most {MaxCountLines} lines per request.");
        var seen = new HashSet<Guid>();
        var changes = new List<StocktakeLineChange>();
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            if (l.ProductId is not { } pid) return V("invalid_lines", "productId is required.", i);
            if (!seen.Add(pid)) return V("duplicate_line", "Duplicate product.", i);
            if (l.CountedQuantity is { } cq && !ValidStockQuantity(cq, null, true)) return V("invalid_quantity", "Counted quantity is invalid.", i);
            DateOnly? sx = null;
            if (l.SurplusExpiresOn is not null)
            {
                if (!TryDate(l.SurplusExpiresOn, out var d)) return V("invalid_date", "surplusExpiresOn must be yyyy-MM-dd.", i);
                sx = d;
            }
            changes.Add(new StocktakeLineChange(pid, l.CountedQuantity, sx));
        }
        if (await StocktakeScopeAsync(actor, id, ct) is { } nf) return nf;
        var found = await store.GetProductsAsync([.. seen], ct);
        for (var i = 0; i < lines.Count; i++)
        {
            if (!found.TryGetValue(changes[i].ProductId, out var p)) return WithLine(ProductNotFound, i);
            if (changes[i].CountedQuantity is { } cq && !ValidStockQuantity(cq, p.Unit, true)) return V("invalid_quantity", "Quantity must be a whole number for this unit.", i);
        }
        var r = await store.PutStocktakeLinesAsync(id, ver, changes, actor.UserId, clock.GetUtcNow(), ct);
        return StocktakeFailure(r) is { } e ? e : r.Stocktake!;
    }

    public async Task<Result<StocktakePostDto>> PostStocktakeAsync(Actor actor, Guid id, StocktakeVersionRequest req, CancellationToken ct)
    {
        if (NeedManage(actor) is { } f) return f;
        if (req.ExpectedVersion is not { } ver) return Error.Validation("expected_version_required", "expectedVersion is required.");
        if (await StocktakeScopeAsync(actor, id, ct) is { } nf) return nf;
        var st = await store.GetStocktakeAsync(id, ct);
        var loc = (await LocationsAsync(ct)).GetValueOrDefault(st!.LocationId);
        if (loc is null) return LocationNotFound;
        var now = clock.GetUtcNow();
        var r = await store.PostStocktakeAsync(id, ver, actor.UserId, now, SlotCalculator.LocalDate(now, loc.Timezone), ct);
        if (StocktakeFailure(r) is { } e) return e;
        var adjustments = r.Operation?.Movements.Count ?? 0;
        logger?.LogInformation("inventory_stocktake_posted id={Id} location={Location} adjustments={Adjustments} user={User}", id, loc.Id, adjustments, actor.UserId);
        var scope = await ViewScopeAsync(actor, ct);
        return new StocktakePostDto(r.Stocktake!, r.Operation is null ? null : Present(actor, r.Operation, scope));
    }

    public async Task<Result<StocktakeDto>> CancelStocktakeAsync(Actor actor, Guid id, StocktakeVersionRequest req, CancellationToken ct)
    {
        if (NeedManage(actor) is { } f) return f;
        if (req.ExpectedVersion is not { } ver) return Error.Validation("expected_version_required", "expectedVersion is required.");
        if (await StocktakeScopeAsync(actor, id, ct) is { } nf) return nf;
        var r = await store.CancelStocktakeAsync(id, ver, actor.UserId, clock.GetUtcNow(), ct);
        return StocktakeFailure(r) is { } e ? e : r.Stocktake!;
    }
}
