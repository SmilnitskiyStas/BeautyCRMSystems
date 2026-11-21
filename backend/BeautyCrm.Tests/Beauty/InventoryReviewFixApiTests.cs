using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
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
/// TASK-880 (security-review складу) на реальній PostgreSQL через HTTP: M1 (стор. залишків/зведення, фільтри й пошук у БД, агрегати),
/// L1 (підроблений курсор -> 422), L2 (межа numeric(14,3)), L3 (гонка зміни одиниці з першим надходженням), L4 (термін надлишку в минулому),
/// Info (округлення сум до 2 знаків). Час - <see cref="FrozenTime"/>, не реальний годинник.
/// </summary>
public sealed class InventoryReviewFixApiTests(AuthApiFixture fx) : IClassFixture<AuthApiFixture>, IDisposable
{
    private readonly List<IDisposable> _disposables = [];
    private static readonly long NoonUnix = DateTimeOffset.Parse("2026-11-18T12:00:00Z").ToUnixTimeSeconds();

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
    private static Task<HttpResponseMessage> Op(HttpClient c, string path, string token, object body) =>
        Call(c, HttpMethod.Post, path, token, body, Guid.NewGuid().ToString());

    private static async Task<JsonElement> Ok(HttpResponseMessage r, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == expected, $"expected {expected}, got {(int)r.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<JsonElement> Fails(HttpResponseMessage r, HttpStatusCode status, string code)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == status, $"expected {status}, got {(int)r.StatusCode}: {body}");
        var root = JsonDocument.Parse(body).RootElement.Clone();
        Assert.Equal(code, root.GetProperty("code").GetString());
        return root;
    }

    private static async Task<Guid> NewProduct(HttpClient c, string token, string name, string unit = "pcs", decimal? price = null, decimal? minStock = null)
    {
        var r = await Call(c, HttpMethod.Post, "/products", token, new { name, unit, purpose = "both", salePrice = 10, trackExpiry = false, purchasePrice = price, minStock });
        return (await Ok(r, HttpStatusCode.Created)).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> Receive(HttpClient c, string token, Guid loc, Guid product, decimal qty, decimal? cost = null) =>
        await Ok(await Op(c, "/receipts", token, new { locationId = loc, lines = new[] { new { productId = product, quantity = qty, unitCost = cost } } }), HttpStatusCode.Created);

    private async Task SeedBatchAsync(Guid tenant, Guid loc, Guid product, decimal qty)
    {
        await using var ctx = fx.Db.CreateContext(tenant);
        var userId = await ctx.Users.AsNoTracking().Select(u => u.Id).FirstAsync();
        var now = DateTimeOffset.UtcNow.AddDays(-30);
        var op = new StockOperation { Id = Guid.NewGuid(), Kind = StockOperationKind.Receipt, LocationId = loc, CreatedByUserId = userId, CreatedAt = now };
        var batch = new StockBatch { Id = Guid.NewGuid(), LocationId = loc, ProductId = product, OperationId = op.Id, ReceivedAt = now, InitialQuantity = qty, Quantity = qty };
        ctx.StockOperations.Add(op);
        ctx.StockBatches.Add(batch);
        ctx.StockMovements.Add(new StockMovement { Id = Guid.NewGuid(), OperationId = op.Id, LocationId = loc, ProductId = product, BatchId = batch.Id, Type = StockMovementType.Receipt, Quantity = qty, CreatedByUserId = userId, OccurredAt = now });
        var level = await ctx.StockLevels.FirstOrDefaultAsync(l => l.LocationId == loc && l.ProductId == product);
        if (level is null) ctx.StockLevels.Add(new StockLevel { LocationId = loc, ProductId = product, Quantity = qty });
        else level.Quantity += qty;
        await ctx.SaveChangesAsync();
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
        foreach (var l in levels)
        {
            Assert.True(l.Quantity >= 0);
            Assert.Equal(l.Quantity, batches.Where(b => b.LocationId == l.LocationId && b.ProductId == l.ProductId).Sum(b => b.Quantity));
            Assert.Equal(l.Quantity, moves.Where(m => m.LocationId == l.LocationId && m.ProductId == l.ProductId).Sum(m => m.Quantity));
        }
    }

    private static string Hash(string cursor)
    {
        var b64 = cursor.Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(b64)).Split('|')[^1];
    }

    private static string Forge(string realCursor, params string[] parts) => Uri.EscapeDataString(Application.Features.BeautyInventory.InventoryService.EncodeCursor(Hash(realCursor), parts));

