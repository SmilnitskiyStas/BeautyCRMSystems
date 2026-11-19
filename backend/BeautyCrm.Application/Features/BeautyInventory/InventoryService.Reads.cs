using System.Globalization;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyCommon;

namespace BeautyCrm.Application.Features.BeautyInventory;

public sealed partial class InventoryService
{
    private static bool IsLow(decimal quantity, decimal? effectiveMin) => effectiveMin is > 0 && quantity < effectiveMin;

    // ---------- залишки ----------

    public async Task<Result<StockPageDto>> GetStockAsync(Actor actor, StockListQuery q, CancellationToken ct)
    {
        if (NeedView(actor) is { } f) return f;
        if (q.LocationId is not { } locId) return V("location_required", "locationId is required.");
        var scope = await ViewScopeAsync(actor, ct);
        var locs = await LocationsAsync(ct);
        if (!scope.Contains(locId) || !locs.TryGetValue(locId, out var loc)) return LocationNotFound;

        var state = (q.State ?? "all").ToLowerInvariant();
        if (state is not ("all" or "low" or "in_stock" or "out_of_stock")) return V("invalid_status", "state is invalid.");
        var search = NullIfEmpty(q.Search);
        var days = ExpiringDays(q.ExpiringWithinDays);
        var today = SlotCalculator.LocalDate(clock.GetUtcNow(), loc.Timezone);

        var hash = ParamHash("stock", locId, state, search, q.Category, q.Purpose, q.ExpiringWithinDays);
        var after = DecodeCursor(q.Cursor, hash, "ng");
        if (!after.IsOk) return after.Error!;
        var n = Take(q.Take);
        var filter = new StockFilter(locId, today, days, state, search, q.Category?.Trim(), q.Purpose, q.ExpiringWithinDays is not null);
        var raw = (await store.ListStockAsync(filter, after.Value, n + 1, ct)).ToList();
        var hasMore = raw.Count > n;
        if (hasMore) raw.RemoveAt(n);
        var costs = SeesCosts(actor);
        var rows = raw.Select(r =>
        {
            var eff = r.SettingMinStock ?? r.ProductMinStock;
            return new StockRowDto(r.Product, locId, r.Quantity, eff, IsLow(r.Quantity, eff), r.IsEnabled, r.NearestExpiresOn, r.ExpiredQuantity, r.ExpiringQuantity,
                costs ? r.Value : null, costs ? r.ValueIncomplete : null);
        }).ToList();
        var next = hasMore ? EncodeCursor(hash, NameKey(rows[^1].Product.Name), rows[^1].Product.Id.ToString()) : null;
        return new StockPageDto(new StockLocationBrief(loc.Id, loc.Name, loc.Timezone, loc.IsActive), rows, hasMore, next);
    }

    public async Task<Result<StockSummaryPageDto>> GetSummaryAsync(Actor actor, SummaryListQuery q, CancellationToken ct)
    {
        if (NeedView(actor) is { } f) return f;
        var scope = await ViewScopeAsync(actor, ct);
        var locs = (await store.ListInventoryLocationsAsync(ct)).Where(l => scope.Contains(l.Id)).OrderBy(l => l.Name, StringComparer.Ordinal).ThenBy(l => l.Id).ToList();
        var header = locs.Select(l => new StockSummaryLocation(l.Id, l.Name, l.IsActive)).ToList();
        var search = NullIfEmpty(q.Search);
        var hash = ParamHash("summary", search, q.Category, q.Purpose, q.LowOnly);
        var after = DecodeCursor(q.Cursor, hash, "ng");
        if (!after.IsOk) return after.Error!;
        var n = Take(q.Take);

        var items = new List<StockSummaryRowDto>();
        var hasMore = false;
        if (locs.Count > 0)
        {
            var filter = new SummaryFilter([.. locs.Select(l => l.Id)], search, q.Category?.Trim(), q.Purpose, q.LowOnly == true);
            var groups = (await store.ListSummaryAsync(filter, after.Value, n + 1, ct)).GroupBy(r => r.Product.Id).ToList();
            hasMore = groups.Count > n;
            foreach (var g in groups.Take(n))
            {
                var p = g.First().Product;
                var perLoc = g.Select(r => new StockSummaryLocationQty(r.LocationId, r.Quantity, IsLow(r.Quantity, r.SettingMinStock ?? r.ProductMinStock))).ToList();
                items.Add(new StockSummaryRowDto(p, g.Sum(r => r.Quantity), perLoc.Any(x => x.IsLow), perLoc,
                    SeesCosts(actor) ? g.Sum(r => r.Value) : null, SeesCosts(actor) ? g.Any(r => r.ValueIncomplete) : null));
            }
        }
        var next = hasMore ? EncodeCursor(hash, NameKey(items[^1].Product.Name), items[^1].Product.Id.ToString()) : null;
        return new StockSummaryPageDto(header, items, hasMore, next);
    }

