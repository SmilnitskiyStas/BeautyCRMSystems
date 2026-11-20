using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyCommon;
using BeautyCrm.Application.Features.BeautyInventory;

namespace BeautyCrm.Tests.Beauty;

/// <summary>
/// TASK-877 (ADR-025, §32.4-§32.7, §32.11 п.5-8, 10, 11, 14): сервіс складу на <see cref="FakeInventoryStore"/>: валідація, права й scope,
/// ідемпотентність, FEFO, переміщення, поля цін. Час лише через <see cref="FakeClock"/> (за замовчуванням 2026-11-18 12:00Z = 14:00 Київ).
/// </summary>
public sealed class InventoryServiceTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 18);

    private sealed class Env
    {
        public readonly FakeInventoryStore Store = new();
        public readonly FakeClock Clock = new(Noon);
        public readonly InventoryService Svc;
        public readonly LocationRow Loc;
        public readonly LocationRow Loc2;
        public Env()
        {
            Svc = new InventoryService(Store, Clock, new InventoryOptions());
            Loc = Store.AddLocation("Main");
            Loc2 = Store.AddLocation("Second");
        }
    }

    private static Actor Owner() => Actor.ForTest(Roles.Owner);
    private static Actor Admin() => Actor.ForTest(Roles.Admin);

    private static Actor AdminWith(params (string Key, string Level)[] overrides)
    {
        var p = PermissionPresets.For(Roles.Admin).ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (var (k, l) in overrides) p[k] = l;
        return Actor.ForTest(Roles.Admin, permissions: p);
    }

    private static Actor Specialist(Guid? specialistId, params (string Key, string Level)[] overrides)
    {
        var p = PermissionPresets.For(Roles.Specialist).ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (var (k, l) in overrides) p[k] = l;
        return Actor.ForTest(Roles.Specialist, specialistId, permissions: p);
    }

    private static ReceiptLineRequest RL(Guid product, decimal qty, decimal? cost = null, string? exp = null, string? lot = null) => new(product, qty, cost, exp, lot);
    private static ReceiptRequest Rcpt(Guid loc, params ReceiptLineRequest[] lines) => new(loc, null, null, null, null, lines);
    private static StockLineRequest SL(Guid product, decimal qty, Guid? batch = null) => new(product, qty, batch);
    private static WriteOffRequest WO(Guid loc, string reason, params StockLineRequest[] lines) => new(loc, reason, null, lines);
    private static TransferRequest Tr(Guid from, Guid to, params StockLineRequest[] lines) => new(from, to, null, lines);

    private static int ErrLine(Result<OperationResult> r) => (int)r.Error!.Extras!["line"]!;

    private static void AssertErr<T>(Result<T> r, string code, ErrorKind? kind = null)
    {
        Assert.False(r.IsOk, "expected error " + code);
        Assert.Equal(code, r.Error!.Code);
        if (kind is { } k) Assert.Equal(k, r.Error.Kind);
    }

    private static int _key;
    private static string Key() => "key-" + Interlocked.Increment(ref _key);

    // ---------- надходження ----------

    [Fact]
    public async Task Receipt_creates_batch_movement_and_level_with_default_cost_from_product()
    {
        var e = new Env();
        var p = e.Store.AddProduct(price: 12.5m);
        var r = await e.Svc.ReceiveAsync(Admin(), Key(), Rcpt(e.Loc.Id, RL(p.Id, 10), RL(p.Id, 5, cost: 20m, exp: "2027-01-01", lot: "L1")), default);

        Assert.True(r.IsOk);
        Assert.False(r.Value!.Replayed);
        Assert.Equal(15, e.Store.Level(e.Loc.Id, p.Id));
        Assert.Equal(2, e.Store.Batches.Count);
        Assert.Equal(12.5m, e.Store.Batches[0].Cost);
        Assert.Equal(20m, e.Store.Batches[1].Cost);
        Assert.Equal(Noon, e.Store.Batches[0].ReceivedAt);
        Assert.All(r.Value.Operation.Movements, m => Assert.Equal("receipt", m.Type));
        Assert.Equal(10 * 12.5m + 5 * 20m, r.Value.Operation.TotalCost);
        e.Store.AssertInvariants();
    }

    [Fact]
    public async Task Receipt_hides_costs_without_inventory_costs_and_forbids_unitCost_field()
    {
        var e = new Env();
        var p = e.Store.AddProduct(price: 10m);
        // admin з revenue.view = none: inventory.costs знижується інваріантом requires до none
        var noCosts = AdminWith(("revenue.view", "none"));
        Assert.Equal("none", noCosts.Access.Level(PermissionKeys.InventoryCosts));

        var forbidden = await e.Svc.ReceiveAsync(noCosts, Key(), Rcpt(e.Loc.Id, RL(p.Id, 1, cost: 5m)), default);
        AssertErr(forbidden, "forbidden_role", ErrorKind.Forbidden);
        Assert.Equal("inventory.costs", forbidden.Error!.Extras!["permission"]);
        Assert.Equal("edit", forbidden.Error.Extras["required"]);

        // 403 раніше за валідацію (від'ємна кількість)
        var before = await e.Svc.ReceiveAsync(noCosts, Key(), Rcpt(e.Loc.Id, RL(p.Id, -1, cost: 5m)), default);
        AssertErr(before, "forbidden_role");

        var ok = await e.Svc.ReceiveAsync(noCosts, Key(), Rcpt(e.Loc.Id, RL(p.Id, 3)), default);
        Assert.True(ok.IsOk);
        Assert.Null(ok.Value!.Operation.TotalCost);
        Assert.All(ok.Value.Operation.Movements, m => Assert.Null(m.UnitCost));
        Assert.Equal(10m, e.Store.Batches.Single().Cost); // партія все одно отримала ціну товару
    }

    [Fact]
    public async Task Receipt_validation_codes()
    {
        var e = new Env();
        var pcs = e.Store.AddProduct("Pcs", "pcs");
        var ml = e.Store.AddProduct("Ml", "ml");
        var exp = e.Store.AddProduct("Exp", "pcs", trackExpiry: true);
        Task<Result<OperationResult>> Go(ReceiptRequest r, string? key = null) => e.Svc.ReceiveAsync(Admin(), key ?? Key(), r, default);

        AssertErr(await Go(new ReceiptRequest(e.Loc.Id, null, null, null, null, null)), "invalid_lines");
        AssertErr(await Go(Rcpt(e.Loc.Id)), "invalid_lines");
        AssertErr(await Go(Rcpt(e.Loc.Id, [.. Enumerable.Range(0, 101).Select(_ => RL(pcs.Id, 1))])), "too_many_lines");
        AssertErr(await Go(Rcpt(e.Loc.Id, RL(pcs.Id, 0))), "invalid_quantity");
        AssertErr(await Go(Rcpt(e.Loc.Id, RL(ml.Id, 1.0005m))), "invalid_quantity");
        AssertErr(await Go(Rcpt(e.Loc.Id, RL(ml.Id, 1_000_000.001m))), "invalid_quantity");
        var frac = await Go(Rcpt(e.Loc.Id, RL(ml.Id, 1), RL(pcs.Id, 1.5m)));
        AssertErr(frac, "invalid_quantity");
        Assert.Equal(1, ErrLine(frac)); // не ціле для pcs, line = індекс рядка
        Assert.True((await Go(Rcpt(e.Loc.Id, RL(ml.Id, 1.5m)))).IsOk); // дробове для ml дозволено
        AssertErr(await Go(Rcpt(e.Loc.Id, RL(pcs.Id, 1, cost: -1m))), "invalid_price");
        AssertErr(await Go(Rcpt(e.Loc.Id, RL(pcs.Id, 1, cost: 1.005m))), "invalid_price");
        AssertErr(await Go(Rcpt(e.Loc.Id, RL(pcs.Id, 1, cost: 1_000_000.01m))), "invalid_price");
        AssertErr(await Go(Rcpt(e.Loc.Id, RL(pcs.Id, 1, exp: "2027-13-01"))), "invalid_date");
        AssertErr(await Go(Rcpt(e.Loc.Id, RL(pcs.Id, 1, exp: "2101-01-01"))), "invalid_date");
        AssertErr(await Go(Rcpt(e.Loc.Id, RL(pcs.Id, 1, lot: new string('x', 65)))), "invalid_lot_number");
        AssertErr(await Go(Rcpt(e.Loc.Id, RL(pcs.Id, 1)) with { DocumentNumber = new string('d', 65) }), "invalid_document_number");
        AssertErr(await Go(Rcpt(e.Loc.Id, RL(pcs.Id, 1)) with { Note = new string('n', 501) }), "invalid_note");
        AssertErr(await Go(Rcpt(e.Loc.Id, RL(pcs.Id, 1)) with { ReceivedOn = "18.11.2026" }), "invalid_date");
        AssertErr(await Go(Rcpt(e.Loc.Id, RL(pcs.Id, 1)) with { ReceivedOn = "2026-11-19" }), "invalid_date"); // у майбутньому
        AssertErr(await Go(Rcpt(e.Loc.Id, RL(pcs.Id, 1)) with { ReceivedOn = "2025-11-17" }), "invalid_date"); // старше 365 днів
        Assert.True((await Go(Rcpt(e.Loc.Id, RL(pcs.Id, 1)) with { ReceivedOn = "2025-11-18" })).IsOk);
        var need = await Go(Rcpt(e.Loc.Id, RL(pcs.Id, 1), RL(exp.Id, 1)));
        AssertErr(need, "expiry_required");
        Assert.Equal(1, ErrLine(need));
        AssertErr(await Go(Rcpt(e.Loc.Id, RL(exp.Id, 1, exp: "2026-11-17"))), "invalid_expiry");
        Assert.True((await Go(Rcpt(e.Loc.Id, RL(exp.Id, 1, exp: "2026-11-18")))).IsOk); // сьогодні ще не прострочено
        AssertErr(await Go(Rcpt(Guid.NewGuid(), RL(pcs.Id, 1))), "location_not_found", ErrorKind.NotFound);
        AssertErr(await Go(Rcpt(e.Loc.Id, RL(Guid.NewGuid(), 1))), "product_not_found", ErrorKind.NotFound);
        AssertErr(await Go(Rcpt(e.Loc.Id, RL(pcs.Id, 1)) with { SupplierId = Guid.NewGuid() }), "supplier_not_found", ErrorKind.NotFound);
    }

    [Fact]
    public async Task Receipt_activity_rules()
    {
        var e = new Env();
        var inactive = e.Store.AddProduct("Old", active: false);
        var disabled = e.Store.AddProduct("Off");
        e.Store.Disabled.Add((e.Loc.Id, disabled.Id));
        var okp = e.Store.AddProduct("Ok");
        var closed = e.Store.AddLocation("Closed", active: false);
        var supplier = e.Store.AddSupplier("Gone", active: false);

        var r1 = await e.Svc.ReceiveAsync(Admin(), Key(), Rcpt(e.Loc.Id, RL(okp.Id, 1), RL(inactive.Id, 1)), default);
        AssertErr(r1, "product_inactive", ErrorKind.Validation);
        Assert.Equal(1, ErrLine(r1));
        AssertErr(await e.Svc.ReceiveAsync(Admin(), Key(), Rcpt(e.Loc.Id, RL(disabled.Id, 1)), default), "product_disabled_for_location");
        AssertErr(await e.Svc.ReceiveAsync(Admin(), Key(), Rcpt(closed.Id, RL(okp.Id, 1)), default), "location_inactive");
        AssertErr(await e.Svc.ReceiveAsync(Admin(), Key(), Rcpt(e.Loc.Id, RL(okp.Id, 1)) with { SupplierId = supplier.Id }, default), "supplier_inactive");
        Assert.Empty(e.Store.Ops);
    }

    // ---------- ідемпотентність ----------

    [Fact]
    public async Task Idempotency_key_required_format_replay_and_reuse()
    {
        var e = new Env();
        var p = e.Store.AddProduct();
        var body = Rcpt(e.Loc.Id, RL(p.Id, 4));

        AssertErr(await e.Svc.ReceiveAsync(Admin(), null, body, default), "idempotency_key_required");
        AssertErr(await e.Svc.ReceiveAsync(Admin(), "", body, default), "idempotency_key_required");
        AssertErr(await e.Svc.ReceiveAsync(Admin(), new string('k', 129), body, default), "invalid_idempotency_key");
        AssertErr(await e.Svc.ReceiveAsync(Admin(), "ключ", body, default), "invalid_idempotency_key");
        AssertErr(await e.Svc.ReceiveAsync(Admin(), "has space", body, default), "invalid_idempotency_key");
        Assert.Empty(e.Store.Ops);

        var first = await e.Svc.ReceiveAsync(Admin(), "k1", body, default);
        var again = await e.Svc.ReceiveAsync(Admin(), "k1", body, default);
        Assert.False(first.Value!.Replayed);
        Assert.True(again.Value!.Replayed);
        Assert.Equal(first.Value.Operation.Id, again.Value.Operation.Id);
        Assert.Equal(4, e.Store.Level(e.Loc.Id, p.Id)); // повтор не дублює надходження

        AssertErr(await e.Svc.ReceiveAsync(Admin(), "k1", Rcpt(e.Loc.Id, RL(p.Id, 5)), default), "idempotency_key_reused");
        // той самий ключ для іншого виду операції теж інше тіло
        AssertErr(await e.Svc.WriteOffAsync(Admin(), "k1", WO(e.Loc.Id, "lost", SL(p.Id, 1)), default), "idempotency_key_reused");
        Assert.Equal(4, e.Store.Level(e.Loc.Id, p.Id));
    }

    [Fact]
    public async Task Parallel_replay_conflict_is_resolved_to_the_stored_operation()
    {
        var e = new Env();
        var p = e.Store.AddProduct();
        var body = Rcpt(e.Loc.Id, RL(p.Id, 2));
        var winner = await e.Svc.ReceiveAsync(Admin(), "race", body, default);
        // імітація: перевірка «ключ уже є» не побачила операції (гонка), а вставка заголовка програла -> DuplicateKey -> повтор
        var calls = 0;
        e.Store.BeforeKeyCheck = () => calls++;
        var store = new HidingStore(e.Store);
        var svc = new InventoryService(store, e.Clock, new InventoryOptions());
        var loser = await svc.ReceiveAsync(Admin(), "race", body, default);
        Assert.True(loser.IsOk);
        Assert.True(loser.Value!.Replayed);
        Assert.Equal(winner.Value!.Operation.Id, loser.Value.Operation.Id);
        Assert.Equal(2, e.Store.Level(e.Loc.Id, p.Id));
    }

    /// <summary>Перший пошук ключа повертає null (операція «ще не видна»), далі - як є.</summary>
    private sealed class HidingStore(FakeInventoryStore inner) : IInventoryStoreProxy(inner)
    {
        private bool _hidden;
        public override Task<KeyedOperation?> FindOperationByKeyAsync(string keyHash, CancellationToken ct)
        {
            if (_hidden) return base.FindOperationByKeyAsync(keyHash, ct);
            _hidden = true;
            return Task.FromResult<KeyedOperation?>(null);
        }
    }

    // ---------- списання, FEFO ----------

    [Fact]
    public async Task WriteOff_takes_expired_first_then_fefo_nulls_last_then_received_at()
    {
        var e = new Env();
        var p = e.Store.AddProduct();
        var t0 = Noon.AddDays(-10);
        var noExp = e.Store.AddBatch(e.Loc.Id, p.Id, 5, null, t0.AddDays(-5));         // NULLS LAST
        var late = e.Store.AddBatch(e.Loc.Id, p.Id, 5, new DateOnly(2027, 6, 1), t0);
        var soonNew = e.Store.AddBatch(e.Loc.Id, p.Id, 5, new DateOnly(2026, 12, 1), t0.AddDays(2));
        var soonOld = e.Store.AddBatch(e.Loc.Id, p.Id, 5, new DateOnly(2026, 12, 1), t0);   // тайбрейк received_at
        var expired = e.Store.AddBatch(e.Loc.Id, p.Id, 5, new DateOnly(2026, 11, 1), t0.AddDays(3)); // прострочена - першою

        var r = await e.Svc.WriteOffAsync(Admin(), Key(), WO(e.Loc.Id, "damaged", SL(p.Id, 18)), default);
        Assert.True(r.IsOk);
        Assert.Equal([expired.Id, soonOld.Id, soonNew.Id, late.Id],
            r.Value!.Operation.Movements.Select(m => m.BatchId!.Value).ToArray());
        Assert.Equal([-5m, -5m, -5m, -3m], r.Value.Operation.Movements.Select(m => m.Quantity).ToArray());
        Assert.Equal(2, late.Qty);
        Assert.Equal(5, noExp.Qty); // без терміну - останньою
        Assert.Equal(7, e.Store.Level(e.Loc.Id, p.Id));
        Assert.All(r.Value.Operation.Movements, m => Assert.Equal("write_off", m.Type));
        e.Store.AssertInvariants();
    }

    [Fact]
    public async Task WriteOff_explicit_batch_and_batch_idor()
    {
        var e = new Env();
        var p = e.Store.AddProduct();
        var other = e.Store.AddProduct("Other");
        var b1 = e.Store.AddBatch(e.Loc.Id, p.Id, 5, new DateOnly(2026, 12, 1));
        var b2 = e.Store.AddBatch(e.Loc.Id, p.Id, 5, new DateOnly(2027, 1, 1));
        var foreign = e.Store.AddBatch(e.Loc2.Id, p.Id, 5);
        var otherProduct = e.Store.AddBatch(e.Loc.Id, other.Id, 5);

        var r = await e.Svc.WriteOffAsync(Admin(), Key(), WO(e.Loc.Id, "lost", SL(p.Id, 3, b2.Id)), default);
        Assert.True(r.IsOk);
        Assert.Equal(2, b2.Qty);
        Assert.Equal(5, b1.Qty);

        var bad = await e.Svc.WriteOffAsync(Admin(), Key(), WO(e.Loc.Id, "lost", SL(p.Id, 1), SL(p.Id, 1, foreign.Id)), default);
        AssertErr(bad, "batch_not_found", ErrorKind.NotFound);
        Assert.Equal(1, ErrLine(bad));
        AssertErr(await e.Svc.WriteOffAsync(Admin(), Key(), WO(e.Loc.Id, "lost", SL(p.Id, 1, otherProduct.Id)), default), "batch_not_found");
        AssertErr(await e.Svc.WriteOffAsync(Admin(), Key(), WO(e.Loc.Id, "lost", SL(p.Id, 1, Guid.NewGuid())), default), "batch_not_found");
        Assert.Equal(5, foreign.Qty);
        e.Store.AssertInvariants();
    }

    [Fact]
    public async Task WriteOff_insufficient_stock_reports_available_and_changes_nothing()
    {
        var e = new Env();
        var a = e.Store.AddProduct("A");
        var b = e.Store.AddProduct("B");
        e.Store.AddBatch(e.Loc.Id, a.Id, 10);
        e.Store.AddBatch(e.Loc.Id, b.Id, 2);

        var r = await e.Svc.WriteOffAsync(Admin(), Key(), WO(e.Loc.Id, "lost", SL(a.Id, 4), SL(b.Id, 3)), default);
        AssertErr(r, "insufficient_stock", ErrorKind.Validation);
        var lines = (System.Collections.IEnumerable)r.Error!.Extras!["lines"]!;
        var only = lines.Cast<object>().Single();
        Assert.Equal(1, (int)only.GetType().GetProperty("line")!.GetValue(only)!);
        Assert.Equal(3m, (decimal)only.GetType().GetProperty("requested")!.GetValue(only)!);
        Assert.Equal(2m, (decimal)only.GetType().GetProperty("available")!.GetValue(only)!);
        Assert.Equal(10, e.Store.Level(e.Loc.Id, a.Id)); // перший рядок теж не застосовано (атомарно)
        Assert.Equal(2, e.Store.Level(e.Loc.Id, b.Id));
        Assert.Empty(e.Store.Ops);
        e.Store.AssertInvariants();
    }

    [Fact]
    public async Task WriteOff_validation_codes()
    {
        var e = new Env();
        var p = e.Store.AddProduct();
        e.Store.AddBatch(e.Loc.Id, p.Id, 50);
        Task<Result<OperationResult>> Go(WriteOffRequest r) => e.Svc.WriteOffAsync(Admin(), Key(), r, default);

        AssertErr(await Go(WO(e.Loc.Id, "nonsense", SL(p.Id, 1))), "invalid_reason");
        AssertErr(await Go(new WriteOffRequest(e.Loc.Id, null, null, [SL(p.Id, 1)])), "invalid_reason");
        AssertErr(await Go(WO(e.Loc.Id, "correction", SL(p.Id, 1))), "note_required");
        AssertErr(await Go(WO(e.Loc.Id, "other", SL(p.Id, 1)) with { Note = "  " }), "note_required");
        Assert.True((await Go(WO(e.Loc.Id, "other", SL(p.Id, 1)) with { Note = "why" })).IsOk);
        AssertErr(await Go(WO(e.Loc.Id, "lost", SL(p.Id, 1)) with { Note = new string('n', 501) }), "invalid_note");
        AssertErr(await Go(WO(e.Loc.Id, "lost")), "invalid_lines");
        AssertErr(await Go(WO(e.Loc.Id, "lost", SL(p.Id, 1), SL(p.Id, 2))), "duplicate_line");
        AssertErr(await Go(WO(e.Loc.Id, "lost", SL(p.Id, -1))), "invalid_quantity");
        AssertErr(await Go(WO(e.Loc.Id, "lost", SL(p.Id, 0.5m))), "invalid_quantity"); // pcs - лише цілі
        AssertErr(await Go(WO(e.Loc.Id, "lost", [.. Enumerable.Range(0, 101).Select(_ => SL(p.Id, 1, Guid.NewGuid()))])), "too_many_lines");
        AssertErr(await Go(WO(Guid.NewGuid(), "lost", SL(p.Id, 1))), "location_not_found");
        AssertErr(await Go(WO(e.Loc.Id, "lost", SL(Guid.NewGuid(), 1))), "product_not_found");
        // списання дозволене для неактивного товару й закладу
        var old = e.Store.AddProduct("Old", active: false);
        e.Store.AddBatch(e.Loc.Id, old.Id, 3);
        Assert.True((await Go(WO(e.Loc.Id, "expired", SL(old.Id, 3)))).IsOk);
    }

    // ---------- межа «прострочено» на локальній півночі закладу ----------

    [Theory]
    [InlineData("2026-11-18T21:59:00Z", false)] // 23:59 за Києвом 18.11: партія зі строком 18.11 ще не прострочена (UTC-дата та сама)
    [InlineData("2026-11-18T22:00:00Z", true)]  // 00:00 19.11 за Києвом: прострочена, хоча UTC-дата ще 18.11
    [InlineData("2026-11-18T22:01:00Z", true)]
    public async Task Expiry_boundary_is_local_midnight_of_the_location(string now, bool expired)
    {
        var e = new Env();
        e.Clock.Now = DateTimeOffset.Parse(now);
        var p = e.Store.AddProduct();
        var near = e.Store.AddBatch(e.Loc.Id, p.Id, 5, new DateOnly(2026, 11, 18), Noon.AddDays(-9));
        var far = e.Store.AddBatch(e.Loc.Id, p.Id, 5, new DateOnly(2027, 5, 1), Noon.AddDays(-9));
        var r = await e.Svc.TransferAsync(Admin(), Key(), Tr(e.Loc.Id, e.Loc2.Id, SL(p.Id, 3)), default);
        Assert.True(r.IsOk);
        // переміщення бере непрострочену: якщо near прострочена - беремо far
        Assert.Equal(expired ? far.Id : near.Id, r.Value!.Operation.Movements.First(m => m.Type == "transfer_out").BatchId);
    }

    [Fact]
    public async Task Receipt_expiry_check_uses_the_location_time_zone()
    {
        var e = new Env();
        var p = e.Store.AddProduct("P", trackExpiry: true);
        e.Clock.Now = DateTimeOffset.Parse("2026-11-18T22:30:00Z"); // у Києві вже 19.11
        AssertErr(await e.Svc.ReceiveAsync(Admin(), Key(), Rcpt(e.Loc.Id, RL(p.Id, 1, exp: "2026-11-18")), default), "invalid_expiry");
        Assert.True((await e.Svc.ReceiveAsync(Admin(), Key(), Rcpt(e.Loc.Id, RL(p.Id, 1, exp: "2026-11-19")), default)).IsOk);
        var la = e.Store.AddLocation("LA", "America/Los_Angeles"); // там ще 18.11 (14:30)
        Assert.True((await e.Svc.ReceiveAsync(Admin(), Key(), Rcpt(la.Id, RL(p.Id, 1, exp: "2026-11-18")), default)).IsOk);
    }

    // ---------- переміщення ----------

    [Fact]
    public async Task Transfer_skips_expired_preserves_batch_attributes_and_sums_to_zero()
    {
        var e = new Env();
        var p = e.Store.AddProduct();
        var received = Noon.AddDays(-20);
        var expired = e.Store.AddBatch(e.Loc.Id, p.Id, 5, new DateOnly(2026, 11, 1), received, 1m);
        var a = e.Store.AddBatch(e.Loc.Id, p.Id, 4, new DateOnly(2026, 12, 1), received.AddDays(1), 2.5m);
        a.Lot = "LOT-A";
        var b = e.Store.AddBatch(e.Loc.Id, p.Id, 4, new DateOnly(2027, 3, 1), received.AddDays(2), 3m);

        var r = await e.Svc.TransferAsync(Admin(), Key(), Tr(e.Loc.Id, e.Loc2.Id, SL(p.Id, 6)), default);
        Assert.True(r.IsOk);
        var op = r.Value!.Operation;
        Assert.Equal("transfer", op.Kind);
        Assert.Equal(e.Loc2.Id, op.ToLocationId);
        Assert.Equal(0m, op.Movements.Sum(m => m.Quantity)); // сума ніг = 0
        Assert.Equal(5, expired.Qty); // прострочена не чіпається
        Assert.Equal(0, a.Qty);
        Assert.Equal(2, b.Qty);
        var moved = e.Store.Batches.Where(x => x.Loc == e.Loc2.Id).OrderBy(x => x.Exp).ToList();
        Assert.Equal(2, moved.Count);
        Assert.Equal([a.Exp, b.Exp], moved.Select(x => x.Exp).ToArray());
        Assert.Equal([2.5m, 3m], moved.Select(x => x.Cost).ToArray());
        Assert.Equal([received.AddDays(1), received.AddDays(2)], moved.Select(x => x.ReceivedAt).ToArray());
        Assert.Equal([a.Id, b.Id], moved.Select(x => x.SourceId!.Value).ToArray());
        Assert.Equal("LOT-A", moved[0].Lot);
        Assert.Equal([4m, 2m], moved.Select(x => x.Initial).ToArray());
        Assert.Equal(7, e.Store.Level(e.Loc.Id, p.Id));
        Assert.Equal(6, e.Store.Level(e.Loc2.Id, p.Id));
        Assert.Equal(4 * 2.5m + 2 * 3m, op.TotalCost); // лише нога transfer_out
        e.Store.AssertInvariants();
    }

    [Fact]
    public async Task Transfer_available_excludes_expired_and_insufficient_is_atomic()
    {
        var e = new Env();
        var p = e.Store.AddProduct();
        e.Store.AddBatch(e.Loc.Id, p.Id, 5, new DateOnly(2026, 11, 1));
        e.Store.AddBatch(e.Loc.Id, p.Id, 2, new DateOnly(2027, 1, 1));
        var r = await e.Svc.TransferAsync(Admin(), Key(), Tr(e.Loc.Id, e.Loc2.Id, SL(p.Id, 3)), default);
        AssertErr(r, "insufficient_stock");
        var only = ((System.Collections.IEnumerable)r.Error!.Extras!["lines"]!).Cast<object>().Single();
        Assert.Equal(2m, (decimal)only.GetType().GetProperty("available")!.GetValue(only)!); // без простроченої
        Assert.Equal(0, e.Store.Level(e.Loc2.Id, p.Id));
        Assert.Equal(7, e.Store.Level(e.Loc.Id, p.Id));
        Assert.Empty(e.Store.Ops);
    }

    [Fact]
    public async Task Transfer_validation_and_activity()
    {
        var e = new Env();
        var p = e.Store.AddProduct();
        var expired = e.Store.AddBatch(e.Loc.Id, p.Id, 5, new DateOnly(2026, 11, 1));
        e.Store.AddBatch(e.Loc.Id, p.Id, 5, new DateOnly(2027, 1, 1));
        var closed = e.Store.AddLocation("Closed", active: false);

        AssertErr(await e.Svc.TransferAsync(Admin(), Key(), Tr(e.Loc.Id, e.Loc.Id, SL(p.Id, 1)), default), "same_location");
        var be = await e.Svc.TransferAsync(Admin(), Key(), Tr(e.Loc.Id, e.Loc2.Id, SL(p.Id, 1, expired.Id)), default);
        AssertErr(be, "batch_expired");
        Assert.Equal(0, ErrLine(be));
        AssertErr(await e.Svc.TransferAsync(Admin(), Key(), Tr(e.Loc.Id, closed.Id, SL(p.Id, 1)), default), "location_inactive");
        e.Store.Disabled.Add((e.Loc2.Id, p.Id));
        AssertErr(await e.Svc.TransferAsync(Admin(), Key(), Tr(e.Loc.Id, e.Loc2.Id, SL(p.Id, 1)), default), "product_disabled_for_location");
        AssertErr(await e.Svc.TransferAsync(Admin(), Key(), Tr(Guid.NewGuid(), e.Loc2.Id, SL(p.Id, 1)), default), "location_not_found");
        AssertErr(await e.Svc.TransferAsync(Admin(), Key(), Tr(e.Loc.Id, Guid.NewGuid(), SL(p.Id, 1)), default), "location_not_found");
        // неактивний заклад-ДЖЕРЕЛО й неактивний товар дозволені (вивезення залишків)
        var old = e.Store.AddProduct("Old", active: false);
        e.Store.AddBatch(closed.Id, old.Id, 2);
        Assert.True((await e.Svc.TransferAsync(Admin(), Key(), Tr(closed.Id, e.Loc.Id, SL(old.Id, 2)), default)).IsOk);
        Assert.Empty(e.Store.Ops.Where(o => o.H.ToLocationId == e.Loc2.Id));
    }

    // ---------- права й scope ----------

    [Fact]
    public async Task Specialist_own_scope_sees_only_own_locations_and_cannot_manage()
    {
        var e = new Env();
        var sid = Guid.NewGuid();
        e.Store.SpecLocations[sid] = [e.Loc.Id];
        var p = e.Store.AddProduct();
        e.Store.AddBatch(e.Loc.Id, p.Id, 5);
        var spec = Specialist(sid);

        var scope = await e.Svc.GetScopeAsync(spec, default);
        Assert.Equal([e.Loc.Id], scope.Value!.Locations.Select(l => l.Id).ToArray());
        Assert.False(scope.Value.Locations.Single().CanManage); // manage = none
        Assert.False(scope.Value.CanEditCatalog);
        Assert.Equal("none", scope.Value.Costs);

        Assert.True((await e.Svc.GetStockAsync(spec, new StockListQuery(e.Loc.Id, null, null, null, null, null, null, null), default)).IsOk);
        AssertErr(await e.Svc.GetStockAsync(spec, new StockListQuery(e.Loc2.Id, null, null, null, null, null, null, null), default), "location_not_found", ErrorKind.NotFound);
        AssertErr(await e.Svc.GetStockAsync(spec, new StockListQuery(null, null, null, null, null, null, null, null), default), "location_required");

        var denied = await e.Svc.ReceiveAsync(spec, Key(), Rcpt(e.Loc.Id, RL(p.Id, 1)), default);
        AssertErr(denied, "forbidden_role", ErrorKind.Forbidden);
        Assert.Equal("inventory.manage", denied.Error!.Extras!["permission"]);
        Assert.Equal("own", denied.Error.Extras["required"]);
        AssertErr(await e.Svc.CreateProductAsync(spec, new ProductRequest("X", null, "pcs", null, "both", null, null, null, null, null, null, null), default), "forbidden_role");
        AssertErr(await e.Svc.ListSuppliersAsync(spec, new SupplierListQuery(null, null, null, null), default), "forbidden_role");
    }

    [Fact]
    public async Task Specialist_without_profile_or_inactive_profile_sees_nothing()
    {
        var e = new Env();
        var p = e.Store.AddProduct();
        e.Store.AddBatch(e.Loc.Id, p.Id, 5);
        foreach (var spec in new[] { Specialist(null), Specialist(Guid.NewGuid()) })
        {
            var scope = await e.Svc.GetScopeAsync(spec, default);
            Assert.Empty(scope.Value!.Locations);
            AssertErr(await e.Svc.GetStockAsync(spec, new StockListQuery(e.Loc.Id, null, null, null, null, null, null, null), default), "location_not_found");
        }
    }

    [Fact]
    public async Task Manage_own_limits_operations_to_own_locations_and_both_transfer_legs()
    {
        var e = new Env();
        var sid = Guid.NewGuid();
        e.Store.SpecLocations[sid] = [e.Loc.Id];
        var p = e.Store.AddProduct();
        e.Store.AddBatch(e.Loc.Id, p.Id, 5);
        e.Store.AddBatch(e.Loc2.Id, p.Id, 5);
        var spec = Specialist(sid, ("inventory.manage", "own"));

        Assert.True((await e.Svc.ReceiveAsync(spec, Key(), Rcpt(e.Loc.Id, RL(p.Id, 1)), default)).IsOk);
        AssertErr(await e.Svc.ReceiveAsync(spec, Key(), Rcpt(e.Loc2.Id, RL(p.Id, 1)), default), "location_not_found", ErrorKind.NotFound);
        AssertErr(await e.Svc.WriteOffAsync(spec, Key(), WO(e.Loc2.Id, "lost", SL(p.Id, 1)), default), "location_not_found");
        AssertErr(await e.Svc.TransferAsync(spec, Key(), Tr(e.Loc.Id, e.Loc2.Id, SL(p.Id, 1)), default), "location_not_found");
        AssertErr(await e.Svc.TransferAsync(spec, Key(), Tr(e.Loc2.Id, e.Loc.Id, SL(p.Id, 1)), default), "location_not_found");
        Assert.Equal(5, e.Store.Level(e.Loc2.Id, p.Id));
        // каталог товарів: лише manage = all
        AssertErr(await e.Svc.CreateProductAsync(spec, new ProductRequest("X", null, "pcs", null, "both", null, null, null, null, null, null, null), default), "forbidden_role");
    }

    [Fact]
    public async Task Operation_of_another_location_is_not_found_and_replay_outside_scope_is_rejected()
    {
        var e = new Env();
        var sid = Guid.NewGuid();
        e.Store.SpecLocations[sid] = [e.Loc.Id];
        var p = e.Store.AddProduct();
        var admin = await e.Svc.ReceiveAsync(Admin(), "same", Rcpt(e.Loc2.Id, RL(p.Id, 2)), default);
        var spec = Specialist(sid, ("inventory.manage", "own"));
        AssertErr(await e.Svc.GetOperationAsync(spec, admin.Value!.Operation.Id, default), "operation_not_found", ErrorKind.NotFound);
        // чужий ключ не розкриває операцію іншого закладу
        AssertErr(await e.Svc.ReceiveAsync(spec, "same", Rcpt(e.Loc2.Id, RL(p.Id, 2)), default), "idempotency_key_reused");
        Assert.True((await e.Svc.GetOperationAsync(Admin(), admin.Value.Operation.Id, default)).IsOk);
    }

    [Fact]
    public async Task Transfer_operation_is_visible_through_either_leg_but_only_in_scope_legs()
    {
        var e = new Env();
        var sid = Guid.NewGuid();
        e.Store.SpecLocations[sid] = [e.Loc2.Id];
        var p = e.Store.AddProduct();
        e.Store.AddBatch(e.Loc.Id, p.Id, 5);
        var tr = await e.Svc.TransferAsync(Admin(), Key(), Tr(e.Loc.Id, e.Loc2.Id, SL(p.Id, 2)), default);
        var seen = await e.Svc.GetOperationAsync(Specialist(sid), tr.Value!.Operation.Id, default);
        Assert.True(seen.IsOk);
        Assert.All(seen.Value!.Movements, m => Assert.Equal(e.Loc2.Id, m.LocationId)); // нога джерела прихована
        Assert.Single(seen.Value.Movements);
    }

    // ---------- ціни у відповідях ----------

    [Fact]
    public async Task Cost_fields_are_stripped_from_stock_and_products_without_costs()
    {
        var e = new Env();
        var p = e.Store.AddProduct(price: 9m);
        e.Store.AddBatch(e.Loc.Id, p.Id, 2, cost: 9m);
        var noCosts = AdminWith(("revenue.view", "none"));
        var stock = await e.Svc.GetStockAsync(noCosts, new StockListQuery(e.Loc.Id, null, null, null, null, null, null, null), default);
        Assert.Null(stock.Value!.Items.Single().Value);
        Assert.Null(stock.Value.Items.Single().ValueIncomplete);
        var withCosts = await e.Svc.GetStockAsync(Admin(), new StockListQuery(e.Loc.Id, null, null, null, null, null, null, null), default);
        Assert.Equal(18m, withCosts.Value!.Items.Single().Value);
        Assert.False(withCosts.Value.Items.Single().ValueIncomplete);
        Assert.Null((await e.Svc.GetProductAsync(noCosts, p.Id, default)).Value!.PurchasePrice);
        Assert.Equal(9m, (await e.Svc.GetProductAsync(Admin(), p.Id, default)).Value!.PurchasePrice);
        // JSON: поле відсутнє, а не 0
        var json = System.Text.Json.JsonSerializer.Serialize((await e.Svc.GetProductAsync(noCosts, p.Id, default)).Value!, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.DoesNotContain("purchasePrice", json);
    }

    [Fact]
    public async Task Stock_state_filters_and_low_flag_use_effective_min_stock()
    {
        var e = new Env();
        var low = e.Store.AddProduct("Low");
        var fine = e.Store.AddProduct("Fine");
        var none = e.Store.AddProduct("None");
        e.Store.Products[low.Id] = low with { MinStock = 5 };
        e.Store.Products[fine.Id] = fine with { MinStock = 2 };
        e.Store.AddBatch(e.Loc.Id, low.Id, 3);
        e.Store.AddBatch(e.Loc.Id, fine.Id, 3);
        async Task<string[]> Names(string state) =>
            [.. (await e.Svc.GetStockAsync(Admin(), new StockListQuery(e.Loc.Id, null, null, null, state, null, null, null), default)).Value!.Items.Select(i => i.Product.Name)];
        Assert.Equal(["Fine", "Low", "None"], await Names("all"));
        Assert.Equal(["Low"], await Names("low"));
        Assert.Equal(["Fine", "Low"], await Names("in_stock"));
        Assert.Equal(["None"], await Names("out_of_stock"));
    }

    // ---------- каталог ----------

    private static ProductRequest PR(string? name = "Gel", string? unit = "pcs", string? purpose = "sale") =>
        new(name, null, unit, null, purpose, null, null, purpose == "salon_use" ? null : 10m, null, null, null, null);

    [Fact]
    public async Task Product_validation_codes()
    {
        var e = new Env();
        Task<Result<ProductDetailDto>> Go(ProductRequest r) => e.Svc.CreateProductAsync(Admin(), r, default);

        AssertErr(await Go(PR(name: "  ")), "invalid_name");
        AssertErr(await Go(PR(name: new string('n', 201))), "invalid_name");
        AssertErr(await Go(PR() with { Category = new string('c', 101) }), "invalid_category");
        AssertErr(await Go(PR(unit: "bottle")), "invalid_unit");
        AssertErr(await Go(PR(unit: null)), "invalid_unit");
        AssertErr(await Go(PR() with { Barcode = "bad code!" }), "invalid_barcode");
        AssertErr(await Go(PR() with { Barcode = new string('1', 65) }), "invalid_barcode");
        AssertErr(await Go(PR(purpose: "gift")), "invalid_purpose");
        AssertErr(await Go(PR() with { PurchasePrice = -1 }), "invalid_price");
        AssertErr(await Go(PR() with { PurchasePrice = 1.234m }), "invalid_price");
        AssertErr(await Go(PR() with { MinStock = -1 }), "invalid_min_stock");
        AssertErr(await Go(PR() with { MinStock = 1.5m }), "invalid_min_stock"); // pcs - цілий
        AssertErr(await Go(PR(unit: "ml") with { MinStock = 0.0001m }), "invalid_min_stock");
        AssertErr(await Go(PR() with { Note = new string('n', 501) }), "invalid_note");
        AssertErr(await Go(PR() with { SupplierId = Guid.NewGuid() }), "supplier_not_found", ErrorKind.NotFound);

        var created = await Go(PR(unit: "ml") with { Category = " Care ", Barcode = " 4820.A-1 ", MinStock = 1.5m, TrackExpiry = true });
        Assert.True(created.IsOk);
        Assert.Equal("Care", created.Value!.Category); // trim
        Assert.Equal("4820.A-1", created.Value.Barcode);
        Assert.True(created.Value.TrackExpiry);
        Assert.True(created.Value.IsActive);
    }

    [Fact]
    public async Task Barcode_unique_and_by_barcode_lookup()
    {
        var e = new Env();
        var first = await e.Svc.CreateProductAsync(Admin(), PR() with { Barcode = "ABC123" }, default);
        AssertErr(await e.Svc.CreateProductAsync(Admin(), PR("Other") with { Barcode = "ABC123" }, default), "barcode_taken", ErrorKind.Conflict);
        var second = await e.Svc.CreateProductAsync(Admin(), PR("Other"), default);
        AssertErr(await e.Svc.UpdateProductAsync(Admin(), second.Value!.Id, PR("Other") with { Barcode = "ABC123" }, default), "barcode_taken");
        Assert.True((await e.Svc.UpdateProductAsync(Admin(), first.Value!.Id, PR() with { Barcode = "ABC123" }, default)).IsOk); // власний штрихкод
        Assert.Equal(first.Value.Id, (await e.Svc.GetProductByBarcodeAsync(Admin(), "  ABC123 ", default)).Value!.Id);
        AssertErr(await e.Svc.GetProductByBarcodeAsync(Admin(), "nope", default), "product_not_found", ErrorKind.NotFound);
        AssertErr(await e.Svc.GetProductByBarcodeAsync(Admin(), null, default), "product_not_found");
    }

    [Fact]
    public async Task Unit_is_locked_once_the_product_has_movements_and_put_semantics()
    {
        var e = new Env();
        var created = await e.Svc.CreateProductAsync(Admin(), PR() with { PurchasePrice = 7m, Category = "A", Note = "n" }, default);
        var id = created.Value!.Id;
        // PUT: purchasePrice null = без змін; решта nullable null = очистити; trackExpiry/isActive null = без змін
        var put = await e.Svc.UpdateProductAsync(Admin(), id, PR(unit: "pcs"), default);
        Assert.Equal(7m, e.Store.Products[id].PurchasePrice);
        Assert.Null(put.Value!.Category);
        Assert.Null(put.Value.Note);
        Assert.True(put.Value.IsActive);

        Assert.True((await e.Svc.UpdateProductAsync(Admin(), id, PR(unit: "ml"), default)).IsOk); // рухів ще немає
        await e.Svc.ReceiveAsync(Admin(), Key(), Rcpt(e.Loc.Id, RL(id, 2)), default);
        AssertErr(await e.Svc.UpdateProductAsync(Admin(), id, PR(unit: "kg"), default), "unit_locked");
        Assert.True((await e.Svc.UpdateProductAsync(Admin(), id, PR(unit: "ml") with { IsActive = false }, default)).IsOk);
        Assert.False(e.Store.Products[id].IsActive);
        AssertErr(await e.Svc.UpdateProductAsync(Admin(), Guid.NewGuid(), PR(), default), "product_not_found", ErrorKind.NotFound);
    }

    [Fact]
    public async Task Sale_price_is_required_for_sale_and_both_and_nulled_for_salon_use()
    {
        var e = new Env();
        Task<Result<ProductDetailDto>> Create(ProductRequest r) => e.Svc.CreateProductAsync(Admin(), r, default);
        AssertErr(await Create(PR() with { SalePrice = null }), "sale_price_required");
        AssertErr(await Create(PR(purpose: "both") with { SalePrice = null }), "sale_price_required");
        AssertErr(await Create(PR() with { SalePrice = -1m }), "invalid_price");
        AssertErr(await Create(PR() with { SalePrice = 1.234m }), "invalid_price");
        AssertErr(await Create(PR() with { SalePrice = 1000001m }), "invalid_price");
        var ok = await Create(PR() with { SalePrice = 149.90m });
        Assert.Equal(149.90m, ok.Value!.SalePrice);
        Assert.Equal(0m, (await Create(PR() with { SalePrice = 0m })).Value!.SalePrice);
        // salon_use: ціна ігнорується й зберігається NULL
        var salon = await Create(PR(purpose: "salon_use") with { SalePrice = 50m });
        Assert.Null(salon.Value!.SalePrice);
        Assert.Null(e.Store.Products[salon.Value.Id].SalePrice);
        Assert.True((await Create(PR(purpose: "salon_use") with { SalePrice = null })).IsOk);
    }

    [Fact]
    public async Task Sale_price_update_rules_legacy_null_and_purpose_switch()
    {
        var e = new Env();
        var salon = (await e.Svc.CreateProductAsync(Admin(), PR(purpose: "salon_use"), default)).Value!;
        // salon_use -> sale без ціни: 422; з ціною: ок
        AssertErr(await e.Svc.UpdateProductAsync(Admin(), salon.Id, PR(purpose: "sale") with { SalePrice = null }, default), "sale_price_required");
        var up = await e.Svc.UpdateProductAsync(Admin(), salon.Id, PR(purpose: "sale") with { SalePrice = 20m }, default);
        Assert.Equal(20m, up.Value!.SalePrice);
        // sale -> salon_use: ціна обнуляється
        var back = await e.Svc.UpdateProductAsync(Admin(), salon.Id, PR(purpose: "salon_use") with { SalePrice = 20m }, default);
        Assert.Null(back.Value!.SalePrice);
        // старий продажний товар без ціни читається (NULL), але перше редагування вимагає ціну
        var legacy = e.Store.AddProduct("Legacy");
        Assert.Null((await e.Svc.GetProductAsync(Admin(), legacy.Id, default)).Value!.SalePrice);
        Assert.Contains(legacy.Id, (await e.Svc.ListProductsAsync(Admin(), new ProductListQuery(null, null, null, null, null, null, null), default)).Value!.Items.Select(x => x.Id));
        AssertErr(await e.Svc.UpdateProductAsync(Admin(), legacy.Id, PR(name: "Legacy", purpose: "both") with { SalePrice = null }, default), "sale_price_required");
        Assert.Equal(5m, (await e.Svc.UpdateProductAsync(Admin(), legacy.Id, PR(name: "Legacy", purpose: "both") with { SalePrice = 5m }, default)).Value!.SalePrice);
    }

    [Fact]
    public async Task Sale_price_is_visible_without_costs_unlike_purchase_price()
    {
        var e = new Env();
        var p = (await e.Svc.CreateProductAsync(Admin(), PR() with { SalePrice = 30m, PurchasePrice = 9m }, default)).Value!;
        var noCosts = AdminWith(("revenue.view", "none"));
        var d = (await e.Svc.GetProductAsync(noCosts, p.Id, default)).Value!;
        Assert.Equal(30m, d.SalePrice);
        Assert.Null(d.PurchasePrice);
        var list = (await e.Svc.ListProductsAsync(noCosts, new ProductListQuery(null, null, null, null, null, null, null), default)).Value!.Items.Single();
        Assert.Equal(30m, list.SalePrice);
        Assert.Null(list.PurchasePrice);
    }

    [Fact]
    public async Task Catalog_write_needs_manage_all_and_purchase_price_needs_costs_edit()
    {
        var e = new Env();
        var manageOwn = AdminWith(("inventory.manage", "own"));
        var denied = await e.Svc.CreateProductAsync(manageOwn, PR(), default);
        AssertErr(denied, "forbidden_role");
        Assert.Equal("all", denied.Error!.Extras!["required"]);
        var costsView = AdminWith(("inventory.costs", "view"));
        var c = await e.Svc.CreateProductAsync(costsView, PR() with { PurchasePrice = 3m }, default);
        AssertErr(c, "forbidden_role");
        Assert.Equal("inventory.costs", c.Error!.Extras!["permission"]);
        Assert.True((await e.Svc.CreateProductAsync(costsView, PR(), default)).IsOk);
        AssertErr(await e.Svc.CreateSupplierAsync(manageOwn, new SupplierRequest("S", null, null, null, null, null), default), "forbidden_role");
    }

    [Fact]
    public async Task Supplier_validation_and_unique_name()
    {
        var e = new Env();
        Task<Result<SupplierDto>> Go(SupplierRequest r) => e.Svc.CreateSupplierAsync(Admin(), r, default);
        AssertErr(await Go(new SupplierRequest(" ", null, null, null, null, null)), "invalid_name");
        AssertErr(await Go(new SupplierRequest("S", new string('c', 201), null, null, null, null)), "invalid_contact_name");
        AssertErr(await Go(new SupplierRequest("S", null, new string('1', 33), null, null, null)), "invalid_phone");
        AssertErr(await Go(new SupplierRequest("S", null, "call me", null, null, null)), "invalid_phone");
        AssertErr(await Go(new SupplierRequest("S", null, null, "no-at-sign", null, null)), "invalid_email");
        AssertErr(await Go(new SupplierRequest("S", null, null, new string('a', 320) + "@x", null, null)), "invalid_email");
        AssertErr(await Go(new SupplierRequest("S", null, null, null, new string('n', 501), null)), "invalid_note");
        var ok = await Go(new SupplierRequest("Acme", "Ann", "+380 (44) 123-45.67", "a@b.c", null, null));
        Assert.True(ok.IsOk);
        AssertErr(await Go(new SupplierRequest("ACME", null, null, null, null, null)), "supplier_name_taken", ErrorKind.Conflict);
        AssertErr(await e.Svc.UpdateSupplierAsync(Admin(), Guid.NewGuid(), new SupplierRequest("Z", null, null, null, null, null), default), "supplier_not_found", ErrorKind.NotFound);
    }

    // ---------- курсори й списки ----------

    [Fact]
    public async Task Products_page_with_keyset_cursor_and_invalid_cursor()
    {
        var e = new Env();
        foreach (var n in new[] { "delta", "Alpha", "charlie", "Bravo", "echo" }) e.Store.AddProduct(n);
        var p1 = await e.Svc.ListProductsAsync(Admin(), new ProductListQuery(null, null, null, null, null, null, 2), default);
        Assert.Equal(["Alpha", "Bravo"], p1.Value!.Items.Select(i => i.Name).ToArray());
        Assert.True(p1.Value.HasMore);
        var p2 = await e.Svc.ListProductsAsync(Admin(), new ProductListQuery(null, null, null, null, null, p1.Value.NextCursor, 2), default);
        Assert.Equal(["charlie", "delta"], p2.Value!.Items.Select(i => i.Name).ToArray());
        var p3 = await e.Svc.ListProductsAsync(Admin(), new ProductListQuery(null, null, null, null, null, p2.Value.NextCursor, 2), default);
        Assert.Equal(["echo"], p3.Value!.Items.Select(i => i.Name).ToArray());
        Assert.False(p3.Value.HasMore);
        Assert.Null(p3.Value.NextCursor);

        AssertErr(await e.Svc.ListProductsAsync(Admin(), new ProductListQuery(null, null, null, null, null, "garbage", 2), default), "invalid_cursor");
        // курсор від інших параметрів
        AssertErr(await e.Svc.ListProductsAsync(Admin(), new ProductListQuery("x", null, null, null, null, p1.Value.NextCursor, 2), default), "invalid_cursor");
    }

    [Fact]
    public async Task Product_search_filters_and_inactive_default()
    {
        var e = new Env();
        e.Store.AddProduct("Red shampoo", barcode: "111");
        e.Store.AddProduct("Blue gel", barcode: "222");
        e.Store.AddProduct("Old", active: false);
        async Task<string[]> Q(string? search = null, string? active = null) =>
            [.. (await e.Svc.ListProductsAsync(Admin(), new ProductListQuery(search, null, null, null, active, null, null), default)).Value!.Items.Select(i => i.Name)];
        Assert.Equal(["Blue gel", "Red shampoo"], await Q());
        Assert.Equal(["Old"], await Q(active: "false"));
        Assert.Equal(3, (await Q(active: "all")).Length);
        Assert.Equal(["Red shampoo"], await Q("SHAMP"));
        Assert.Equal(["Blue gel"], await Q("222"));
        AssertErr(await e.Svc.ListProductsAsync(Admin(), new ProductListQuery(null, null, null, null, "maybe", null, null), default), "invalid_status");
    }

    [Fact]
    public async Task Range_validation_for_movements_and_operations()
    {
        var e = new Env();
        var q = (string? from, string? to) => new MovementListQuery(null, null, null, null, from, to, null, null);
        AssertErr(await e.Svc.ListMovementsAsync(Admin(), q("nonsense", null), default), "invalid_range");
        AssertErr(await e.Svc.ListMovementsAsync(Admin(), q("2026-11-10T00:00:00Z", "2026-11-01T00:00:00Z"), default), "invalid_range");
        AssertErr(await e.Svc.ListMovementsAsync(Admin(), q("2025-01-01T00:00:00Z", "2026-11-01T00:00:00Z"), default), "invalid_range"); // > 366 діб
        AssertErr(await e.Svc.ListOperationsAsync(Admin(), new OperationListQuery(null, null, "x", null, null, null), default), "invalid_range");
    }

    // ---------- налаштування закладу ----------

    [Fact]
    public async Task Location_settings_validate_min_stock_scope_and_not_found()
    {
        var e = new Env();
        var p = e.Store.AddProduct("P", "pcs");
        AssertErr(await e.Svc.PutLocationSettingAsync(Admin(), p.Id, e.Loc.Id, new LocationSettingRequest(1.5m, true), default), "invalid_min_stock");
        Assert.True((await e.Svc.PutLocationSettingAsync(Admin(), p.Id, e.Loc.Id, new LocationSettingRequest(2, false), default)).IsOk);
        Assert.Contains((e.Loc.Id, p.Id), e.Store.Disabled);
        AssertErr(await e.Svc.PutLocationSettingAsync(Admin(), p.Id, Guid.NewGuid(), new LocationSettingRequest(1, true), default), "location_not_found");
        AssertErr(await e.Svc.PutLocationSettingAsync(Admin(), Guid.NewGuid(), e.Loc.Id, new LocationSettingRequest(1, true), default), "product_not_found");
        Assert.True((await e.Svc.DeleteLocationSettingAsync(Admin(), p.Id, e.Loc.Id, default)).IsOk);
        AssertErr(await e.Svc.DeleteLocationSettingAsync(Admin(), p.Id, e.Loc.Id, default), "setting_not_found", ErrorKind.NotFound);
    }

    // ---------- права: ключі каталогу ----------

    [Fact]
    public async Task Owner_has_all_and_module_off_drops_inventory_permissions()
    {
        var e = new Env();
        var scope = await e.Svc.GetScopeAsync(Owner(), default);
        Assert.Equal("edit", scope.Value!.Costs);
        Assert.True(scope.Value.CanEditCatalog);
        Assert.Equal(30, scope.Value.ExpiringSoonDays);

        var off = Actor.ForTest(Roles.Admin, modules: BeautyModules.All);
        Assert.Equal("none", off.Access.Level(PermissionKeys.InventoryView));
        AssertErr(await e.Svc.GetScopeAsync(off, default), "forbidden_role");
        // beauty_analytics вимкнено: revenue.view знижується, а разом з ним inventory.costs (requires)
        var noAnalytics = Actor.ForTest(Roles.Admin, modules: BeautyModules.Known.Where(m => m != "beauty_analytics"));
        Assert.Equal("all", noAnalytics.Access.Level(PermissionKeys.InventoryView));
        Assert.Equal("none", noAnalytics.Access.Level(PermissionKeys.InventoryCosts));
    }

    [Fact]
    public void Fefo_planner_orders_and_shares_one_balance_between_lines()
    {
        var t = DateTimeOffset.UnixEpoch;
        var p = Guid.NewGuid();
        var b1 = new BatchRow(Guid.NewGuid(), p, new DateOnly(2026, 12, 1), t, 5, null, null, null);
        var b2 = new BatchRow(Guid.NewGuid(), p, null, t, 5, null, null, null);
        // явна партія b1 (3) + підбір (4): підбір бере залишок b1 (2), потім b2 (2)
        var plan = FefoPlanner.Allocate([new(0, p, 3, b1.Id), new(1, p, 4, null)], [b1, b2], false, Today);
        Assert.Empty(plan.Shortages);
        Assert.Equal([(b1.Id, 3m), (b1.Id, 2m), (b2.Id, 2m)], plan.Takes.Select(x => (x.BatchId, x.Quantity)).ToArray());
        var short1 = FefoPlanner.Allocate([new(0, p, 11, null)], [b1, b2], false, Today);
        Assert.Equal(10m, short1.Shortages.Single().Available);
    }

    [Fact]
    public void Fefo_planner_serves_explicit_batch_lines_before_fefo_lines_M1()
    {
        var t = DateTimeOffset.UnixEpoch;
        var p = Guid.NewGuid();
        var b1 = new BatchRow(Guid.NewGuid(), p, new DateOnly(2027, 6, 1), t, 5, null, null, null);
        var b2 = new BatchRow(Guid.NewGuid(), p, new DateOnly(2026, 12, 1), t, 5, null, null, null); // FEFO-перша
        // рядок 0 без партії (5) не повинен забрати b2, яку явно просить рядок 1
        var plan = FefoPlanner.Allocate([new(0, p, 5, null), new(1, p, 5, b2.Id)], [b1, b2], false, Today);
        Assert.Empty(plan.Shortages);
        Assert.Equal([(0, b1.Id), (1, b2.Id)], plan.Takes.Select(x => (x.Line, x.BatchId)).ToArray()); // Line збережено, порядок за рядками
    }

    [Fact]
    public void Fefo_planner_rejects_explicit_expired_batch_when_skipping_expired_and_foreign_product_batch()
    {
        var t = DateTimeOffset.UnixEpoch;
        var p = Guid.NewGuid();
        var expired = new BatchRow(Guid.NewGuid(), p, Today.AddDays(-1), t, 5, null, null, null);
        var other = new BatchRow(Guid.NewGuid(), Guid.NewGuid(), null, t, 5, null, null, null);
        var skip = FefoPlanner.Allocate([new(0, p, 1, expired.Id)], [expired], true, Today);
        Assert.True(skip.Shortages.Single().BatchExpired);
        Assert.Empty(skip.Takes);
        Assert.Empty(FefoPlanner.Allocate([new(0, p, 1, expired.Id)], [expired], false, Today).Shortages); // списання: прострочене можна
        var foreign = FefoPlanner.Allocate([new(0, p, 1, other.Id)], [other], false, Today);
        Assert.Equal(0m, foreign.Shortages.Single().Available);
        Assert.Empty(foreign.Takes);
    }
}