    // ---------- L1 ----------

    [SkippableFact]
    public async Task Forged_cursors_with_valid_hash_are_422_not_500_on_every_paged_endpoint()
    {
        NeedDb();
        var c = NewClient();
        var t = await NewTenantAsync();
        var a = await NewProduct(c, t.Owner, "A");
        var b = await NewProduct(c, t.Owner, "B");
        await Receive(c, t.Owner, t.L1, a, 1);
        await Receive(c, t.Owner, t.L1, b, 1);
        await Receive(c, t.Owner, t.L1, a, 1);

        const string Good = "00000000-0000-0000-0000-000000000001";
        var cases = new (string Path, string[] Garbage)[]
        {
            ("/products?take=1", ["x", "not-a-guid"]),
            ($"/stock?locationId={t.L1}&take=1", ["x", "not-a-guid"]),
            ("/stock/summary?take=1", ["x", "not-a-guid"]),
            ("/movements?take=1", ["abc", Good]),
            ("/movements?take=1", ["99999999999999999999", Good]),
            ("/operations?take=1", ["-1", Good]),
            ("/operations?take=1", ["1", "zzz"]),
            ("/batches?take=1", ["0", "", "1", Good]),
            ("/batches?take=1", ["0", "2026-99-99", "1", Good]),
            ("/batches?take=1", ["1", "", "abc", Good]),
        };
        foreach (var (path, garbage) in cases)
        {
            var page = await Ok(await Get(c, path, t.Owner));
            Assert.True(page.GetProperty("hasMore").GetBoolean(), path);
            var real = page.GetProperty("nextCursor").GetString()!;
            var sep = path.Contains('?') ? "&" : "?";
            await Fails(await Get(c, $"{path}{sep}cursor={Forge(real, garbage)}", t.Owner), HttpStatusCode.UnprocessableEntity, "invalid_cursor");
        }
    }

    // ---------- M1 ----------

    [SkippableFact]
    public async Task Stock_and_summary_page_and_filter_in_the_database_keeping_the_response_shape()
    {
        NeedDb();
        var c = NewClient();
        var t = await NewTenantAsync();
        var alpha = await NewProduct(c, t.Owner, "Alpha");
        var beta = await NewProduct(c, t.Owner, "Beta", minStock: 10);
        await NewProduct(c, t.Owner, "Gamma");
        await NewProduct(c, t.Owner, "100%_x");
        await Receive(c, t.Owner, t.L1, alpha, 5);
        await Receive(c, t.Owner, t.L1, beta, 3);

        static string[] Names(JsonElement page) => [.. page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("product").GetProperty("name").GetString()!)];
        string Stock(string q) => $"/stock?locationId={t.L1}{q}";

        // повний порядок: lower(name) Ordinal
        var all = await Ok(await Get(c, Stock(""), t.Owner));
        Assert.Equal(new[] { "100%_x", "alpha", "beta", "gamma" }, Names(all).Select(n => n.ToLowerInvariant()));

        // сторінки по 2 з'єднуються в той самий список без дублів
        var p1 = await Ok(await Get(c, Stock("&take=2"), t.Owner));
        Assert.True(p1.GetProperty("hasMore").GetBoolean());
        var p2 = await Ok(await Get(c, Stock("&take=2&cursor=" + Uri.EscapeDataString(p1.GetProperty("nextCursor").GetString()!)), t.Owner));
        Assert.False(p2.GetProperty("hasMore").GetBoolean());
        Assert.Equal(Names(all), [.. Names(p1), .. Names(p2)]);

        Assert.Equal(["Beta"], Names(await Ok(await Get(c, Stock("&state=low"), t.Owner))));
        Assert.Equal(["Alpha", "Beta"], Names(await Ok(await Get(c, Stock("&state=in_stock"), t.Owner))));
        Assert.Equal(["100%_x", "Gamma"], Names(await Ok(await Get(c, Stock("&state=out_of_stock"), t.Owner))));
        Assert.Equal(["100%_x"], Names(await Ok(await Get(c, Stock("&search=" + Uri.EscapeDataString("%_")), t.Owner)))); // метасимволи LIKE екрануються
        Assert.Equal(["Alpha"], Names(await Ok(await Get(c, Stock("&search=ALP"), t.Owner))));
        Assert.Empty(Names(await Ok(await Get(c, Stock("&purpose=nonsense"), t.Owner))));