    // ---------- партії ----------

    public async Task<Result<Page<BatchDto>>> ListBatchesAsync(Actor actor, BatchListQuery q, CancellationToken ct)
    {
        if (NeedView(actor) is { } f) return f;
        var status = (q.Status ?? "active").ToLowerInvariant();
        if (status is not ("active" or "expiring" or "expired" or "empty" or "all")) return V("invalid_status", "status is invalid.");
        var scope = await ViewScopeAsync(actor, ct);
        var locs = await LocationsAsync(ct);
        IEnumerable<LocationRow> chosen;
        if (q.LocationId is { } lid)
        {
            if (!scope.Contains(lid) || !locs.TryGetValue(lid, out var one)) return LocationNotFound;
            chosen = [one];
        }
        else chosen = locs.Values.Where(l => scope.Contains(l.Id));

        var now = clock.GetUtcNow();
        var groups = chosen.GroupBy(l => SlotCalculator.LocalDate(now, l.Timezone))
            .Select(g => ((IReadOnlyList<Guid>)g.Select(l => l.Id).ToList(), g.Key)).ToList();
        var days = ExpiringDays(q.ExpiringWithinDays);
        var hash = ParamHash("batches", q.LocationId, q.ProductId, status, days);
        var after = DecodeCursor(q.Cursor, hash, "fDtgg");
        if (!after.IsOk) return after.Error!;
        if (after.Value is { } av && av[0] == "0" && av[1].Length == 0) return Error.Validation("invalid_cursor", "Cursor is invalid.");
        var n = Take(q.Take);
        List<BatchDto> rows = groups.Count == 0 ? [] : [.. await store.ListBatchesAsync(new BatchFilter(groups, q.ProductId, status, days), after.Value, n + 1, ct)];
        var hasMore = rows.Count > n;
        if (hasMore) rows.RemoveAt(n);
        var items = rows.Select(b => SeesCosts(actor) ? b : b with { UnitCost = null }).ToList();
        return new Page<BatchDto>(items, hasMore, hasMore ? BatchCursor(hash, rows[^1]) : null);
    }

    private static string BatchCursor(string hash, BatchDto b) => EncodeCursor(hash,
        b.ExpiresOn is null ? "1" : "0", b.ExpiresOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "", b.ReceivedAt.UtcTicks.ToString(CultureInfo.InvariantCulture), b.LocationId.ToString(), b.Id.ToString());

    // ---------- журнал і операції ----------

    private Result<(DateTimeOffset From, DateTimeOffset To)> ParseRange(string? from, string? to, bool defaults)
    {
        DateTimeOffset? f = null, t = null;
        if (from is not null)
        {
            if (!DateTimeOffset.TryParse(from, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var x)) return V("invalid_range", "from is invalid.");
            f = x.ToUniversalTime();
        }
        if (to is not null)
        {
            if (!DateTimeOffset.TryParse(to, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var x)) return V("invalid_range", "to is invalid.");
            t = x.ToUniversalTime();
        }
        if (defaults)
        {
            t ??= clock.GetUtcNow();
            f ??= t.Value.AddDays(-30);
        }
        if (f is { } a && t is { } b && (a > b || b - a > TimeSpan.FromDays(MaxRangeDays)))
            return V("invalid_range", $"from..to must be a valid range of at most {MaxRangeDays} days.");
        return (f ?? DateTimeOffset.MinValue, t ?? DateTimeOffset.MaxValue);
    }

    private static string TimeCursorPart(DateTimeOffset t) => t.UtcTicks.ToString(CultureInfo.InvariantCulture);

