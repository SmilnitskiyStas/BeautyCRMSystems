using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyInventory;

namespace BeautyCrm.Tests.Beauty;

/// <summary>
/// TASK-879 (§32.11 п.5-8, 10): property-style перевірка інваріанта «рівень = Σ партій = Σ рухів» на <see cref="FakeInventoryStore"/>
/// після довільної серії надходжень, списань і переміщень. Генератор детермінований (фіксований seed), час - лише <see cref="FakeClock"/>.
/// Додатково для кожного кроку: помилка (insufficient_stock тощо) не змінює стан; переміщення ніколи не бере прострочену партію;
/// сума ніг переміщення = 0; списання не залишає від'ємних партій.
/// </summary>
public sealed class InventoryInvariantPropertyTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 18);
    private static int _k;
    private static string Key() => "prop-" + Interlocked.Increment(ref _k);

    private static string State(FakeInventoryStore s) =>
        string.Join("|", s.Levels.OrderBy(kv => kv.Key.Item1).ThenBy(kv => kv.Key.Item2).Select(kv => $"{kv.Key.Item1}:{kv.Key.Item2}={kv.Value}"))
        + "#" + string.Join(",", s.Batches.Select(b => $"{b.Id}={b.Qty}")) + "#" + s.Ops.Count + "#" + s.Ops.Sum(o => o.Moves.Count);

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(2026)]
    [InlineData(918273)]
    public async Task Random_operation_series_keeps_level_equal_to_batches_and_movements(int seed)
    {
        var rnd = new Random(seed);
        var store = new FakeInventoryStore();
        var svc = new InventoryService(store, new FakeClock(Noon), new InventoryOptions());
        var actor = Actor.ForTest(Roles.Owner);
        var locs = new[] { store.AddLocation("A").Id, store.AddLocation("B").Id, store.AddLocation("C").Id };
        var prods = new[] { store.AddProduct("P1").Id, store.AddProduct("P2", trackExpiry: true).Id, store.AddProduct("P3").Id };
        string?[] expiries = [null, "2026-11-01", "2026-11-18", "2026-12-01", "2027-03-01", "2030-01-01"]; // дві прострочені, одна «сьогодні»
        int ok = 0, rejected = 0;

        for (var i = 0; i < 160; i++)
        {
            var before = State(store);
            var loc = locs[rnd.Next(locs.Length)];
            var prod = prods[rnd.Next(prods.Length)];
            var qty = rnd.Next(1, 9);
            switch (rnd.Next(3))
            {
                case 0:
                {
                    // надходження: прострочений строк допустимий лише для товару без обліку терміну (§32.7 invalid_expiry)
                    var exp = expiries[rnd.Next(expiries.Length)];
                    var r = await svc.ReceiveAsync(actor, Key(), new ReceiptRequest(loc, null, null, null, null, [new ReceiptLineRequest(prod, qty, null, exp, null)]), default);
                    if (r.IsOk) ok++; else { rejected++; Assert.Equal(before, State(store)); }
                    break;
                }
                case 1:
                {
                    var reason = new[] { "lost", "damaged", "expired", "internal_use" }[rnd.Next(4)];
                    var r = await svc.WriteOffAsync(actor, Key(), new WriteOffRequest(loc, reason, null, [new StockLineRequest(prod, qty, null)]), default);
                    if (r.IsOk) { ok++; Assert.All(r.Value!.Operation.Movements, m => Assert.True(m.Quantity < 0)); }
                    else { rejected++; Assert.Equal("insufficient_stock", r.Error!.Code); Assert.Equal(before, State(store)); }
                    break;
                }
                default:
                {
                    var to = locs.Where(l => l != loc).ElementAt(rnd.Next(locs.Length - 1));
                    var r = await svc.TransferAsync(actor, Key(), new TransferRequest(loc, to, null, [new StockLineRequest(prod, qty, null)]), default);
                    if (r.IsOk)
                    {
                        ok++;
                        var moves = r.Value!.Operation.Movements;
                        Assert.Equal(0m, moves.Sum(m => m.Quantity));
                        // жодна партія-джерело не була простроченою
                        foreach (var m in moves.Where(m => m.Type == "transfer_out"))
                        {
                            var src = store.Batches.Single(b => b.Id == m.BatchId);
                            Assert.True(src.Exp is null || src.Exp >= Today, "transfer took an expired batch");
                        }
                    }
                    else { rejected++; Assert.Equal("insufficient_stock", r.Error!.Code); Assert.Equal(before, State(store)); }
                    break;
                }
            }
            store.AssertInvariants();
        }

        Assert.True(ok > 20, $"seed {seed}: too few successful operations ({ok})");
        Assert.True(rejected >= 1, $"seed {seed}: scenario never hit a rejection");
        // сума ніг кожного переміщення = 0 і ніде немає від'ємних партій
        foreach (var op in store.Ops.Where(o => o.H.Kind == "transfer")) Assert.Equal(0m, op.Moves.Sum(m => m.Qty));
        Assert.All(store.Batches, b => Assert.True(b.Qty >= 0));
    }

    [Fact]
    public async Task Same_seed_gives_same_final_state()
    {
        async Task<string> Run()
        {
            var rnd = new Random(5);
            var store = new FakeInventoryStore();
            var svc = new InventoryService(store, new FakeClock(Noon), new InventoryOptions());
            var a = store.AddLocation("A").Id;
            var p = store.AddProduct("P").Id;
            for (var i = 0; i < 20; i++)
                await svc.ReceiveAsync(Actor.ForTest(Roles.Owner), "same-" + i, new ReceiptRequest(a, null, null, null, null, [new ReceiptLineRequest(p, rnd.Next(1, 9), null, null, null)]), default);
            return string.Join(",", store.Levels.Values);
        }
        Assert.Equal(await Run(), await Run());
    }
}