        var low = (await Ok(await Get(c, Stock("&state=low"), t.Owner))).GetProperty("items")[0];
        Assert.Equal(3m, low.GetProperty("quantity").GetDecimal());
        Assert.Equal(10m, low.GetProperty("minStock").GetDecimal());
        Assert.True(low.GetProperty("isLow").GetBoolean());

        // зведення
        Assert.Equal(["Beta"], Names(await Ok(await Get(c, "/stock/summary?lowOnly=true", t.Owner))));
        var s1 = await Ok(await Get(c, "/stock/summary?take=3", t.Owner));
        Assert.True(s1.GetProperty("hasMore").GetBoolean());
        var s2 = await Ok(await Get(c, "/stock/summary?take=3&cursor=" + Uri.EscapeDataString(s1.GetProperty("nextCursor").GetString()!), t.Owner));
        Assert.Equal(["Gamma"], Names(s2));
        var a = s1.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("product").GetProperty("name").GetString() == "Alpha");
        Assert.Equal(5m, a.GetProperty("totalQuantity").GetDecimal());
        Assert.Equal(2, a.GetProperty("locations").GetArrayLength()); // обидва заклади, навіть з нульовим залишком
    }

    [SkippableFact]
    public async Task Product_list_flags_has_movements_for_the_page()
    {
        NeedDb();
        var c = NewClient();
        var t = await NewTenantAsync();
        var moved = await NewProduct(c, t.Owner, "A-moved");
        await NewProduct(c, t.Owner, "B-fresh");
        await Receive(c, t.Owner, t.L1, moved, 1);
        var items = (await Ok(await Get(c, "/products", t.Owner))).GetProperty("items").EnumerateArray().ToList();
        Assert.True(items.Single(i => i.GetProperty("name").GetString() == "A-moved").GetProperty("hasMovements").GetBoolean());
        Assert.False(items.Single(i => i.GetProperty("name").GetString() == "B-fresh").GetProperty("hasMovements").GetBoolean());
    }

    // ---------- Info: округлення ----------

    [SkippableFact]
    public async Task Total_cost_and_stock_value_are_rounded_to_two_decimals()
    {
        NeedDb();
        var c = NewClient();
        var t = await NewTenantAsync();
        var p = await NewProduct(c, t.Owner, "Ml", "ml");
        var op = await Receive(c, t.Owner, t.L1, p, 0.333m, cost: 1.01m); // 0.33633
        Assert.Equal(0.34m, op.GetProperty("totalCost").GetDecimal());
        var row = (await Ok(await Get(c, $"/stock?locationId={t.L1}", t.Owner))).GetProperty("items")[0];
        Assert.Equal(0.34m, row.GetProperty("value").GetDecimal());
        var sum = (await Ok(await Get(c, "/stock/summary", t.Owner))).GetProperty("items")[0];
        Assert.Equal(0.34m, sum.GetProperty("value").GetDecimal());
    }

    // ---------- L2 ----------

    [SkippableFact]
    public async Task Receipt_and_transfer_above_numeric_14_3_are_422_and_leave_state_unchanged()
    {
        NeedDb();
        var c = NewClient();
        var t = await NewTenantAsync();
        var p = await NewProduct(c, t.Owner, "Big", "ml");
        await SeedBatchAsync(t.Id, t.L1, p, 99_999_999_999m);

        var over = await Fails(await Op(c, "/receipts", t.Owner, new { locationId = t.L1, lines = new[] { new { productId = p, quantity = 1m } } }),
            HttpStatusCode.UnprocessableEntity, "quantity_out_of_range");
        Assert.Equal(0, over.GetProperty("line").GetInt32());
        Assert.Equal(99_999_999_999m, await LevelAsync(t.Id, t.L1, p));

        // кілька рядків одного товару: рахується сума, помилка на рядку, що перетнув межу
        var two = await Fails(await Op(c, "/receipts", t.Owner, new { locationId = t.L1, lines = new[] { new { productId = p, quantity = 0.5m }, new { productId = p, quantity = 0.5m } } }),
            HttpStatusCode.UnprocessableEntity, "quantity_out_of_range");
        Assert.Equal(1, two.GetProperty("line").GetInt32());

        await Receive(c, t.Owner, t.L1, p, 0.999m); // рівно до межі
        Assert.Equal(99_999_999_999.999m, await LevelAsync(t.Id, t.L1, p));
        await Fails(await Op(c, "/receipts", t.Owner, new { locationId = t.L1, lines = new[] { new { productId = p, quantity = 0.001m } } }),
            HttpStatusCode.UnprocessableEntity, "quantity_out_of_range");

        // переміщення в заклад, де рівень біля межі
        var q = await NewProduct(c, t.Owner, "Big2", "ml");
        await SeedBatchAsync(t.Id, t.Loc2, q, 99_999_999_999m);
        await SeedBatchAsync(t.Id, t.L1, q, 5m);
        await Fails(await Op(c, "/transfers", t.Owner, new { fromLocationId = t.L1, toLocationId = t.Loc2, lines = new[] { new { productId = q, quantity = 1m } } }),
            HttpStatusCode.UnprocessableEntity, "quantity_out_of_range");
        Assert.Equal(5m, await LevelAsync(t.Id, t.L1, q));
        Assert.Equal(99_999_999_999m, await LevelAsync(t.Id, t.Loc2, q));
        await AssertInvariantsAsync(t.Id);
    }

    // ---------- L3 ----------

    [SkippableFact]
    public async Task Unit_change_racing_with_the_first_receipt_never_leaves_fractional_stock_on_an_integer_unit()
    {
        NeedDb();
        var c = NewClient();
        var t = await NewTenantAsync();
        for (var round = 0; round < 8; round++)
        {
            var p = await NewProduct(c, t.Owner, "Race" + round, "ml");
            var receipt = Op(c, "/receipts", t.Owner, new { locationId = t.L1, lines = new[] { new { productId = p, quantity = 0.5m } } });
            var change = Call(c, HttpMethod.Put, $"/products/{p}", t.Owner, new { name = "Race" + round, unit = "pcs", purpose = "both", salePrice = 10, trackExpiry = false });
            var rs = await Task.WhenAll(receipt, change);

            var received = rs[0].StatusCode == HttpStatusCode.Created;
            var changed = rs[1].StatusCode == HttpStatusCode.OK;
            Assert.True(received ^ changed, $"round {round}: receipt={(int)rs[0].StatusCode} change={(int)rs[1].StatusCode}");
            if (!received) await Fails(rs[0], HttpStatusCode.UnprocessableEntity, "invalid_quantity");
            if (!changed) await Fails(rs[1], HttpStatusCode.UnprocessableEntity, "unit_locked");
            Assert.Equal(received ? 0.5m : 0m, await LevelAsync(t.Id, t.L1, p));
        }
        await AssertInvariantsAsync(t.Id);
    }

    // ---------- L4 ----------

    [SkippableFact]
    public async Task Stocktake_surplus_expiry_in_the_past_is_422_like_receipt_and_today_is_accepted()
    {
        NeedDb();
        var c = NewClient(new FrozenTime(NoonUnix)); // 18.11.2026 14:00 за Києвом
        var t = await NewTenantAsync();
        var p = await NewProduct(c, t.Owner, "Surplus");

        var st = await Ok(await Call(c, HttpMethod.Post, "/stocktakes", t.Owner, new { locationId = t.L1, productIds = new[] { p } }), HttpStatusCode.Created);
        var id = st.GetProperty("id").GetGuid();
        var version = st.GetProperty("version").GetInt32();

        var put = await Ok(await Call(c, HttpMethod.Put, $"/stocktakes/{id}/lines", t.Owner,
            new { expectedVersion = version, lines = new[] { new { productId = p, countedQuantity = 4, surplusExpiresOn = "2026-11-17" } } }));
        version = put.GetProperty("version").GetInt32();
        var bad = await Fails(await Call(c, HttpMethod.Post, $"/stocktakes/{id}/post", t.Owner, new { expectedVersion = version }), HttpStatusCode.UnprocessableEntity, "invalid_expiry");
        Assert.Equal(p, bad.GetProperty("productId").GetGuid());
        Assert.Equal(0m, await LevelAsync(t.Id, t.L1, p)); // відкат: нічого не записано

        put = await Ok(await Call(c, HttpMethod.Put, $"/stocktakes/{id}/lines", t.Owner,
            new { expectedVersion = version, lines = new[] { new { productId = p, countedQuantity = 4, surplusExpiresOn = "2026-11-18" } } }));
        version = put.GetProperty("version").GetInt32();
        await Ok(await Call(c, HttpMethod.Post, $"/stocktakes/{id}/post", t.Owner, new { expectedVersion = version }));
        Assert.Equal(4m, await LevelAsync(t.Id, t.L1, p));
        await AssertInvariantsAsync(t.Id);
    }
}