    public async Task<Result<Page<MovementDto>>> ListMovementsAsync(Actor actor, MovementListQuery q, CancellationToken ct)
    {
        if (NeedView(actor) is { } f) return f;
        var scope = await ViewScopeAsync(actor, ct);
        var locs = await LocationsAsync(ct);
        if (q.LocationId is { } l && (!scope.Contains(l) || !locs.ContainsKey(l))) return LocationNotFound;
        var range = ParseRange(q.From, q.To, true);
        if (!range.IsOk) return range.Error!;
        var hash = ParamHash("movements", q.LocationId, q.ProductId, q.Type, q.OperationId, q.From is null ? null : range.Value.From.UtcTicks, q.To is null ? null : range.Value.To.UtcTicks);
        var after = DecodeCursor(q.Cursor, hash, "tg");
        if (!after.IsOk) return after.Error!;
        IReadOnlyList<Guid>? ids = q.LocationId is { } one ? [one] : scope.All ? null : [.. scope.Ids];
        var n = Take(q.Take);
        List<MovementDto> rows = ids is { Count: 0 } ? [] : [.. await store.ListMovementsAsync(
            new MovementFilter(ids, q.ProductId, q.Type, q.OperationId, range.Value.From, range.Value.To), after.Value, n + 1, ct)];
        var hasMore = rows.Count > n;
        if (hasMore) rows.RemoveAt(n);
        var items = rows.Select(m => SeesCosts(actor) ? m : m with { UnitCost = null }).ToList();
        return new Page<MovementDto>(items, hasMore, hasMore ? EncodeCursor(hash, TimeCursorPart(rows[^1].OccurredAt), rows[^1].Id.ToString()) : null);
    }

    public async Task<Result<Page<OperationSummaryDto>>> ListOperationsAsync(Actor actor, OperationListQuery q, CancellationToken ct)
    {
        if (NeedView(actor) is { } f) return f;
        var scope = await ViewScopeAsync(actor, ct);
        var locs = await LocationsAsync(ct);
        if (q.LocationId is { } l && (!scope.Contains(l) || !locs.ContainsKey(l))) return LocationNotFound;
        var range = ParseRange(q.From, q.To, false);
        if (!range.IsOk) return range.Error!;
        var hash = ParamHash("operations", q.Kind, q.LocationId, q.From is null ? null : range.Value.From.UtcTicks, q.To is null ? null : range.Value.To.UtcTicks);
        var after = DecodeCursor(q.Cursor, hash, "tg");
        if (!after.IsOk) return after.Error!;
        IReadOnlyList<Guid>? ids = scope.All ? null : [.. scope.Ids];
        var n = Take(q.Take);
        List<OperationSummaryDto> rows = ids is { Count: 0 } ? [] : [.. await store.ListOperationsAsync(
            new OperationFilter(ids, q.Kind, q.LocationId, q.From is null ? null : range.Value.From, q.To is null ? null : range.Value.To), after.Value, n + 1, ct)];
        var hasMore = rows.Count > n;
        if (hasMore) rows.RemoveAt(n);
        var items = rows.Select(o => SeesCosts(actor) ? o : o with { TotalCost = null }).ToList();
        return new Page<OperationSummaryDto>(items, hasMore, hasMore ? EncodeCursor(hash, TimeCursorPart(rows[^1].CreatedAt), rows[^1].Id.ToString()) : null);
    }

    public async Task<Result<OperationDto>> GetOperationAsync(Actor actor, Guid id, CancellationToken ct)
    {
        if (NeedView(actor) is { } f) return f;
        var scope = await ViewScopeAsync(actor, ct);
        if (await store.GetOperationAsync(id, ct) is not { } op || !(scope.Contains(op.LocationId) || (op.ToLocationId is { } t && scope.Contains(t))))
            return Error.NotFound("operation_not_found", "Operation not found.");
        return Present(actor, op, scope);
    }

    /// <summary>Поля цін за inventory.costs; у scope own - лише ноги в закладах scope.</summary>
    private OperationDto Present(Actor actor, OperationDto op, Scope viewScope)
    {
        var moves = viewScope.All ? op.Movements : [.. op.Movements.Where(m => viewScope.Contains(m.LocationId))];
        if (SeesCosts(actor)) return op with { Movements = moves };
        return op with { TotalCost = null, Movements = [.. moves.Select(m => m with { UnitCost = null })] };
    }
}
