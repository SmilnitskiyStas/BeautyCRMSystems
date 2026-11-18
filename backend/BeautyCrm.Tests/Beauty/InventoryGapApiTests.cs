using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Tests.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static BeautyCrm.Tests.Auth.AuthApiFixture;

namespace BeautyCrm.Tests.Beauty;

/// <summary>
/// TASK-879 (§32.11 п.5, 9, 10, 11, 12, 14): доповнення покриття складу на реальній PostgreSQL через HTTP. Те, чого немає в
/// <see cref="InventoryApiTests"/>: межа «прострочено» опівночі Europe/Kyiv на БД (<see cref="FrozenTime"/> замість годинника),
/// гонки переміщень і змішані гонки списання/переміщення за один рівень, ізоляція ключа ідемпотентності між tenant,
/// IDOR партії між закладами/tenant, цілі для pcs/pack на всіх трьох операціях і рандомізована серія з фіксованим seed
/// (інваріант level = Σ batches = Σ movements + повний обхід курсору журналу). Час тестового сценарію не залежить від реального годинника.
/// </summary>
public sealed class InventoryGapApiTests(AuthApiFixture fx) : IClassFixture<AuthApiFixture>, IDisposable
{
    private readonly List<IDisposable> _disposables = [];
    private static readonly DateOnly Expired = new(2020, 1, 1);

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
    }

    private void NeedDb() => Skip.If(fx.SkipReason is not null, fx.SkipReason);

    private HttpClient NewClient(FrozenTime? clock = null)
    {
        var f = fx.CreateFactory(10_000).WithWebHostBuilder(b =>
        {
            b.UseSetting("Inventory:WritePerUserPerMinute", "10000");
            if (clock is not null)
                b.ConfigureTestServices(s =>
                {
                    s.RemoveAll<TimeProvider>();
                    s.AddSingleton<TimeProvider>(clock);
                });
        });
        _disposables.Add(f);
        return f.CreateClient();
    }

    private sealed record Tn(TenantSeed Seed, BeautySeed Beauty, Guid Loc2, string Owner)
    {
        public Guid Id => Seed.TenantId;
        public Guid L1 => Beauty.LocationId;
    }

    private async Task<Tn> NewTenantAsync()
    {
        var t = await fx.CreateTenantAsync([.. BeautyModules.Known]);
        var owner = await fx.LoginAsync(t.Slug, t.OwnerEmail);
        var seed = await fx.SeedBeautyAsync(t.TenantId);
        Guid loc2;
        await using (var ctx = fx.Db.CreateContext(t.TenantId))
        {
            var l2 = new Location { Name = "Second", Timezone = "Europe/Kyiv" };
            ctx.Locations.Add(l2);
            await ctx.Locations.Where(l => l.Id == seed.LocationId).ExecuteUpdateAsync(s => s.SetProperty(l => l.Timezone, "Europe/Kyiv"));
            await ctx.SaveChangesAsync();
            loc2 = l2.Id;
        }
        return new Tn(t, seed, loc2, owner.AccessToken);
    }

    private static Task<HttpResponseMessage> Call(HttpClient c, HttpMethod m, string path, string token, object? body = null, string? key = null)
    {
        var r = new HttpRequestMessage(m, "/api/beauty/inventory" + path);
        if (body is not null) r.Content = JsonContent.Create(body, options: Json);
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (key is not null) r.Headers.Add("Idempotency-Key", key);
        return c.SendAsync(r);
    }

    private static Task<HttpResponseMessage> Get(HttpClient c, string path, string token) => Call(c, HttpMethod.Get, path, token);
    private static Task<HttpResponseMessage> Op(HttpClient c, string path, string token, object body, string? key = null) =>
        Call(c, HttpMethod.Post, path, token, body, key ?? Guid.NewGuid().ToString());

    private static async Task<JsonElement> Ok(HttpResponseMessage r, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == expected, $"expected {expected}, got {(int)r.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task Fails(HttpResponseMessage r, HttpStatusCode status, string code)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == status, $"expected {status}, got {(int)r.StatusCode}: {body}");
        Assert.Equal(code, JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
    }

    private static async Task<Guid> NewProduct(HttpClient c, string token, string name, string unit = "pcs")
    {
        var r = await Call(c, HttpMethod.Post, "/products", token, new { name, unit, purpose = "both", trackExpiry = false });
        return (await Ok(r, HttpStatusCode.Created)).GetProperty("id").GetGuid();
    }

    private static async Task Receive(HttpClient c, string token, Guid loc, Guid product, decimal qty, string? exp = null) =>
        await Ok(await Op(c, "/receipts", token, new { locationId = loc, lines = new[] { new { productId = product, quantity = qty, expiresOn = exp } } }), HttpStatusCode.Created);

    private async Task<Guid> SeedBatchAsync(Guid tenant, Guid loc, Guid product, decimal qty, DateOnly? exp)
    {
        await using var ctx = fx.Db.CreateContext(tenant);
        var userId = await ctx.Users.AsNoTracking().Select(u => u.Id).FirstAsync();
        var now = DateTimeOffset.UtcNow.AddDays(-30);
        var op = new StockOperation { Id = Guid.NewGuid(), Kind = StockOperationKind.Receipt, LocationId = loc, CreatedByUserId = userId, CreatedAt = now };
        var batch = new StockBatch { Id = Guid.NewGuid(), LocationId = loc, ProductId = product, OperationId = op.Id, ReceivedAt = now, ExpiresOn = exp, InitialQuantity = qty, Quantity = qty };
        ctx.StockOperations.Add(op);
        ctx.StockBatches.Add(batch);
        ctx.StockMovements.Add(new StockMovement { Id = Guid.NewGuid(), OperationId = op.Id, LocationId = loc, ProductId = product, BatchId = batch.Id, Type = StockMovementType.Receipt, Quantity = qty, CreatedByUserId = userId, OccurredAt = now });
        var level = await ctx.StockLevels.FirstOrDefaultAsync(l => l.LocationId == loc && l.ProductId == product);
        if (level is null) ctx.StockLevels.Add(new StockLevel { LocationId = loc, ProductId = product, Quantity = qty });
        else level.Quantity += qty;
        await ctx.SaveChangesAsync();
        return batch.Id;
    }

    private async Task<decimal> LevelAsync(Guid tenant, Guid loc, Guid product)
    {
        await using var ctx = fx.Db.CreateContext(tenant);
        return await ctx.StockLevels.AsNoTracking().Where(l => l.LocationId == loc && l.ProductId == product).Select(l => l.Quantity).FirstOrDefaultAsync();
    }

    private async Task AssertInvariantsAsync(Guid tenant)
    {
        await using var ctx = fx.Db.CreateContext(tenant);
        var levels = await ctx.StockLevels.AsNoTracking().ToListAsync();
        var batches = await ctx.StockBatches.AsNoTracking().ToListAsync();
        var moves = await ctx.StockMovements.AsNoTracking().ToListAsync();
        var pairs = levels.Select(l => (l.LocationId, l.ProductId)).Union(batches.Select(b => (b.LocationId, b.ProductId))).Union(moves.Select(m => (m.LocationId, m.ProductId))).ToList();
        foreach (var (loc, prod) in pairs)
        {
            var level = levels.Where(l => l.LocationId == loc && l.ProductId == prod).Sum(l => l.Quantity);
            Assert.True(level >= 0);
            Assert.Equal(level, batches.Where(b => b.LocationId == loc && b.ProductId == prod).Sum(b => b.Quantity));
            Assert.Equal(level, moves.Where(m => m.LocationId == loc && m.ProductId == prod).Sum(m => m.Quantity));
        }
        Assert.All(batches, b => Assert.InRange(b.Quantity, 0m, b.InitialQuantity));
        foreach (var op in await ctx.StockOperations.AsNoTracking().Where(o => o.Kind == StockOperationKind.Transfer).ToListAsync())
            Assert.Equal(0m, moves.Where(m => m.OperationId == op.Id).Sum(m => m.Quantity));
    }

    // ---------- межа «прострочено» опівночі Europe/Kyiv на реальній БД ----------

    [SkippableTheory]
    [InlineData("2026-11-18T21:59:00Z", false)] // 23:59 за Києвом 18.11: партія зі строком 18.11 ще не прострочена
    [InlineData("2026-11-18T22:00:00Z", true)]  // 00:00 19.11 за Києвом, хоча UTC-дата ще 18.11
    [InlineData("2026-11-18T22:01:00Z", true)]
    public async Task Expiry_boundary_is_kyiv_midnight_on_the_database(string now, bool expired)
    {
        NeedDb();
        var clock = new FrozenTime(DateTimeOffset.Parse(now).ToUnixTimeSeconds());
        var c = NewClient(clock);
        var t = await NewTenantAsync();
        var prod = await NewProduct(c, t.Owner, "P");
        var near = await SeedBatchAsync(t.Id, t.L1, prod, 5, new DateOnly(2026, 11, 18));
        var far = await SeedBatchAsync(t.Id, t.L1, prod, 5, new DateOnly(2027, 5, 1));

        var tr = await Ok(await Op(c, "/transfers", t.Owner, new { fromLocationId = t.L1, toLocationId = t.Loc2, lines = new[] { new { productId = prod, quantity = 3 } } }), HttpStatusCode.Created);
        var outLeg = tr.GetProperty("movements").EnumerateArray().Single(m => m.GetProperty("type").GetString() == "transfer_out");
        Assert.Equal(expired ? far : near, outLeg.GetProperty("batchId").GetGuid());

        // списання без явної партії бере прострочену першою тільки після півночі
        var wo = await Ok(await Op(c, "/write-offs", t.Owner, new { locationId = t.L1, reason = "lost", lines = new[] { new { productId = prod, quantity = 1 } } }), HttpStatusCode.Created);
        var woBatch = wo.GetProperty("movements")[0].GetProperty("batchId").GetGuid();
        Assert.Equal(near, woBatch); // near (строк 18.11) раніший за far за FEFO в обох випадках; після півночі - ще й прострочений
        await AssertInvariantsAsync(t.Id);
    }

    // ---------- гонки ----------

    [SkippableFact]
    public async Task Two_parallel_transfers_of_the_last_unit_one_wins_without_negative_level()
    {
        NeedDb();
        var c = NewClient();
        var t = await NewTenantAsync();
        var prod = await NewProduct(c, t.Owner, "P");
        await Receive(c, t.Owner, t.L1, prod, 1);
        var rs = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Op(c, "/transfers", t.Owner, new { fromLocationId = t.L1, toLocationId = t.Loc2, lines = new[] { new { productId = prod, quantity = 1 } } })));
        Assert.Equal(1, rs.Count(r => r.StatusCode == HttpStatusCode.Created));
        var loser = rs.Single(r => r.StatusCode == HttpStatusCode.UnprocessableEntity);
        Assert.Equal("insufficient_stock", JsonDocument.Parse(await loser.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
        Assert.Equal(0, await LevelAsync(t.Id, t.L1, prod));
        Assert.Equal(1, await LevelAsync(t.Id, t.Loc2, prod));
        await AssertInvariantsAsync(t.Id);
    }

    [SkippableFact]
    public async Task Parallel_write_off_and_transfer_of_the_last_unit_one_wins()
    {
        NeedDb();
        var c = NewClient();
        var t = await NewTenantAsync();
        var prod = await NewProduct(c, t.Owner, "P");
        for (var round = 0; round < 3; round++)
        {
            await Receive(c, t.Owner, t.L1, prod, 1);
            var rs = await Task.WhenAll(
                Op(c, "/write-offs", t.Owner, new { locationId = t.L1, reason = "lost", lines = new[] { new { productId = prod, quantity = 1 } } }),
                Op(c, "/transfers", t.Owner, new { fromLocationId = t.L1, toLocationId = t.Loc2, lines = new[] { new { productId = prod, quantity = 1 } } }));
            Assert.Equal(1, rs.Count(r => r.StatusCode == HttpStatusCode.Created));
            Assert.Equal(1, rs.Count(r => r.StatusCode == HttpStatusCode.UnprocessableEntity));
            Assert.Equal(0, await LevelAsync(t.Id, t.L1, prod));
            await AssertInvariantsAsync(t.Id);
            // повернути з L2 назад, щоб не накопичувати: списати
            var back = await LevelAsync(t.Id, t.Loc2, prod);
            if (back > 0) await Ok(await Op(c, "/write-offs", t.Owner, new { locationId = t.Loc2, reason = "lost", lines = new[] { new { productId = prod, quantity = back } } }), HttpStatusCode.Created);
        }
    }

    [SkippableFact]
    public async Task Many_parallel_write_offs_never_overdraw_and_win_count_equals_stock()
    {
        NeedDb();
        var c = NewClient();
        var t = await NewTenantAsync();
        var prod = await NewProduct(c, t.Owner, "P");
        await Receive(c, t.Owner, t.L1, prod, 5);
        await SeedBatchAsync(t.Id, t.L1, prod, 3, Expired); // 8 одиниць у двох партіях (одна прострочена)
        var rs = await Task.WhenAll(Enumerable.Range(0, 14).Select(_ => Op(c, "/write-offs", t.Owner, new { locationId = t.L1, reason = "lost", lines = new[] { new { productId = prod, quantity = 1 } } })));
        Assert.Equal(8, rs.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(6, rs.Count(r => r.StatusCode == HttpStatusCode.UnprocessableEntity));
        Assert.Equal(0, await LevelAsync(t.Id, t.L1, prod));
        await AssertInvariantsAsync(t.Id);
    }

    // ---------- ідемпотентність і ізоляція ----------

    [SkippableFact]
    public async Task Same_idempotency_key_in_two_tenants_is_independent()
    {
        NeedDb();
        var c = NewClient();
        var a = await NewTenantAsync();
        var b = await NewTenantAsync();
        var pa = await NewProduct(c, a.Owner, "P");
        var pb = await NewProduct(c, b.Owner, "P");
        const string key = "shared-key-across-tenants";
        var ra = await Op(c, "/receipts", a.Owner, new { locationId = a.L1, lines = new[] { new { productId = pa, quantity = 2 } } }, key);
        var rb = await Op(c, "/receipts", b.Owner, new { locationId = b.L1, lines = new[] { new { productId = pb, quantity = 7 } } }, key);
        Assert.Equal(HttpStatusCode.Created, ra.StatusCode);
        Assert.Equal(HttpStatusCode.Created, rb.StatusCode); // не 200-replay і не idempotency_key_reused
        Assert.False(rb.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(2, await LevelAsync(a.Id, a.L1, pa));
        Assert.Equal(7, await LevelAsync(b.Id, b.L1, pb));
    }

    [SkippableFact]
    public async Task Replay_returns_the_same_operation_without_second_movement_and_other_body_is_422()
    {
        NeedDb();
        var c = NewClient();
        var t = await NewTenantAsync();
        var prod = await NewProduct(c, t.Owner, "P");
        await Receive(c, t.Owner, t.L1, prod, 10);
        var body = new { locationId = t.L1, reason = "lost", lines = new[] { new { productId = prod, quantity = 4 } } };
        var first = await Op(c, "/write-offs", t.Owner, body, "wo-1");
        var firstJson = await Ok(first, HttpStatusCode.Created);
        var again = await Op(c, "/write-offs", t.Owner, body, "wo-1");
        var againJson = await Ok(again, HttpStatusCode.OK);
        Assert.True(again.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(firstJson.GetProperty("id").GetGuid(), againJson.GetProperty("id").GetGuid());
        Assert.Equal(6, await LevelAsync(t.Id, t.L1, prod)); // списано один раз
        await Fails(await Op(c, "/write-offs", t.Owner, new { locationId = t.L1, reason = "lost", lines = new[] { new { productId = prod, quantity = 5 } } }, "wo-1"), HttpStatusCode.UnprocessableEntity, "idempotency_key_reused");
        // ключ однієї операції не можна повторно використати для іншого виду операції
        await Fails(await Op(c, "/transfers", t.Owner, new { fromLocationId = t.L1, toLocationId = t.Loc2, lines = new[] { new { productId = prod, quantity = 4 } } }, "wo-1"), HttpStatusCode.UnprocessableEntity, "idempotency_key_reused");
        Assert.Equal(6, await LevelAsync(t.Id, t.L1, prod));
        await AssertInvariantsAsync(t.Id);
    }

    // ---------- IDOR партії ----------

    [SkippableFact]
    public async Task Explicit_batch_of_another_location_or_tenant_is_404_batch_not_found_and_changes_nothing()
    {
        NeedDb();
        var c = NewClient();
        var a = await NewTenantAsync();
        var b = await NewTenantAsync();
        var pa = await NewProduct(c, a.Owner, "P");
        var pb = await NewProduct(c, b.Owner, "P");
        await Receive(c, a.Owner, a.L1, pa, 5);
        var foreignLoc = await SeedBatchAsync(a.Id, a.Loc2, pa, 5, null);
        var foreignTenant = await SeedBatchAsync(b.Id, b.L1, pb, 5, null);

        await Fails(await Op(c, "/write-offs", a.Owner, new { locationId = a.L1, reason = "lost", lines = new[] { new { productId = pa, quantity = 1, batchId = foreignLoc } } }), HttpStatusCode.NotFound, "batch_not_found");
        await Fails(await Op(c, "/write-offs", a.Owner, new { locationId = a.L1, reason = "lost", lines = new[] { new { productId = pa, quantity = 1, batchId = foreignTenant } } }), HttpStatusCode.NotFound, "batch_not_found");
        await Fails(await Op(c, "/transfers", a.Owner, new { fromLocationId = a.L1, toLocationId = a.Loc2, lines = new[] { new { productId = pa, quantity = 1, batchId = foreignTenant } } }), HttpStatusCode.NotFound, "batch_not_found");
        Assert.Equal(5, await LevelAsync(a.Id, a.L1, pa));
        Assert.Equal(5, await LevelAsync(b.Id, b.L1, pb));
        await AssertInvariantsAsync(a.Id);
        await AssertInvariantsAsync(b.Id);
    }

    // ---------- цілі для pcs / pack ----------

    [SkippableTheory]
    [InlineData("pcs")]
    [InlineData("pack")]
    public async Task Fractional_quantity_is_rejected_for_countable_units_on_every_operation(string unit)
    {
        NeedDb();
        var c = NewClient();
        var t = await NewTenantAsync();
        var prod = await NewProduct(c, t.Owner, "P", unit);
        await Receive(c, t.Owner, t.L1, prod, 5);
        await Fails(await Op(c, "/receipts", t.Owner, new { locationId = t.L1, lines = new[] { new { productId = prod, quantity = 1.5 } } }), HttpStatusCode.UnprocessableEntity, "invalid_quantity");
        await Fails(await Op(c, "/write-offs", t.Owner, new { locationId = t.L1, reason = "lost", lines = new[] { new { productId = prod, quantity = 0.5 } } }), HttpStatusCode.UnprocessableEntity, "invalid_quantity");
        await Fails(await Op(c, "/transfers", t.Owner, new { fromLocationId = t.L1, toLocationId = t.Loc2, lines = new[] { new { productId = prod, quantity = 2.25 } } }), HttpStatusCode.UnprocessableEntity, "invalid_quantity");
        await Fails(await Op(c, "/receipts", t.Owner, new { locationId = t.L1, lines = new[] { new { productId = prod, quantity = 0 } } }), HttpStatusCode.UnprocessableEntity, "invalid_quantity");
        Assert.Equal(5, await LevelAsync(t.Id, t.L1, prod));
    }

    // ---------- рандомізована серія (фіксований seed) ----------

    [SkippableTheory]
    [InlineData(11)]
    [InlineData(2026)]
    public async Task Random_http_series_keeps_invariants_matches_model_and_cursor_walk_is_complete(int seed)
    {
        NeedDb();
        var rnd = new Random(seed);
        var c = NewClient();
        var t = await NewTenantAsync();
        var locs = new[] { t.L1, t.Loc2 };
        var prods = new[] { await NewProduct(c, t.Owner, "A"), await NewProduct(c, t.Owner, "B") };
        var model = new Dictionary<(Guid, Guid), decimal>();
        decimal M(Guid l, Guid p) => model.GetValueOrDefault((l, p));
        var successes = 0;

        for (var i = 0; i < 50; i++)
        {
            var loc = locs[rnd.Next(2)];
            var other = loc == locs[0] ? locs[1] : locs[0];
            var prod = prods[rnd.Next(2)];
            var qty = rnd.Next(1, 7);
            switch (rnd.Next(3))
            {
                case 0:
                {
                    var exp = rnd.Next(3) switch { 0 => null, 1 => "2099-01-01", _ => "2098-06-01" };
                    await Receive(c, t.Owner, loc, prod, qty, exp);
                    model[(loc, prod)] = M(loc, prod) + qty;
                    successes++;
                    break;
                }
                case 1:
                {
                    var r = await Op(c, "/write-offs", t.Owner, new { locationId = loc, reason = "lost", lines = new[] { new { productId = prod, quantity = qty } } });
                    if (M(loc, prod) >= qty) { Assert.Equal(HttpStatusCode.Created, r.StatusCode); model[(loc, prod)] = M(loc, prod) - qty; successes++; }
                    else await Fails(r, HttpStatusCode.UnprocessableEntity, "insufficient_stock");
                    break;
                }
                default:
                {
                    var r = await Op(c, "/transfers", t.Owner, new { fromLocationId = loc, toLocationId = other, lines = new[] { new { productId = prod, quantity = qty } } });
                    if (M(loc, prod) >= qty) { Assert.Equal(HttpStatusCode.Created, r.StatusCode); model[(loc, prod)] = M(loc, prod) - qty; model[(other, prod)] = M(other, prod) + qty; successes++; }
                    else await Fails(r, HttpStatusCode.UnprocessableEntity, "insufficient_stock");
                    break;
                }
            }
            if (i % 10 == 9) await AssertInvariantsAsync(t.Id);
        }

        foreach (var l in locs) foreach (var p in prods) Assert.Equal(M(l, p), await LevelAsync(t.Id, l, p));
        await AssertInvariantsAsync(t.Id);

        // повний обхід курсору журналу рухів: без дублів і пропусків, кількість збігається з БД
        var ids = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = await Ok(await Get(c, "/movements?take=7" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)), t.Owner));
            ids.AddRange(page.GetProperty("items").EnumerateArray().Select(m => m.GetProperty("id").GetGuid()));
            // nextCursor може бути відсутнім або null на останній сторінці
            cursor = page.TryGetProperty("nextCursor", out var nc) && nc.ValueKind == JsonValueKind.String ? nc.GetString() : null;
        } while (cursor is not null);
        await using var ctx = fx.Db.CreateContext(t.Id);
        Assert.Equal(await ctx.StockMovements.CountAsync(), ids.Count);
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.True(successes > 10);
    }
}
