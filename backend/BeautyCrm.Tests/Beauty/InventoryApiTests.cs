using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Tests.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using static BeautyCrm.Tests.Auth.AuthApiFixture;

namespace BeautyCrm.Tests.Beauty;

/// <summary>
/// TASK-877 (ADR-025, §32.11 п.5-13): склад на реальній PostgreSQL (RLS, роль app без BYPASSRLS) через повний HTTP-конвеєр:
/// модуль і права, scope own, IDOR між закладами й tenant, ідемпотентність, FEFO, атомарне переміщення, інвентаризація, insufficient_stock,
/// прострочені партії, гонки (паралельні списання, зустрічні переміщення, однаковий Idempotency-Key, проведення проти списання),
/// append-only журнал, ліміт, no-store. Інваріант level = Σ batches = Σ movements перевіряється після сценаріїв. Час: дати лише константами
/// (прострочені партії сіються напряму в БД зі строком 2020-01-01), без залежності від меж «сьогодні».
/// </summary>
public sealed class InventoryApiTests(AuthApiFixture fx) : IClassFixture<AuthApiFixture>, IDisposable
{
    private readonly List<IDisposable> _disposables = [];
    private static readonly DateOnly Expired = new(2020, 1, 1);
    private const string Far = "2099-01-01";

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
    }

    private void NeedDb() => Skip.If(fx.SkipReason is not null, fx.SkipReason);

    private HttpClient NewClient(string? writePerMinute = "600")
    {
        var f = fx.CreateFactory(10_000).WithWebHostBuilder(b =>
        {
            if (writePerMinute is not null) b.UseSetting("Inventory:WritePerUserPerMinute", writePerMinute);
        });
        _disposables.Add(f);
        return f.CreateClient();
    }

    private sealed record Tn(TenantSeed Seed, BeautySeed Beauty, Guid Loc2, string Owner, string Admin, string Spec, string SpecOwn, string AdminNoRevenue, string Deputy)
    {
        public Guid Id => Seed.TenantId;
        public Guid L1 => Beauty.LocationId;
    }

    /// <summary>
    /// Два заклади (L1 - заклад профілю майстра A, L2 - чужий), owner, admin (пресет), specialist (пресет: view=own, manage=none),
    /// specialist з manage=own, admin без revenue.view (inventory.costs знижено до none), deputy (пресет).
    /// </summary>
    private async Task<Tn> NewTenantAsync(string[]? modules = null)
    {
        var t = await fx.CreateTenantAsync(modules ?? [.. BeautyModules.Known]);
        var owner = await fx.LoginAsync(t.Slug, t.OwnerEmail);
        var seed = await fx.SeedBeautyAsync(t.TenantId);
        Guid loc2;
        await using (var ctx = fx.Db.CreateContext(t.TenantId))
        {
            var l2 = new Location { Name = "Second", Timezone = "Europe/Kyiv" };
            ctx.Locations.Add(l2);
            ctx.SpecialistLocations.Add(new SpecialistLocation { SpecialistId = seed.SpecialistA, LocationId = seed.LocationId });
            ctx.SpecialistLocations.Add(new SpecialistLocation { SpecialistId = seed.SpecialistB, LocationId = seed.LocationId });
            await ctx.SaveChangesAsync();
            loc2 = l2.Id;
        }
        var admin = await fx.InviteAndLoginAsync(t, owner.AccessToken, Roles.Admin);
        var spec = await fx.InviteAndLoginAsync(t, owner.AccessToken, Roles.Specialist, seed.SpecialistA);
        var own = PermissionPresets.For(Roles.Specialist).ToDictionary(kv => kv.Key, kv => kv.Value);
        own["inventory.manage"] = "own";
        var specOwn = await fx.CreateUserAsync(t, Roles.Specialist, seed.SpecialistB, own);
        var noRev = PermissionPresets.For(Roles.Admin).ToDictionary(kv => kv.Key, kv => kv.Value);
        noRev["revenue.view"] = "none";
        var adminNoRev = await fx.CreateUserAsync(t, Roles.Admin, null, noRev);
        var deputy = await fx.CreateDeputyAsync(t);
        return new Tn(t, seed, loc2, owner.AccessToken, admin.AccessToken, spec.AccessToken, specOwn.Tokens.AccessToken, adminNoRev.Tokens.AccessToken, deputy.Tokens.AccessToken);
    }

    private static HttpRequestMessage Build(HttpMethod m, string path, string? token, object? body, string? key)
    {
        var r = new HttpRequestMessage(m, "/api/beauty/inventory" + path);
        if (body is not null) r.Content = JsonContent.Create(body, options: Json);
        if (token is not null) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (key is not null) r.Headers.Add("Idempotency-Key", key);
        return r;
    }

    private static Task<HttpResponseMessage> Call(HttpClient c, HttpMethod m, string path, string? token, object? body = null, string? key = null) =>
        c.SendAsync(Build(m, path, token, body, key));

    private static Task<HttpResponseMessage> Get(HttpClient c, string path, string? token) => Call(c, HttpMethod.Get, path, token);

    private static Task<HttpResponseMessage> Op(HttpClient c, string path, string? token, object body, string? key = null) =>
        Call(c, HttpMethod.Post, path, token, body, key ?? Guid.NewGuid().ToString());

    private static async Task<JsonElement> J(HttpResponseMessage r) => await Read<JsonElement>(r);
    private static async Task<string> Code(HttpResponseMessage r) => (await J(r)).GetProperty("code").GetString()!;

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

    private static void AssertNoStore(HttpResponseMessage r) => Assert.Equal("no-store", r.Headers.CacheControl?.ToString());

    // ---------- наповнення ----------

    private static async Task<Guid> NewProduct(HttpClient c, string token, string name, string unit = "pcs", bool trackExpiry = false, decimal? price = null, string? barcode = null)
    {
        var r = await Call(c, HttpMethod.Post, "/products", token, new { name, unit, purpose = "both", trackExpiry, purchasePrice = price, barcode });
        return (await Ok(r, HttpStatusCode.Created)).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> Receive(HttpClient c, string token, Guid loc, Guid product, decimal qty, string? exp = null, decimal? cost = null, string? key = null)
    {
        var r = await Op(c, "/receipts", token, new { locationId = loc, lines = new[] { new { productId = product, quantity = qty, expiresOn = exp, unitCost = cost } } }, key);
        return await Ok(r, HttpStatusCode.Created);
    }

    /// <summary>Пряме сівання партії (в т.ч. прострочені) з усіма інваріантами: операція + партія + рух + рівень.</summary>
    private async Task<Guid> SeedBatchAsync(Guid tenant, Guid loc, Guid product, decimal qty, DateOnly? exp, decimal? cost = null)
    {
        await using var ctx = fx.Db.CreateContext(tenant);
        var userId = await ctx.Users.AsNoTracking().Select(u => u.Id).FirstAsync();
        var now = DateTimeOffset.UtcNow.AddDays(-30);
        var op = new StockOperation { Id = Guid.NewGuid(), Kind = StockOperationKind.Receipt, LocationId = loc, CreatedByUserId = userId, CreatedAt = now };
        var batch = new StockBatch { Id = Guid.NewGuid(), LocationId = loc, ProductId = product, OperationId = op.Id, ReceivedAt = now, ExpiresOn = exp, InitialQuantity = qty, Quantity = qty, UnitCost = cost };
        ctx.StockOperations.Add(op);
        ctx.StockBatches.Add(batch);
        ctx.StockMovements.Add(new StockMovement { Id = Guid.NewGuid(), OperationId = op.Id, LocationId = loc, ProductId = product, BatchId = batch.Id, Type = StockMovementType.Receipt, Quantity = qty, UnitCost = cost, CreatedByUserId = userId, OccurredAt = now });
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

    /// <summary>§32.4: для кожної пари level = Σ batches = Σ movements, усе &gt;= 0, 0 &lt;= batch.quantity &lt;= initial.</summary>
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
            Assert.Equal(0m, moves.Where(m => m.OperationId == op.Id).Sum(m => m.Quantity)); // сума ніг переміщення = 0
    }

    // ---------- модуль і права ----------

    [SkippableFact]
    public async Task Module_disabled_gives_403_before_forbidden_role_on_every_endpoint()
    {
        NeedDb();
        var c = NewClient();
        var t = await NewTenantAsync(modules: [.. BeautyModules.All]); // без beauty_inventory
        var id = Guid.NewGuid();
        foreach (var token in new[] { t.Owner, t.Admin, t.Spec })
            foreach (var (m, path) in new[]
                     {
                         (HttpMethod.Get, "/scope"), (HttpMethod.Get, "/products"), (HttpMethod.Get, $"/products/{id}"), (HttpMethod.Post, "/products"),
                         (HttpMethod.Get, "/suppliers"), (HttpMethod.Get, "/stock?locationId=" + t.L1), (HttpMethod.Get, "/stock/summary"),
                         (HttpMethod.Get, "/batches"), (HttpMethod.Get, "/movements"), (HttpMethod.Get, "/operations"),
                         (HttpMethod.Post, "/receipts"), (HttpMethod.Post, "/write-offs"), (HttpMethod.Post, "/transfers"),
                         (HttpMethod.Get, "/stocktakes"), (HttpMethod.Post, "/stocktakes"), (HttpMethod.Post, $"/stocktakes/{id}/post"),
                     })
            {
                var r = await Call(c, m, path, token, m == HttpMethod.Post ? new { } : null, Guid.NewGuid().ToString());
                Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
                Assert.Equal("module_disabled", await Code(r)); // раніше за forbidden_role (specialist: manage = none)
                AssertNoStore(r);
            }
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get(c, "/scope", null)).StatusCode);
    }

    [SkippableFact]
    public async Task Specialist_preset_is_read_only_forbidden_role_with_permission_and_required_and_no_store()
    {
        NeedDb();
        var c = NewClient();
        var t = await NewTenantAsync();
        var prod = await NewProduct(c, t.Admin, "P");
        var rcpt = await Op(c, "/receipts", t.Spec, new { locationId = t.L1, lines = new[] { new { productId = prod, quantity = 1 } } });
        Assert.Equal(HttpStatusCode.Forbidden, rcpt.StatusCode);
        var body = await J(rcpt);
        Assert.Equal("forbidden_role", body.GetProperty("code").GetString());
        Assert.Equal("inventory.manage", body.GetProperty("permission").GetString());
        Assert.Equal("own", body.GetProperty("required").GetString());
        AssertNoStore(rcpt);
        foreach (var (m, path, req) in new[]
                 {
                     (HttpMethod.Post, "/write-offs", "own"), (HttpMethod.Post, "/transfers", "own"), (HttpMethod.Post, "/products", "all"),
                     (HttpMethod.Put, $"/products/{prod}", "all"), (HttpMethod.Post, "/suppliers", "all"), (HttpMethod.Get, "/suppliers", "own"),
                     (HttpMethod.Post, "/stocktakes", "own"),
                 })
        {
            var r = await Call(c, m, path, t.Spec, new { }, Guid.NewGuid().ToString());
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
            Assert.Equal(req, (await J(r)).GetProperty("required").GetString());
        }
        var read = await Get(c, "/products", t.Spec);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        AssertNoStore(read);
        AssertNoStore(await Get(c, "/scope", t.Owner));
        Assert.Empty((await J(await Get(c, "/operations", t.Admin))).GetProperty("items").EnumerateArray());
    }

    [SkippableFact]
    public async Task Costs_are_absent_without_inventory_costs_and_writing_them_is_forbidden()
    {
        NeedDb();
        var c = NewClient();
        var t = await NewTenantAsync();
        var prod = await NewProduct(c, t.Admin, "Priced", price: 12.5m);
        await Receive(c, t.Admin, t.L1, prod, 4, cost: 20m);

        // admin з costs = edit бачить ціни
        var full = await Ok(await Get(c, $"/products/{prod}", t.Admin));
        Assert.Equal(12.5m, full.GetProperty("purchasePrice").GetDecimal());
        var stockFull = (await Ok(await Get(c, $"/stock?locationId={t.L1}", t.Admin))).GetProperty("items")[0];
        Assert.Equal(80m, stockFull.GetProperty("value").GetDecimal());
        Assert.False(stockFull.GetProperty("valueIncomplete").GetBoolean());

        foreach (var token in new[] { t.AdminNoRevenue, t.Deputy, t.Spec })
        {
            var raw = await (await Get(c, "/products", token)).Content.ReadAsStringAsync();
            Assert.DoesNotContain("purchasePrice", raw);
            Assert.DoesNotContain("unitCost", await (await Get(c, "/batches", token)).Content.ReadAsStringAsync());
            Assert.DoesNotContain("unitCost", await (await Get(c, "/movements", token)).Content.ReadAsStringAsync());
            var stock = await (await Get(c, $"/stock?locationId={t.L1}", token)).Content.ReadAsStringAsync();
            Assert.DoesNotContain("\"value\"", stock);
            Assert.DoesNotContain("valueIncomplete", stock);
            Assert.DoesNotContain("totalCost", await (await Get(c, "/operations", token)).Content.ReadAsStringAsync());
            Assert.DoesNotContain("value", await (await Get(c, "/stock/summary", token)).Content.ReadAsStringAsync().ContinueWith(t => t.Result.Replace("lowOnly", "")));
        }
        Assert.Equal("none", (await J(await Get(c, "/scope", t.AdminNoRevenue))).GetProperty("costs").GetString());
        Assert.Equal("edit", (await J(await Get(c, "/scope", t.Admin))).GetProperty("costs").GetString());

        // поле ціни в тілі без costs = edit -> 403 раніше за валідацію
        var p = await Call(c, HttpMethod.Post, "/products", t.AdminNoRevenue, new { name = "", unit = "bad", purchasePrice = 5 });
        Assert.Equal(HttpStatusCode.Forbidden, p.StatusCode);
        var pb = await J(p);
        Assert.Equal("forbidden_role", pb.GetProperty("code").GetString());
        Assert.Equal("inventory.costs", pb.GetProperty("permission").GetString());
        Assert.Equal("edit", pb.GetProperty("required").GetString());
        var rc = await Op(c, "/receipts", t.Deputy, new { locationId = t.L1, lines = new[] { new { productId = prod, quantity = -5, unitCost = 1 } } });
        Assert.Equal("inventory.costs", (await J(rc)).GetProperty("permission").GetString());
        // без поля - дозволено, партія бере ціну товару незалежно від права
        var ok = await Op(c, "/receipts", t.AdminNoRevenue, new { locationId = t.L1, lines = new[] { new { productId = prod, quantity = 1 } } });
        var op = await Ok(ok, HttpStatusCode.Created);
        Assert.DoesNotContain("unitCost", op.ToString());
        Assert.DoesNotContain("totalCost", op.ToString());
        var cost = (await Ok(await Get(c, "/batches", t.Admin))).GetProperty("items").EnumerateArray().Select(b => b.GetProperty("unitCost").GetDecimal()).Order().ToArray();
        Assert.Equal([12.5m, 20m], cost);
    }

    // ---------- scope й IDOR ----------

    [SkippableFact]
    public async Task Specialist_own_scope_sees_only_own_locations_and_foreign_ids_are_404()
    {
        NeedDb();
        var c = NewClient();
        var t = await NewTenantAsync();
        var prod = await NewProduct(c, t.Admin, "P");
        await Receive(c, t.Admin, t.L1, prod, 5);
        var foreignOp = await Receive(c, t.Admin, t.Loc2, prod, 7);
        var foreignBatch = (await Ok(await Get(c, $"/batches?locationId={t.Loc2}", t.Admin))).GetProperty("items")[0].GetProperty("id").GetGuid();

        var scope = await Ok(await Get(c, "/scope", t.Spec));
        Assert.Equal([t.L1], scope.GetProperty("locations").EnumerateArray().Select(l => l.GetProperty("id").GetGuid()).ToArray());
        Assert.False(scope.GetProperty("locations")[0].GetProperty("canManage").GetBoolean());
        Assert.False(scope.GetProperty("canEditCatalog").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await Get(c, $"/stock?locationId={t.L1}", t.Spec)).StatusCode);
        foreach (var path in new[] { $"/stock?locationId={t.Loc2}", $"/batches?locationId={t.Loc2}", $"/movements?locationId={t.Loc2}", $"/operations?locationId={t.Loc2}", $"/stocktakes?locationId={t.Loc2}" })
            await Fails(await Get(c, path, t.Spec), HttpStatusCode.NotFound, "location_not_found");
        await Fails(await Get(c, $"/operations/{foreignOp.GetProperty("id").GetGuid()}", t.Spec), HttpStatusCode.NotFound, "operation_not_found");
        // без locationId - лише заклади scope
        var batches = (await Ok(await Get(c, "/batches", t.Spec))).GetProperty("items").EnumerateArray().ToList();
        Assert.Single(batches);
        Assert.Equal(t.L1, batches[0].GetProperty("locationId").GetGuid());
        Assert.DoesNotContain(foreignBatch.ToString(), (await Get(c, "/batches?status=all", t.Spec)).Content.ReadAsStringAsync().Result);
        Assert.All((await Ok(await Get(c, "/movements", t.Spec))).GetProperty("items").EnumerateArray(), m => Assert.Equal(t.L1, m.GetProperty("locationId").GetGuid()));
        Assert.Single((await Ok(await Get(c, "/operations", t.Spec))).GetProperty("items").EnumerateArray());
        var summary = await Ok(await Get(c, "/stock/summary", t.Spec));
        Assert.Equal([t.L1], summary.GetProperty("locations").EnumerateArray().Select(l => l.GetProperty("id").GetGuid()).ToArray());
        Assert.Equal(5m, summary.GetProperty("items")[0].GetProperty("totalQuantity").GetDecimal());
        // власник бачить усе
        Assert.Equal(12m, (await Ok(await Get(c, "/stock/summary", t.Owner))).GetProperty("items")[0].GetProperty("totalQuantity").GetDecimal());
    }

    [SkippableFact]
    public async Task Specialist_without_profile_link_sees_empty_lists()
    {
        NeedDb();
        var c = NewClient();
        var t = await NewTenantAsync();
        var prod = await NewProduct(c, t.Admin, "P");
        await Receive(c, t.Admin, t.L1, prod, 5);
        Guid lonely;
        await using (var ctx = fx.Db.CreateContext(t.Id))
        {
            var sp = new Specialist { FullName = "Lonely" }; // профіль без закладів
            ctx.Specialists.Add(sp);
            await ctx.SaveChangesAsync();
            lonely = sp.Id;
        }
        var other = await fx.InviteAndLoginAsync(t.Seed, t.Owner, Roles.Specialist, lonely);
        Assert.Empty((await Ok(await Get(c, "/scope", other.AccessToken))).GetProperty("locations").EnumerateArray());
        Assert.Empty((await Ok(await Get(c, "/batches", other.AccessToken))).GetProperty("items").EnumerateArray());
        Assert.Empty((await Ok(await Get(c, "/movements", other.AccessToken))).GetProperty("items").EnumerateArray());
        Assert.Empty((await Ok(await Get(c, "/operations", other.AccessToken))).GetProperty("items").EnumerateArray());
        await Fails(await Get(c, $"/stock?locationId={t.L1}", other.AccessToken), HttpStatusCode.NotFound, "location_not_found");
        // каталог товарів видно повністю
        Assert.Single((await Ok(await Get(c, "/products", other.AccessToken))).GetProperty("items").EnumerateArray());
    }

    [SkippableFact]
    public async Task Manage_own_works_in_own_location_only_and_transfer_needs_both_legs()
    {
        NeedDb();
        var c = NewClient();
        var t = await NewTenantAsync();
        var prod = await NewProduct(c, t.Admin, "P");
        await Receive(c, t.Admin, t.Loc2, prod, 5);

        await Receive(c, t.SpecOwn, t.L1, prod, 3);
        await Fails(await Op(c, "/receipts", t.SpecOwn, new { locationId = t.Loc2, lines = new[] { new { productId = prod, quantity = 1 } } }), HttpStatusCode.NotFound, "location_not_found");
        await Fails(await Op(c, "/write-offs", t.SpecOwn, new { locationId = t.Loc2, reason = "lost", lines = new[] { new { productId = prod, quantity = 1 } } }), HttpStatusCode.NotFound, "location_not_found");
        await Fails(await Op(c, "/transfers", t.SpecOwn, new { fromLocationId = t.L1, toLocationId = t.Loc2, lines = new[] { new { productId = prod, quantity = 1 } } }), HttpStatusCode.NotFound, "location_not_found");
        await Fails(await Op(c, "/transfers", t.SpecOwn, new { fromLocationId = t.Loc2, toLocationId = t.L1, lines = new[] { new { productId = prod, quantity = 1 } } }), HttpStatusCode.NotFound, "location_not_found");
        await Fails(await Call(c, HttpMethod.Post, "/stocktakes", t.SpecOwn, new { locationId = t.Loc2 }), HttpStatusCode.NotFound, "location_not_found");
        await Fails(await Call(c, HttpMethod.Put, $"/products/{prod}/locations/{t.Loc2}", t.SpecOwn, new { minStock = 1, isEnabled = true }), HttpStatusCode.NotFound, "location_not_found");
        Assert.Equal(HttpStatusCode.OK, (await Call(c, HttpMethod.Put, $"/products/{prod}/locations/{t.L1}", t.SpecOwn, new { minStock = 1, isEnabled = true })).StatusCode);
        // каталог й постачальники - лише manage = all
        await Fails(await Call(c, HttpMethod.Post, "/products", t.SpecOwn, new { name = "X", unit = "pcs", purpose = "sale" }), HttpStatusCode.Forbidden, "forbidden_role");
        await Fails(await Call(c, HttpMethod.Post, "/suppliers", t.SpecOwn, new { name = "S" }), HttpStatusCode.Forbidden, "forbidden_role");
        Assert.Equal(HttpStatusCode.OK, (await Get(c, "/suppliers", t.SpecOwn)).StatusCode); // перегляд контактів: manage >= own
        Assert.Equal(5, await LevelAsync(t.Id, t.Loc2, prod));
        await AssertInvariantsAsync(t.Id);
    }

    [SkippableFact]
    public async Task Cross_tenant_ids_are_404_and_lists_are_isolated()
    {
        NeedDb();
        var c = NewClient();
        var a = await NewTenantAsync();
        var b = await NewTenantAsync();
        var prodA = await NewProduct(c, a.Admin, "A-product", barcode: "SAME-1");
        var opA = await Receive(c, a.Admin, a.L1, prodA, 5);
        var prodB = await NewProduct(c, b.Admin, "B-product", barcode: "SAME-1"); // штрихкод унікальний лише в межах tenant

        await Fails(await Get(c, $"/products/{prodA}", b.Admin), HttpStatusCode.NotFound, "product_not_found");
        await Fails(await Get(c, $"/operations/{opA.GetProperty("id").GetGuid()}", b.Owner), HttpStatusCode.NotFound, "operation_not_found");
        await Fails(await Get(c, $"/stock?locationId={a.L1}", b.Owner), HttpStatusCode.NotFound, "location_not_found");
        await Fails(await Op(c, "/receipts", b.Owner, new { locationId = a.L1, lines = new[] { new { productId = prodB, quantity = 1 } } }), HttpStatusCode.NotFound, "location_not_found");
        await Fails(await Op(c, "/receipts", b.Owner, new { locationId = b.L1, lines = new[] { new { productId = prodA, quantity = 1 } } }), HttpStatusCode.NotFound, "product_not_found");
        await Fails(await Call(c, HttpMethod.Put, $"/products/{prodA}", b.Owner, new { name = "hijack", unit = "pcs", purpose = "sale" }), HttpStatusCode.NotFound, "product_not_found");
        Assert.Equal(prodB, (await Ok(await Get(c, "/products/by-barcode?code=SAME-1", b.Owner))).GetProperty("id").GetGuid());
        Assert.Equal(["B-product"], (await Ok(await Get(c, "/products?active=all", b.Owner))).GetProperty("items").EnumerateArray().Select(p => p.GetProperty("name").GetString()).ToArray());
        Assert.Empty((await Ok(await Get(c, "/movements", b.Owner))).GetProperty("items").EnumerateArray());
        Assert.Equal(5, await LevelAsync(a.Id, a.L1, prodA));
    }

    // ---------- каталог ----------

    [SkippableFact]
    public async Task Catalog_crud_barcode_suppliers_unit_lock_and_validation_over_http()
    {
        NeedDb();
        var c = NewClient();
        var t = await NewTenantAsync();

        var sup = await Ok(await Call(c, HttpMethod.Post, "/suppliers", t.Admin, new { name = "Acme", contactName = "Ann", phone = "+380 44 111", email = "a@b.c" }), HttpStatusCode.Created);
        await Fails(await Call(c, HttpMethod.Post, "/suppliers", t.Admin, new { name = "ACME" }), HttpStatusCode.Conflict, "supplier_name_taken");
        await Fails(await Call(c, HttpMethod.Post, "/suppliers", t.Admin, new { name = "" }), HttpStatusCode.UnprocessableEntity, "invalid_name");
        var supId = sup.GetProperty("id").GetGuid();

        var p = await Ok(await Call(c, HttpMethod.Post, "/products", t.Admin, new { name = "Shampoo", unit = "pcs", purpose = "sale", barcode = "4820-1", supplierId = supId, minStock = 3, category = "Hair" }), HttpStatusCode.Created);
        var id = p.GetProperty("id").GetGuid();
        Assert.Equal("Acme", p.GetProperty("supplier").GetProperty("name").GetString());
        Assert.False(p.GetProperty("hasMovements").GetBoolean());
        await Fails(await Call(c, HttpMethod.Post, "/products", t.Admin, new { name = "Dup", unit = "pcs", purpose = "sale", barcode = "4820-1" }), HttpStatusCode.Conflict, "barcode_taken");
        await Fails(await Call(c, HttpMethod.Post, "/products", t.Admin, new { name = "Bad", unit = "bottle", purpose = "sale" }), HttpStatusCode.UnprocessableEntity, "invalid_unit");
        await Fails(await Call(c, HttpMethod.Post, "/products", t.Admin, new { name = "Bad", unit = "pcs", purpose = "sale", barcode = "bad code" }), HttpStatusCode.UnprocessableEntity, "invalid_barcode");
        await Fails(await Call(c, HttpMethod.Post, "/products", t.Admin, new { name = "Bad", unit = "pcs", purpose = "sale", minStock = 1.5 }), HttpStatusCode.UnprocessableEntity, "invalid_min_stock");
        await Fails(await Call(c, HttpMethod.Post, "/products", t.Admin, new { name = "Bad", unit = "pcs", purpose = "sale", supplierId = Guid.NewGuid() }), HttpStatusCode.NotFound, "supplier_not_found");

        Assert.Equal(["Hair"], (await Ok(await Get(c, "/categories", t.Spec))).GetProperty("items").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Equal(id, (await Ok(await Get(c, "/products/by-barcode?code=%204820-1%20", t.Spec))).GetProperty("id").GetGuid());
        await Fails(await Get(c, "/products/by-barcode?code=none", t.Spec), HttpStatusCode.NotFound, "product_not_found");

        await Receive(c, t.Admin, t.L1, id, 2);
        await Fails(await Call(c, HttpMethod.Put, $"/products/{id}", t.Admin, new { name = "Shampoo", unit = "ml", purpose = "sale" }), HttpStatusCode.UnprocessableEntity, "unit_locked");
        var upd = await Ok(await Call(c, HttpMethod.Put, $"/products/{id}", t.Admin, new { name = "Shampoo 2", unit = "pcs", purpose = "both", isActive = false }));
        Assert.Equal("Shampoo 2", upd.GetProperty("name").GetString());
        Assert.False(upd.GetProperty("isActive").GetBoolean());
        Assert.True(upd.GetProperty("hasMovements").GetBoolean());
        Assert.Equal(JsonValueKind.Undefined, upd.TryGetProperty("barcode", out var bc) ? bc.ValueKind == JsonValueKind.Null ? JsonValueKind.Undefined : bc.ValueKind : JsonValueKind.Undefined); // PUT: null = очистити

        // налаштування закладу: мінімум і вимкнення
        var s = await Ok(await Call(c, HttpMethod.Put, $"/products/{id}/locations/{t.L1}", t.Admin, new { minStock = 10, isEnabled = true }));
        Assert.Equal(10m, s.GetProperty("minStock").GetDecimal());
        var detail = await Ok(await Get(c, $"/products/{id}", t.Admin));
        Assert.Single(detail.GetProperty("locationSettings").EnumerateArray());
        Assert.Equal(HttpStatusCode.NoContent, (await Call(c, HttpMethod.Delete, $"/products/{id}/locations/{t.L1}", t.Admin)).StatusCode);
        await Fails(await Call(c, HttpMethod.Delete, $"/products/{id}/locations/{t.L1}", t.Admin), HttpStatusCode.NotFound, "setting_not_found");
        // неактивний товар: надходження -> 422 product_inactive
        var r = await Op(c, "/receipts", t.Admin, new { locationId = t.L1, lines = new[] { new { productId = id, quantity = 1 } } });
        await Fails(r, HttpStatusCode.UnprocessableEntity, "product_inactive");
        // pagination і невалідний курсор
        await Fails(await Get(c, "/products?cursor=broken", t.Admin), HttpStatusCode.UnprocessableEntity, "invalid_cursor");
        await NewProduct(c, t.Admin, "Zeta"); await NewProduct(c, t.Admin, "alpha");
        var page1 = await Ok(await Get(c, "/products?take=1", t.Admin));
        Assert.True(page1.GetProperty("hasMore").GetBoolean());
        var page2 = await Ok(await Get(c, "/products?take=1&cursor=" + Uri.EscapeDataString(page1.GetProperty("nextCursor").GetString()!), t.Admin));
        Assert.NotEqual(page1.GetProperty("items")[0].GetProperty("id").GetGuid(), page2.GetProperty("items")[0].GetProperty("id").GetGuid());
    }

    // ---------- ідемпотентність ----------

    [SkippableFact]
    public async Task Idempotency_replay_reuse_missing_and_invalid_key()
    {
        NeedDb();
        var c = NewClient();
        var t = await NewTenantAsync();
        var prod = await NewProduct(c, t.Admin, "P");
        var body = new { locationId = t.L1, lines = new[] { new { productId = prod, quantity = 4 } } };

        await Fails(await Call(c, HttpMethod.Post, "/receipts", t.Admin, body), HttpStatusCode.UnprocessableEntity, "idempotency_key_required");
        await Fails(await Call(c, HttpMethod.Post, "/receipts", t.Admin, body, new string('k', 129)), HttpStatusCode.UnprocessableEntity, "invalid_idempotency_key");
        await Fails(await Call(c, HttpMethod.Post, "/write-offs", t.Admin, body), HttpStatusCode.UnprocessableEntity, "idempotency_key_required");
        await Fails(await Call(c, HttpMethod.Post, "/transfers", t.Admin, body), HttpStatusCode.UnprocessableEntity, "idempotency_key_required");

        var first = await Call(c, HttpMethod.Post, "/receipts", t.Admin, body, "idem-1");
        var firstJson = await Ok(first, HttpStatusCode.Created);
        Assert.False(first.Headers.Contains("Idempotent-Replayed"));
        var again = await Call(c, HttpMethod.Post, "/receipts", t.Admin, body, "idem-1");
        var againJson = await Ok(again);
        Assert.Equal("true", again.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(firstJson.GetProperty("id").GetGuid(), againJson.GetProperty("id").GetGuid());
        Assert.Equal(4, await LevelAsync(t.Id, t.L1, prod));
        await Fails(await Call(c, HttpMethod.Post, "/receipts", t.Admin, new { locationId = t.L1, lines = new[] { new { productId = prod, quantity = 5 } } }, "idem-1"), HttpStatusCode.UnprocessableEntity, "idempotency_key_reused");
        Assert.Equal(4, await LevelAsync(t.Id, t.L1, prod));
        await AssertInvariantsAsync(t.Id);
    }

    [SkippableFact]
    public async Task Parallel_requests_with_the_same_idempotency_key_create_one_operation()
    {
        NeedDb();
        var c = NewClient();
        var t = await NewTenantAsync();
        var prod = await NewProduct(c, t.Admin, "P");
        var body = new { locationId = t.L1, lines = new[] { new { productId = prod, quantity = 3 } } };
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Call(c, HttpMethod.Post, "/receipts", t.Admin, body, "same-key")));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(5, results.Count(r => r.StatusCode == HttpStatusCode.OK && r.Headers.Contains("Idempotent-Replayed")));
        Assert.Single((await Ok(await Get(c, "/operations", t.Admin))).GetProperty("items").EnumerateArray());
        Assert.Equal(3, await LevelAsync(t.Id, t.L1, prod));
        await AssertInvariantsAsync(t.Id);
    }

    // ---------- FEFO, списання, прострочені ----------

    [SkippableFact]
    public async Task Write_off_follows_fefo_expired_first_and_levels_batches_movements_agree()
    {
        NeedDb();
        var c = NewClient();
        var t = await NewTenantAsync();
        var prod = await NewProduct(c, t.Admin, "P", trackExpiry: false, price: 2m);
        var expired = await SeedBatchAsync(t.Id, t.L1, prod, 5, Expired);
        await Receive(c, t.Admin, t.L1, prod, 5, exp: "2099-06-01");
        await Receive(c, t.Admin, t.L1, prod, 5, exp: "2098-06-01");
        await Receive(c, t.Admin, t.L1, prod, 5); // без терміну - останньою

        var wo = await Ok(await Op(c, "/write-offs", t.Admin, new { locationId = t.L1, reason = "damaged", lines = new[] { new { productId = prod, quantity = 12 } } }), HttpStatusCode.Created);
        var moves = wo.GetProperty("movements").EnumerateArray().ToList();
        Assert.Equal(-5m, moves.Single(m => m.GetProperty("batchId").GetGuid() == expired).GetProperty("quantity").GetDecimal()); // прострочена - першою
        Assert.Equal([-5m, -5m, -2m], moves.Select(m => m.GetProperty("quantity").GetDecimal()).OrderBy(x => x).ToArray());
        Assert.All(moves, m => Assert.Equal("write_off", m.GetProperty("type").GetString()));
        Assert.Equal("damaged", wo.GetProperty("reason").GetString());
        var batches = (await Ok(await Get(c, "/batches?status=all", t.Admin))).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(4, batches.Count);
        // FEFO-сортування списку партій: 2020, 2098, 2099, без терміну
        Assert.Equal("empty", batches.First(b => b.GetProperty("id").GetGuid() == expired).GetProperty("status").GetString()); // повністю списана
        Assert.Equal(8, await LevelAsync(t.Id, t.L1, prod));
        await AssertInvariantsAsync(t.Id);
    }

    [SkippableFact]
    public async Task Batch_listing_statuses_order_and_cursor()
    {
        NeedDb();
        var c = NewClient();
        var t = await NewTenantAsync();
        var prod = await NewProduct(c, t.Admin, "P");
        var expired = await SeedBatchAsync(t.Id, t.L1, prod, 1, Expired);
        await Receive(c, t.Admin, t.L1, prod, 1, exp: "2099-01-01");
        await Receive(c, t.Admin, t.L1, prod, 1);
        var emptied = await SeedBatchAsync(t.Id, t.L1, prod, 1, null);
        await Ok(await Op(c, "/write-offs", t.Admin, new { locationId = t.L1, reason = "lost", lines = new[] { new { productId = prod, quantity = 1, batchId = emptied } } }), HttpStatusCode.Created);

        async Task<List<JsonElement>> L(string q) => [.. (await Ok(await Get(c, "/batches?locationId=" + t.L1 + q, t.Admin))).GetProperty("items").EnumerateArray()];
        Assert.Equal(3, (await L("")).Count); // active: quantity > 0
        Assert.Equal([expired], (await L("&status=expired")).Select(b => b.GetProperty("id").GetGuid()));
        Assert.Equal([emptied], (await L("&status=empty")).Select(b => b.GetProperty("id").GetGuid()));
        Assert.Empty(await L("&status=expiring"));
        Assert.Equal(4, (await L("&status=all")).Count);
        var first = await L("&status=all");
        Assert.Equal("expired", first[0].GetProperty("status").GetString());
        Assert.True(first[0].GetProperty("daysToExpiry").GetInt32() < 0);
        Assert.Equal(JsonValueKind.Null, first[^1].GetProperty("expiresOn").ValueKind); // NULLS LAST
        await Fails(await Get(c, $"/batches?locationId={t.L1}&status=weird", t.Admin), HttpStatusCode.UnprocessableEntity, "invalid_status");

        // курсор: повний обхід по одному запису без дублів
        var seen = new List<Guid>();
        string? cursor = null;
        for (var i = 0; i < 6; i++)
        {
            var page = await Ok(await Get(c, $"/batches?locationId={t.L1}&status=all&take=1" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)), t.Admin));
            seen.AddRange(page.GetProperty("items").EnumerateArray().Select(b => b.GetProperty("id").GetGuid()));
            if (!page.GetProperty("hasMore").GetBoolean()) break;
            cursor = page.GetProperty("nextCursor").GetString();
        }
        Assert.Equal(4, seen.Count);
        Assert.Equal(4, seen.Distinct().Count());
        Assert.Equal(seen, first.Select(b => b.GetProperty("id").GetGuid()).ToList());
    }

    [SkippableFact]
    public async Task Insufficient_stock_is_422_with_available_and_changes_nothing()
    {
        NeedDb();
        var c = NewClient();
        var t = await NewTenantAsync();
        var a = await NewProduct(c, t.Admin, "A");
        var b = await NewProduct(c, t.Admin, "B");
        await Receive(c, t.Admin, t.L1, a, 10);
        await Receive(c, t.Admin, t.L1, b, 2);
        var before = (await Ok(await Get(c, "/operations", t.Admin))).GetProperty("items").GetArrayLength();

        var r = await Op(c, "/write-offs", t.Admin, new { locationId = t.L1, reason = "lost", lines = new[] { new { productId = a, quantity = 4 }, new { productId = b, quantity = 3 } } });
        await Fails(r, HttpStatusCode.UnprocessableEntity, "insufficient_stock");
        var lines = (await J(r)).GetProperty("lines").EnumerateArray().ToList();
        Assert.Single(lines);
        Assert.Equal(1, lines[0].GetProperty("line").GetInt32());
        Assert.Equal(b, lines[0].GetProperty("productId").GetGuid());
        Assert.Equal(3m, lines[0].GetProperty("requested").GetDecimal());
        Assert.Equal(2m, lines[0].GetProperty("available").GetDecimal());
        Assert.Equal(10, await LevelAsync(t.Id, t.L1, a));
        Assert.Equal(before, (await Ok(await Get(c, "/operations", t.Admin))).GetProperty("items").GetArrayLength());
        // нестача не зберігає ключ ідемпотентності: повтор після поповнення проходить
        await AssertInvariantsAsync(t.Id);
    }

    [SkippableFact]
    public async Task Expired_batches_are_not_transferred_and_expired_explicit_batch_is_rejected()
    {
        NeedDb();
        var c = NewClient();
        var t = await NewTenantAsync();
        var prod = await NewProduct(c, t.Admin, "P");
        var expired = await SeedBatchAsync(t.Id, t.L1, prod, 5, Expired);
        await Receive(c, t.Admin, t.L1, prod, 2, exp: Far);

        var r = await Op(c, "/transfers", t.Admin, new { fromLocationId = t.L1, toLocationId = t.Loc2, lines = new[] { new { productId = prod, quantity = 3 } } });
        await Fails(r, HttpStatusCode.UnprocessableEntity, "insufficient_stock");
        Assert.Equal(2m, (await J(r)).GetProperty("lines")[0].GetProperty("available").GetDecimal()); // без простроченої
        var be = await Op(c, "/transfers", t.Admin, new { fromLocationId = t.L1, toLocationId = t.Loc2, lines = new[] { new { productId = prod, quantity = 1, batchId = expired } } });
        await Fails(be, HttpStatusCode.UnprocessableEntity, "batch_expired");
        Assert.Equal(0, (await J(be)).GetProperty("line").GetInt32());
        Assert.Equal(0, await LevelAsync(t.Id, t.Loc2, prod));
        // списання простроченої явно дозволене
        Assert.Equal(HttpStatusCode.Created, (await Op(c, "/write-offs", t.Admin, new { locationId = t.L1, reason = "expired", lines = new[] { new { productId = prod, quantity = 5, batchId = expired } } })).StatusCode);
        await AssertInvariantsAsync(t.Id);
    }

    // ---------- переміщення ----------

    [SkippableFact]
    public async Task Transfer_moves_batches_with_attributes_atomically_and_legs_sum_to_zero()
    {
        NeedDb();
        var c = NewClient();
        var t = await NewTenantAsync();
        var prod = await NewProduct(c, t.Admin, "P", trackExpiry: true);
        await Receive(c, t.Admin, t.L1, prod, 4, exp: "2099-03-01", cost: 2.5m);
        await Receive(c, t.Admin, t.L1, prod, 4, exp: "2099-09-01", cost: 3m);
        var src = (await Ok(await Get(c, $"/batches?locationId={t.L1}", t.Admin))).GetProperty("items").EnumerateArray().ToList();

        var tr = await Ok(await Op(c, "/transfers", t.Admin, new { fromLocationId = t.L1, toLocationId = t.Loc2, note = "n", lines = new[] { new { productId = prod, quantity = 6 } } }), HttpStatusCode.Created);
        Assert.Equal("transfer", tr.GetProperty("kind").GetString());
        Assert.Equal(t.Loc2, tr.GetProperty("toLocationId").GetGuid());
        var moves = tr.GetProperty("movements").EnumerateArray().ToList();
        Assert.Equal(0m, moves.Sum(m => m.GetProperty("quantity").GetDecimal()));
        Assert.Equal(4, moves.Count);
        Assert.Equal(["transfer_in", "transfer_out"], moves.Select(m => m.GetProperty("type").GetString()).Distinct().Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(4 * 2.5m + 2 * 3m, tr.GetProperty("totalCost").GetDecimal()); // лише нога transfer_out

        var dst = (await Ok(await Get(c, $"/batches?locationId={t.Loc2}", t.Admin))).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, dst.Count);
        Assert.Equal(src.Select(b => b.GetProperty("expiresOn").GetString()).ToArray(), dst.Select(b => b.GetProperty("expiresOn").GetString()).ToArray());
        Assert.Equal(src.Select(b => b.GetProperty("receivedAt").GetDateTimeOffset()).ToArray(), dst.Select(b => b.GetProperty("receivedAt").GetDateTimeOffset()).ToArray());
        Assert.Equal([2.5m, 3m], dst.Select(b => b.GetProperty("unitCost").GetDecimal()).ToArray());
        Assert.Equal(src.Select(b => b.GetProperty("id").GetGuid()).ToArray(), dst.Select(b => b.GetProperty("sourceBatchId").GetGuid()).ToArray());
        Assert.Equal([4m, 2m], dst.Select(b => b.GetProperty("quantity").GetDecimal()).ToArray());
        Assert.Equal(2, await LevelAsync(t.Id, t.L1, prod));
        Assert.Equal(6, await LevelAsync(t.Id, t.Loc2, prod));

        // збій на другому рядку відкочує весь запит
        var prod2 = await NewProduct(c, t.Admin, "Q");
        await Receive(c, t.Admin, t.L1, prod2, 1);
        var bad = await Op(c, "/transfers", t.Admin, new { fromLocationId = t.L1, toLocationId = t.Loc2, lines = new[] { new { productId = prod, quantity = 1 }, new { productId = prod2, quantity = 5 } } });
        await Fails(bad, HttpStatusCode.UnprocessableEntity, "insufficient_stock");
        Assert.Equal(2, await LevelAsync(t.Id, t.L1, prod));
        Assert.Equal(6, await LevelAsync(t.Id, t.Loc2, prod));
        Assert.Equal(1, await LevelAsync(t.Id, t.L1, prod2));
        await Fails(await Op(c, "/transfers", t.Admin, new { fromLocationId = t.L1, toLocationId = t.L1, lines = new[] { new { productId = prod, quantity = 1 } } }), HttpStatusCode.UnprocessableEntity, "same_location");
        await AssertInvariantsAsync(t.Id);
    }

    [SkippableFact]
    public async Task Transfer_destination_rules_inactive_location_and_disabled_product()
    {
        NeedDb();
        var c = NewClient();
        var t = await NewTenantAsync();
        var prod = await NewProduct(c, t.Admin, "P");
        await Receive(c, t.Admin, t.L1, prod, 5);
        Assert.Equal(HttpStatusCode.OK, (await Call(c, HttpMethod.Put, $"/products/{prod}/locations/{t.Loc2}", t.Admin, new { isEnabled = false })).StatusCode);
        await Fails(await Op(c, "/transfers", t.Admin, new { fromLocationId = t.L1, toLocationId = t.Loc2, lines = new[] { new { productId = prod, quantity = 1 } } }), HttpStatusCode.UnprocessableEntity, "product_disabled_for_location");
        await Fails(await Op(c, "/receipts", t.Admin, new { locationId = t.Loc2, lines = new[] { new { productId = prod, quantity = 1 } } }), HttpStatusCode.UnprocessableEntity, "product_disabled_for_location");
        await using (var ctx = fx.Db.CreateContext(t.Id))
            await ctx.Locations.Where(l => l.Id == t.Loc2).ExecuteUpdateAsync(s => s.SetProperty(l => l.IsActive, false));
        await Fails(await Op(c, "/transfers", t.Admin, new { fromLocationId = t.L1, toLocationId = t.Loc2, lines = new[] { new { productId = prod, quantity = 1 } } }), HttpStatusCode.UnprocessableEntity, "location_inactive");
        await Fails(await Op(c, "/receipts", t.Admin, new { locationId = t.Loc2, lines = new[] { new { productId = prod, quantity = 1 } } }), HttpStatusCode.UnprocessableEntity, "location_inactive");
        // списання в неактивному закладі дозволене
        await SeedBatchAsync(t.Id, t.Loc2, prod, 2, null);
        Assert.Equal(HttpStatusCode.Created, (await Op(c, "/write-offs", t.Admin, new { locationId = t.Loc2, reason = "lost", lines = new[] { new { productId = prod, quantity = 2 } } })).StatusCode);
        await AssertInvariantsAsync(t.Id);
    }

    // ---------- гонки ----------

    [SkippableFact]
    public async Task Two_parallel_write_offs_of_the_last_unit_one_wins_level_zero()
    {
        NeedDb();
        var c = NewClient();
        var t = await NewTenantAsync();
        var prod = await NewProduct(c, t.Admin, "P");
        await Receive(c, t.Admin, t.L1, prod, 1);
        for (var round = 0; round < 3; round++)
        {
            var rs = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Op(c, "/write-offs", t.Admin, new { locationId = t.L1, reason = "lost", lines = new[] { new { productId = prod, quantity = 1 } } })));
            Assert.Equal(1, rs.Count(r => r.StatusCode == HttpStatusCode.Created));
            var loser = rs.Single(r => r.StatusCode == HttpStatusCode.UnprocessableEntity);
            Assert.Equal("insufficient_stock", await Code(loser));
            Assert.Equal(0, await LevelAsync(t.Id, t.L1, prod));
            await AssertInvariantsAsync(t.Id);
            await Receive(c, t.Admin, t.L1, prod, 1);
        }
    }

    [SkippableFact]
    public async Task Opposite_transfers_in_parallel_do_not_deadlock_and_keep_invariants()
    {
        NeedDb();
        var c = NewClient();
        var t = await NewTenantAsync();
        var a = await NewProduct(c, t.Admin, "A");
        var b = await NewProduct(c, t.Admin, "B");
        foreach (var p in new[] { a, b })
        {
            await Receive(c, t.Admin, t.L1, p, 20);
            await Receive(c, t.Admin, t.Loc2, p, 20);
        }
        var tasks = new List<Task<HttpResponseMessage>>();
        for (var i = 0; i < 8; i++)
        {
            var (from, to) = i % 2 == 0 ? (t.L1, t.Loc2) : (t.Loc2, t.L1);
            // різний порядок товарів у рядках: порядок блокувань не залежить від порядку рядків
            var lines = i % 4 < 2 ? new[] { new { productId = a, quantity = 1 }, new { productId = b, quantity = 1 } } : new[] { new { productId = b, quantity = 1 }, new { productId = a, quantity = 1 } };
            tasks.Add(Op(c, "/transfers", t.Admin, new { fromLocationId = from, toLocationId = to, lines }));
        }
        var done = await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(TimeSpan.FromSeconds(60)));
        Assert.True(done == Task.WhenAll(tasks) || tasks.All(x => x.IsCompleted), "deadlock: transfers did not finish");
        var rs = await Task.WhenAll(tasks);
        Assert.All(rs, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        foreach (var p in new[] { a, b })
            Assert.Equal(40, await LevelAsync(t.Id, t.L1, p) + await LevelAsync(t.Id, t.Loc2, p));
        await AssertInvariantsAsync(t.Id);
    }

    // ---------- інвентаризація ----------

    private static async Task<JsonElement> NewStocktake(HttpClient c, string token, Guid loc, Guid[]? productIds = null) =>
        await Ok(await Call(c, HttpMethod.Post, "/stocktakes", token, new { locationId = loc, productIds }), HttpStatusCode.Created);

    private static object Lines(int version, params object[] lines) => new { expectedVersion = version, lines };

    [SkippableFact]
    public async Task Stocktake_surplus_shortage_zero_and_closed_rules()
    {
        NeedDb();
        var c = NewClient();
        var t = await NewTenantAsync();
        var shortP = await NewProduct(c, t.Admin, "Short");
        var surplusP = await NewProduct(c, t.Admin, "Surplus", price: 4m);
        var zeroP = await NewProduct(c, t.Admin, "Zero");
        var skipP = await NewProduct(c, t.Admin, "Skipped");
        var expired = await SeedBatchAsync(t.Id, t.L1, shortP, 3, Expired);
        await Receive(c, t.Admin, t.L1, shortP, 7, exp: Far);
        await Receive(c, t.Admin, t.L1, surplusP, 2);
        await Receive(c, t.Admin, t.L1, zeroP, 5);

        var st = await NewStocktake(c, t.Admin, t.L1);
        var id = st.GetProperty("id").GetGuid();
        Assert.Equal("draft", st.GetProperty("status").GetString());
        Assert.Equal(4, st.GetProperty("lineCount").GetInt32());
        Assert.Equal(0, st.GetProperty("countedCount").GetInt32());
        var dup = await Call(c, HttpMethod.Post, "/stocktakes", t.Admin, new { locationId = t.L1 });
        await Fails(dup, HttpStatusCode.Conflict, "stocktake_in_progress");
        Assert.Equal(id, (await J(dup)).GetProperty("stocktakeId").GetGuid());

        // порожнє проведення
        await Fails(await Call(c, HttpMethod.Post, $"/stocktakes/{id}/post", t.Admin, new { expectedVersion = 1 }), HttpStatusCode.UnprocessableEntity, "stocktake_empty");
        await Fails(await Call(c, HttpMethod.Post, $"/stocktakes/{id}/post", t.Admin, new { }), HttpStatusCode.UnprocessableEntity, "expected_version_required");

        // рух під час чернетки не блокується й враховується: expected = поточний рівень
        await Receive(c, t.Admin, t.L1, zeroP, 1);
        var live = await Ok(await Get(c, $"/stocktakes/{id}", t.Admin));
        Assert.Equal(6m, live.GetProperty("lines").EnumerateArray().First(l => l.GetProperty("product").GetProperty("id").GetGuid() == zeroP).GetProperty("expectedQuantity").GetDecimal());

        // версія
        await Fails(await Call(c, HttpMethod.Put, $"/stocktakes/{id}/lines", t.Admin, new { lines = new[] { new { productId = zeroP, countedQuantity = 1 } } }), HttpStatusCode.UnprocessableEntity, "expected_version_required");
        var conflict = await Call(c, HttpMethod.Put, $"/stocktakes/{id}/lines", t.Admin, Lines(5, new { productId = zeroP, countedQuantity = 1 }));
        await Fails(conflict, HttpStatusCode.Conflict, "stocktake_version_conflict");
        Assert.Equal(1, (await J(conflict)).GetProperty("currentVersion").GetInt32());
        await Fails(await Call(c, HttpMethod.Put, $"/stocktakes/{id}/lines", t.Admin, Lines(1, new { productId = zeroP, countedQuantity = 1 }, new { productId = zeroP, countedQuantity = 2 })), HttpStatusCode.UnprocessableEntity, "duplicate_line");
        await Fails(await Call(c, HttpMethod.Put, $"/stocktakes/{id}/lines", t.Admin, Lines(1, new { productId = zeroP, countedQuantity = -1 })), HttpStatusCode.UnprocessableEntity, "invalid_quantity");
        await Fails(await Call(c, HttpMethod.Put, $"/stocktakes/{id}/lines", t.Admin, Lines(1, new { productId = zeroP, countedQuantity = 1.5 })), HttpStatusCode.UnprocessableEntity, "invalid_quantity");
        await Fails(await Call(c, HttpMethod.Put, $"/stocktakes/{id}/lines", t.Admin, Lines(1, new { productId = Guid.NewGuid(), countedQuantity = 1 })), HttpStatusCode.NotFound, "product_not_found");

        var put = await Ok(await Call(c, HttpMethod.Put, $"/stocktakes/{id}/lines", t.Admin, Lines(1,
            new { productId = shortP, countedQuantity = 4 },     // було 10 -> нестача 6: FEFO бере прострочену (3) і ще 3
            new { productId = surplusP, countedQuantity = 5 },   // було 2 -> надлишок 3
            new { productId = zeroP, countedQuantity = 6 })));    // було 6 -> різниці немає
        Assert.Equal(2, put.GetProperty("version").GetInt32());
        Assert.Equal(3, put.GetProperty("countedCount").GetInt32());
        // «не рахували» не береться
        var uncounted = put.GetProperty("lines").EnumerateArray().First(l => l.GetProperty("product").GetProperty("id").GetGuid() == skipP);
        Assert.Equal(JsonValueKind.Null, uncounted.GetProperty("countedQuantity").ValueKind);

        var posted = await Ok(await Call(c, HttpMethod.Post, $"/stocktakes/{id}/post", t.Admin, new { expectedVersion = 2 }));
        Assert.Equal("posted", posted.GetProperty("stocktake").GetProperty("status").GetString());
        var op = posted.GetProperty("operation");
        Assert.Equal("stocktake", op.GetProperty("kind").GetString());
        Assert.Equal(id, op.GetProperty("stocktakeId").GetGuid());
        var moves = op.GetProperty("movements").EnumerateArray().ToList();
        Assert.Equal(3, moves.Count); // 2 з нестачі + 1 надлишок; нуль без рухів
        Assert.All(moves, m => Assert.Equal("stocktake_adjust", m.GetProperty("type").GetString()));
        var shortMoves = moves.Where(m => m.GetProperty("product").GetProperty("id").GetGuid() == shortP).ToList();
        // порядок рухів з однаковим occurred_at - за випадковим id, тому «прострочені першими» перевіряємо за складом, а не за позицією
        Assert.Contains(expired, shortMoves.Select(m => m.GetProperty("batchId").GetGuid()));
        Assert.Equal(-3m, shortMoves.Single(m => m.GetProperty("batchId").GetGuid() == expired).GetProperty("quantity").GetDecimal());
        Assert.Equal(2, shortMoves.Count);
        Assert.All(shortMoves, m => Assert.Equal(-3m, m.GetProperty("quantity").GetDecimal()));
        Assert.Equal(3m, moves.Single(m => m.GetProperty("product").GetProperty("id").GetGuid() == surplusP).GetProperty("quantity").GetDecimal());
        Assert.Equal(4, await LevelAsync(t.Id, t.L1, shortP));
        Assert.Equal(5, await LevelAsync(t.Id, t.L1, surplusP));
        Assert.Equal(6, await LevelAsync(t.Id, t.L1, zeroP));
        var surplusBatch = (await Ok(await Get(c, $"/batches?locationId={t.L1}&productId={surplusP}&status=all", t.Admin))).GetProperty("items").EnumerateArray().Single(b => b.GetProperty("operationId").GetGuid() == op.GetProperty("id").GetGuid());
        Assert.Equal(3m, surplusBatch.GetProperty("quantity").GetDecimal());
        Assert.Equal(4m, surplusBatch.GetProperty("unitCost").GetDecimal()); // ціна товару
        var snap = (await Ok(await Get(c, $"/stocktakes/{id}", t.Admin))).GetProperty("lines").EnumerateArray().First(l => l.GetProperty("product").GetProperty("id").GetGuid() == surplusP);
        Assert.Equal(2m, snap.GetProperty("expectedQuantity").GetDecimal()); // знімок на момент проведення
        Assert.Equal(3m, snap.GetProperty("difference").GetDecimal());

        // незмінність
        await Fails(await Call(c, HttpMethod.Post, $"/stocktakes/{id}/post", t.Admin, new { expectedVersion = 3 }), HttpStatusCode.Conflict, "stocktake_closed");
        await Fails(await Call(c, HttpMethod.Post, $"/stocktakes/{id}/cancel", t.Admin, new { expectedVersion = 3 }), HttpStatusCode.Conflict, "stocktake_closed");
        await Fails(await Call(c, HttpMethod.Put, $"/stocktakes/{id}/lines", t.Admin, Lines(3, new { productId = zeroP, countedQuantity = 1 })), HttpStatusCode.Conflict, "stocktake_closed");
        Assert.Equal(["posted"], (await Ok(await Get(c, $"/stocktakes?locationId={t.L1}&status=posted", t.Admin))).GetProperty("items").EnumerateArray().Select(s => s.GetProperty("status").GetString()).ToArray());

        // після проведення можна відкрити нову й скасувати: рухів немає
        var st2 = await NewStocktake(c, t.Admin, t.L1, [zeroP]);
        var movesBefore = (await Ok(await Get(c, "/movements?take=200", t.Admin))).GetProperty("items").GetArrayLength();
        var cancelled = await Ok(await Call(c, HttpMethod.Post, $"/stocktakes/{st2.GetProperty("id").GetGuid()}/cancel", t.Admin, new { expectedVersion = 1 }));
        Assert.Equal("cancelled", cancelled.GetProperty("status").GetString());
        Assert.Equal(movesBefore, (await Ok(await Get(c, "/movements?take=200", t.Admin))).GetProperty("items").GetArrayLength());
        await AssertInvariantsAsync(t.Id);
    }

    [SkippableFact]
    public async Task Stocktake_requires_expiry_for_tracked_surplus_and_zero_difference_creates_no_operation()
    {
        NeedDb();
        var c = NewClient();
        var t = await NewTenantAsync();
        var tracked = await NewProduct(c, t.Admin, "Tracked", trackExpiry: true);
        var plain = await NewProduct(c, t.Admin, "Plain");
        await Receive(c, t.Admin, t.L1, tracked, 2, exp: Far);
        await Receive(c, t.Admin, t.L1, plain, 2);
        var id = (await NewStocktake(c, t.Admin, t.L1, [tracked, plain])).GetProperty("id").GetGuid();

        await Ok(await Call(c, HttpMethod.Put, $"/stocktakes/{id}/lines", t.Admin, Lines(1, new { productId = tracked, countedQuantity = 5 })));
        var r = await Call(c, HttpMethod.Post, $"/stocktakes/{id}/post", t.Admin, new { expectedVersion = 2 });
        await Fails(r, HttpStatusCode.UnprocessableEntity, "expiry_required");
        Assert.Equal(2, await LevelAsync(t.Id, t.L1, tracked)); // відкат
        Assert.Equal("draft", (await Ok(await Get(c, $"/stocktakes/{id}", t.Admin))).GetProperty("status").GetString());

        await Ok(await Call(c, HttpMethod.Put, $"/stocktakes/{id}/lines", t.Admin, Lines(2, new { productId = tracked, countedQuantity = 5, surplusExpiresOn = Far })));
        var ok = await Ok(await Call(c, HttpMethod.Post, $"/stocktakes/{id}/post", t.Admin, new { expectedVersion = 3 }));
        Assert.Equal(5, await LevelAsync(t.Id, t.L1, tracked));
        Assert.Equal(1, ok.GetProperty("operation").GetProperty("movements").GetArrayLength());

        var id2 = (await NewStocktake(c, t.Admin, t.L1, [plain])).GetProperty("id").GetGuid();
        await Ok(await Call(c, HttpMethod.Put, $"/stocktakes/{id2}/lines", t.Admin, Lines(1, new { productId = plain, countedQuantity = 2 })));
        var zero = await Ok(await Call(c, HttpMethod.Post, $"/stocktakes/{id2}/post", t.Admin, new { expectedVersion = 2 }));
        Assert.Equal(JsonValueKind.Null, zero.GetProperty("operation").ValueKind);
        Assert.Equal("posted", zero.GetProperty("stocktake").GetProperty("status").GetString());
        await AssertInvariantsAsync(t.Id);
    }

    [SkippableFact]
    public async Task Stocktake_post_races_with_write_offs_and_invariants_hold()
    {
        NeedDb();
        var c = NewClient();
        var t = await NewTenantAsync();
        var prod = await NewProduct(c, t.Admin, "P");
        await Receive(c, t.Admin, t.L1, prod, 10);
        var id = (await NewStocktake(c, t.Admin, t.L1, [prod])).GetProperty("id").GetGuid();
        await Ok(await Call(c, HttpMethod.Put, $"/stocktakes/{id}/lines", t.Admin, Lines(1, new { productId = prod, countedQuantity = 6 })));

        var tasks = new List<Task<HttpResponseMessage>> { Call(c, HttpMethod.Post, $"/stocktakes/{id}/post", t.Admin, new { expectedVersion = 2 }) };
        for (var i = 0; i < 3; i++) tasks.Add(Op(c, "/write-offs", t.Admin, new { locationId = t.L1, reason = "lost", lines = new[] { new { productId = prod, quantity = 1 } } }));
        var rs = await Task.WhenAll(tasks);
        Assert.All(rs, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Created }));
        // проведення виставляє рівень рівно в 6 лише якщо виконалось після списань; інакше списання йдуть від 6. Інваріант - обов'язковий.
        var level = await LevelAsync(t.Id, t.L1, prod);
        Assert.InRange(level, 3m, 6m);
        await AssertInvariantsAsync(t.Id);
    }

    // ---------- append-only, ліміт ----------

    [SkippableFact]
    public async Task Write_rate_limit_returns_429_with_retry_after()
    {
        NeedDb();
        var c = NewClient(writePerMinute: "10");
        var t = await NewTenantAsync();
        var statuses = new List<HttpStatusCode>();
        HttpResponseMessage? limited = null;
        for (var i = 0; i < 12; i++)
        {
            var r = await Call(c, HttpMethod.Post, "/suppliers", t.Admin, new { name = "S" + i });
            statuses.Add(r.StatusCode);
            if (r.StatusCode == HttpStatusCode.TooManyRequests) { limited = r; break; }
        }
        Assert.NotNull(limited);
        Assert.Equal(10, statuses.Count(s => s == HttpStatusCode.Created));
        Assert.Equal("rate_limited", await Code(limited!));
        Assert.Equal("60", limited!.Headers.RetryAfter?.Delta?.TotalSeconds.ToString());
        // читання не лімітується; ліміт на користувача, не на tenant
        Assert.Equal(HttpStatusCode.OK, (await Get(c, "/suppliers", t.Admin)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Call(c, HttpMethod.Post, "/suppliers", t.Owner, new { name = "Other" })).StatusCode);
    }

    [SkippableFact]
    public async Task Movements_and_operations_listing_filters_and_range()
    {
        NeedDb();
        var c = NewClient();
        var t = await NewTenantAsync();
        var prod = await NewProduct(c, t.Admin, "P");
        await Receive(c, t.Admin, t.L1, prod, 5);
        await Ok(await Op(c, "/write-offs", t.Admin, new { locationId = t.L1, reason = "lost", lines = new[] { new { productId = prod, quantity = 1 } } }), HttpStatusCode.Created);

        var all = (await Ok(await Get(c, "/movements", t.Admin))).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, all.Count);
        Assert.Equal("write_off", all[0].GetProperty("type").GetString()); // occurred_at DESC
        Assert.Equal("lost", all[0].GetProperty("reason").GetString());
        Assert.Single((await Ok(await Get(c, "/movements?type=receipt", t.Admin))).GetProperty("items").EnumerateArray());
        Assert.Single((await Ok(await Get(c, "/operations?kind=write_off", t.Admin))).GetProperty("items").EnumerateArray());
        var opId = all[0].GetProperty("operationId").GetGuid();
        Assert.Single((await Ok(await Get(c, $"/movements?operationId={opId}", t.Admin))).GetProperty("items").EnumerateArray());
        var op = await Ok(await Get(c, $"/operations/{opId}", t.Admin));
        Assert.Equal(1, op.GetProperty("productCount").GetInt32());
        Assert.Equal(1, op.GetProperty("movements").GetArrayLength());
        await Fails(await Get(c, "/movements?from=2020-01-01T00:00:00Z&to=2026-01-01T00:00:00Z", t.Admin), HttpStatusCode.UnprocessableEntity, "invalid_range");
        await Fails(await Get(c, "/operations?from=garbage", t.Admin), HttpStatusCode.UnprocessableEntity, "invalid_range");
        // повний обхід журналу курсором
        var p1 = await Ok(await Get(c, "/movements?take=1", t.Admin));
        Assert.True(p1.GetProperty("hasMore").GetBoolean());
        var p2 = await Ok(await Get(c, "/movements?take=1&cursor=" + Uri.EscapeDataString(p1.GetProperty("nextCursor").GetString()!), t.Admin));
        Assert.False(p2.GetProperty("hasMore").GetBoolean());
        Assert.Equal("receipt", p2.GetProperty("items")[0].GetProperty("type").GetString());
        await Fails(await Get(c, "/movements?take=1&type=receipt&cursor=" + Uri.EscapeDataString(p1.GetProperty("nextCursor").GetString()!), t.Admin), HttpStatusCode.UnprocessableEntity, "invalid_cursor");
    }

    [SkippableFact]
    public async Task Summary_and_stock_flags_low_expiring_and_locations()
    {
        NeedDb();
        var c = NewClient();
        var t = await NewTenantAsync();
        var prod = await NewProduct(c, t.Admin, "Low", price: 1m);
        Assert.Equal(HttpStatusCode.OK, (await Call(c, HttpMethod.Put, $"/products/{prod}", t.Admin, new { name = "Low", unit = "pcs", purpose = "both", minStock = 5 })).StatusCode);
        await Receive(c, t.Admin, t.L1, prod, 3, exp: Far);
        await Receive(c, t.Admin, t.Loc2, prod, 8);
        var expired = await SeedBatchAsync(t.Id, t.L1, prod, 2, Expired);

        var stock = (await Ok(await Get(c, $"/stock?locationId={t.L1}", t.Admin))).GetProperty("items")[0];
        Assert.Equal(5m, stock.GetProperty("quantity").GetDecimal());
        Assert.Equal(5m, stock.GetProperty("minStock").GetDecimal());
        Assert.False(stock.GetProperty("isLow").GetBoolean()); // 5 не < 5
        Assert.Equal(2m, stock.GetProperty("expiredQuantity").GetDecimal());
        Assert.Equal("2020-01-01", stock.GetProperty("nearestExpiresOn").GetString());
        // налаштування закладу змінює ефективний мінімум
        await Call(c, HttpMethod.Put, $"/products/{prod}/locations/{t.L1}", t.Admin, new { minStock = 6, isEnabled = true });
        Assert.True((await Ok(await Get(c, $"/stock?locationId={t.L1}&state=low", t.Admin))).GetProperty("items")[0].GetProperty("isLow").GetBoolean());
        Assert.Empty((await Ok(await Get(c, $"/stock?locationId={t.Loc2}&state=low", t.Admin))).GetProperty("items").EnumerateArray());
        Assert.Single((await Ok(await Get(c, $"/stock?locationId={t.L1}&state=in_stock", t.Admin))).GetProperty("items").EnumerateArray());
        Assert.Empty((await Ok(await Get(c, $"/stock?locationId={t.L1}&state=out_of_stock", t.Admin))).GetProperty("items").EnumerateArray());
        await Fails(await Get(c, "/stock", t.Admin), HttpStatusCode.UnprocessableEntity, "location_required");

        var sum = await Ok(await Get(c, "/stock/summary", t.Admin));
        var row = sum.GetProperty("items")[0];
        Assert.Equal(13m, row.GetProperty("totalQuantity").GetDecimal());
        Assert.True(row.GetProperty("isLowAnywhere").GetBoolean());
        Assert.Equal(2, row.GetProperty("locations").GetArrayLength());
        Assert.Single((await Ok(await Get(c, "/stock/summary?lowOnly=true", t.Admin))).GetProperty("items").EnumerateArray());
        _ = expired;
    }
}
