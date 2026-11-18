namespace BeautyCrm.Application.Features.BeautyInventory;

/// <summary>Взяття з партії (рух -quantity).</summary>
public sealed record PlannedTake(int Line, Guid ProductId, Guid BatchId, decimal Quantity, decimal? UnitCost);

public sealed record FefoPlan(IReadOnlyList<PlannedTake> Takes, IReadOnlyList<StockShortage> Shortages);

/// <summary>
/// FEFO (§32.4): порядок expires_on ASC NULLS LAST, received_at ASC, id ASC, партії з quantity &gt; 0. Прострочені (expires_on &lt; today)
/// мають найменші дати, тож природно йдуть першими. Для переміщення прострочені пропускаються (<paramref name="skipExpired"/>) і не
/// входять до available. Рядки обробляються послідовно: явна партія й підбір по товару ділять один залишок. Чиста функція, спільна
/// для EF-реалізації (під блокуванням рівнів) і фейку тестів.
/// </summary>
public static class FefoPlanner
{
    public static int Compare(BatchRow a, BatchRow b)
    {
        var an = a.ExpiresOn is null; var bn = b.ExpiresOn is null;
        if (an != bn) return an ? 1 : -1;
        if (!an) { var c = a.ExpiresOn!.Value.CompareTo(b.ExpiresOn!.Value); if (c != 0) return c; }
        var r = a.ReceivedAt.CompareTo(b.ReceivedAt);
        return r != 0 ? r : a.Id.CompareTo(b.Id);
    }

    public static bool IsExpired(DateOnly? expiresOn, DateOnly today) => expiresOn is { } e && e < today;

    public static FefoPlan Allocate(IReadOnlyList<StockLine> lines, IReadOnlyList<BatchRow> batches, bool skipExpired, DateOnly today)
    {
        var remaining = batches.ToDictionary(b => b.Id, b => b.Quantity);
        var byProduct = batches.GroupBy(b => b.ProductId).ToDictionary(g => g.Key, g => g.OrderBy(x => x, Comparer<BatchRow>.Create(Compare)).ToList());
        var takes = new List<PlannedTake>();
        var shortages = new List<StockShortage>();

        foreach (var l in lines)
        {
            if (l.BatchId is { } bid)
            {
                var b = batches.FirstOrDefault(x => x.Id == bid);
                var avail = b is null ? 0m : remaining[bid];
                if (b is null || l.Quantity > avail) { shortages.Add(new(l.Line, l.ProductId, bid, l.Quantity, avail)); continue; }
                remaining[bid] = avail - l.Quantity;
                takes.Add(new(l.Line, l.ProductId, bid, l.Quantity, b.UnitCost));
                continue;
            }

            var list = byProduct.TryGetValue(l.ProductId, out var bs) ? bs : [];
            var usable = list.Where(b => remaining[b.Id] > 0 && !(skipExpired && IsExpired(b.ExpiresOn, today))).ToList();
            var total = usable.Sum(b => remaining[b.Id]);
            if (l.Quantity > total) { shortages.Add(new(l.Line, l.ProductId, null, l.Quantity, total)); continue; }
            var need = l.Quantity;
            foreach (var b in usable)
            {
                if (need <= 0) break;
                var take = Math.Min(need, remaining[b.Id]);
                remaining[b.Id] -= take;
                need -= take;
                takes.Add(new(l.Line, l.ProductId, b.Id, take, b.UnitCost));
            }
        }
        return new FefoPlan(takes, shortages);
    }
}