/// <summary>Проксі з можливістю перевизначити окремі методи (імітація гонок у сервісних тестах).</summary>
internal abstract class IInventoryStoreProxy(FakeInventoryStore inner) : IInventoryStore
{
    public Task<IReadOnlyList<LocationRow>> ListInventoryLocationsAsync(CancellationToken ct) => inner.ListInventoryLocationsAsync(ct);
    public Task<IReadOnlyList<Guid>> SpecialistLocationIdsAsync(Guid s, CancellationToken ct) => inner.SpecialistLocationIdsAsync(s, ct);
    public Task<IReadOnlyList<ProductDto>> ListProductsAsync(CancellationToken ct) => inner.ListProductsAsync(ct);
    public Task<IReadOnlySet<Guid>> ProductIdsWithMovementsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) => inner.ProductIdsWithMovementsAsync(ids, ct);
    public Task<ProductDetailDto?> GetProductAsync(Guid id, CancellationToken ct) => inner.GetProductAsync(id, ct);
    public Task<ProductDto?> GetProductByBarcodeAsync(string code, CancellationToken ct) => inner.GetProductByBarcodeAsync(code, ct);
    public Task<IReadOnlyDictionary<Guid, ProductDto>> GetProductsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) => inner.GetProductsAsync(ids, ct);
    public Task<IReadOnlyList<string>> ListCategoriesAsync(CancellationToken ct) => inner.ListCategoriesAsync(ct);
    public Task<IReadOnlyList<CategoryDto>> ListCategoryNodesAsync(CancellationToken ct) => inner.ListCategoryNodesAsync(ct);
    public Task<CategoryRow?> GetCategoryRowAsync(Guid id, CancellationToken ct) => inner.GetCategoryRowAsync(id, ct);
    public Task<CategoryWriteResult> WriteCategoryAsync(Func<IReadOnlyList<CategoryRow>, CategoryPlan> decide, DateTimeOffset n, CancellationToken ct) => inner.WriteCategoryAsync(decide, n, ct);
    public Task<CategoryWriteOutcome> DeleteCategoryAsync(Guid id, CancellationToken ct) => inner.DeleteCategoryAsync(id, ct);
    public Task<ProductWriteResult> CreateProductAsync(ProductData d, Guid u, DateTimeOffset n, CancellationToken ct) => inner.CreateProductAsync(d, u, n, ct);
    public Task<ProductWriteResult> UpdateProductAsync(Guid id, ProductData d, Guid u, DateTimeOffset n, CancellationToken ct) => inner.UpdateProductAsync(id, d, u, n, ct);
    public Task<LocationSettingDto?> UpsertSettingAsync(Guid p, Guid l, decimal? m, bool e, Guid u, DateTimeOffset n, CancellationToken ct) => inner.UpsertSettingAsync(p, l, m, e, u, n, ct);
    public Task<bool> DeleteSettingAsync(Guid p, Guid l, CancellationToken ct) => inner.DeleteSettingAsync(p, l, ct);
    public Task<IReadOnlyDictionary<Guid, bool>> GetEnabledMapAsync(Guid l, IReadOnlyCollection<Guid> ids, CancellationToken ct) => inner.GetEnabledMapAsync(l, ids, ct);
    public Task<IReadOnlyList<SupplierDto>> ListSuppliersAsync(CancellationToken ct) => inner.ListSuppliersAsync(ct);
    public Task<SupplierDto?> GetSupplierAsync(Guid id, CancellationToken ct) => inner.GetSupplierAsync(id, ct);
    public Task<SupplierWriteResult> CreateSupplierAsync(SupplierData d, DateTimeOffset n, CancellationToken ct) => inner.CreateSupplierAsync(d, n, ct);
    public Task<SupplierWriteResult> UpdateSupplierAsync(Guid id, SupplierData d, DateTimeOffset n, CancellationToken ct) => inner.UpdateSupplierAsync(id, d, n, ct);
    public Task<IReadOnlyList<StockRaw>> ListStockAsync(StockFilter f, string[]? a, int t, CancellationToken ct) => inner.ListStockAsync(f, a, t, ct);
    public Task<IReadOnlyList<SummaryRaw>> ListSummaryAsync(SummaryFilter f, string[]? a, int t, CancellationToken ct) => inner.ListSummaryAsync(f, a, t, ct);
    public Task<IReadOnlyList<BatchDto>> ListBatchesAsync(BatchFilter f, string[]? a, int t, CancellationToken ct) => inner.ListBatchesAsync(f, a, t, ct);
    public Task<IReadOnlyDictionary<Guid, BatchRef>> GetBatchRefsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) => inner.GetBatchRefsAsync(ids, ct);
    public Task<IReadOnlyList<MovementDto>> ListMovementsAsync(MovementFilter f, string[]? a, int t, CancellationToken ct) => inner.ListMovementsAsync(f, a, t, ct);
    public Task<IReadOnlyList<OperationSummaryDto>> ListOperationsAsync(OperationFilter f, string[]? a, int t, CancellationToken ct) => inner.ListOperationsAsync(f, a, t, ct);
    public Task<OperationDto?> GetOperationAsync(Guid id, CancellationToken ct) => inner.GetOperationAsync(id, ct);
    public virtual Task<KeyedOperation?> FindOperationByKeyAsync(string keyHash, CancellationToken ct) => inner.FindOperationByKeyAsync(keyHash, ct);
    public Task<StockWriteResult> PostReceiptAsync(OperationHeader h, IReadOnlyList<ReceiptLine> l, CancellationToken ct) => inner.PostReceiptAsync(h, l, ct);
    public Task<StockWriteResult> PostWriteOffAsync(OperationHeader h, IReadOnlyList<StockLine> l, DateOnly t, CancellationToken ct) => inner.PostWriteOffAsync(h, l, t, ct);
    public Task<StockWriteResult> PostTransferAsync(OperationHeader h, IReadOnlyList<StockLine> l, DateOnly t, CancellationToken ct) => inner.PostTransferAsync(h, l, t, ct);
    public Task<IReadOnlyList<StocktakeSummaryDto>> ListStocktakesAsync(StocktakeFilter f, string[]? a, int t, CancellationToken ct) => inner.ListStocktakesAsync(f, a, t, ct);
    public Task<StocktakeDto?> GetStocktakeAsync(Guid id, CancellationToken ct) => inner.GetStocktakeAsync(id, ct);
    public Task<StocktakeWriteResult> CreateStocktakeAsync(Guid l, string? n, IReadOnlyList<Guid>? p, Guid u, DateTimeOffset now, CancellationToken ct) => inner.CreateStocktakeAsync(l, n, p, u, now, ct);
    public Task<StocktakeWriteResult> PutStocktakeLinesAsync(Guid id, int v, IReadOnlyList<StocktakeLineChange> l, Guid u, DateTimeOffset n, CancellationToken ct) => inner.PutStocktakeLinesAsync(id, v, l, u, n, ct);
    public Task<StocktakeWriteResult> PostStocktakeAsync(Guid id, int v, Guid u, DateTimeOffset n, DateOnly t, CancellationToken ct) => inner.PostStocktakeAsync(id, v, u, n, t, ct);
    public Task<StocktakeWriteResult> CancelStocktakeAsync(Guid id, int v, Guid u, DateTimeOffset n, CancellationToken ct) => inner.CancelStocktakeAsync(id, v, u, n, ct);
}
