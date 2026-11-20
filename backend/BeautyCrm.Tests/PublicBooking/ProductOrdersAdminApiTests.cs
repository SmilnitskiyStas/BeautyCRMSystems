using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyPublicBooking;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Tests.Auth;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static BeautyCrm.Tests.Auth.AuthApiFixture;

namespace BeautyCrm.Tests.PublicBooking;

/// <summary>
/// TASK-903 (ADR-026, §33.13/§33.16/§33.19 п.6): CRM-маршрути замовлень товарів на реальному PostgreSQL (RLS, роль app) через повний HTTP-конвеєр.
/// Замовлення створюються й оплачуються через публічний API з фейковою оплатою (дані sale-рухів і партій справжні); прострочення/зависання - прямим UPDATE у БД.
/// </summary>
public sealed class ProductOrdersAdminApiTests(AuthApiFixture fx) : IClassFixture<AuthApiFixture>, IDisposable
{
    private readonly List<IDisposable> _disposables = [];
    private static readonly DateOnly Far = new(2099, 1, 1);
    private static readonly DateOnly Mid = new(2098, 1, 1);
    private const string Phone = "+380671234567";

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
    }

    private void NeedDb() => Skip.If(fx.SkipReason is not null, fx.SkipReason);

    private sealed record Env(
        TenantSeed T, Guid L1, Guid L2, Guid SpecA, Guid SpecB, string Owner, string Spec, string SpecOwn, string AdminFull, string AdminMasked)
    {
        public string Slug => T.Slug;
    }

    private sealed record Host(HttpClient Client, FakeOrderPayments Pay);

    private Host NewHost(bool enabled = true, string? writePerMinute = "600")
    {
        var pay = new FakeOrderPayments();
        var f = fx.CreateFactory(10_000).WithWebHostBuilder(b =>
        {
            b.UseSetting("ProductOrders:Enabled", enabled ? "true" : "false");
            b.UseSetting("PublicBooking:RateLimit:ReadPermit", "100000");
            b.UseSetting("PublicBooking:RateLimit:TokenPermit", "100000");
            b.UseSetting("PublicBooking:RateLimit:WritePermit", "100000");
            if (writePerMinute is not null) b.UseSetting("Inventory:WritePerUserPerMinute", writePerMinute);
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<IPaymentService>();
                s.AddSingleton<IPaymentService>(pay);
            });
        });
        _disposables.Add(f);
        return new Host(f.CreateClient(), pay);
    }

    private async Task<Env> NewEnvAsync(string[]? modules = null)
    {
        var t = await fx.CreateTenantAsync(modules ?? [.. BeautyModules.Known]);
        var owner = await fx.LoginAsync(t.Slug, t.OwnerEmail);
        var seed = await fx.SeedBeautyAsync(t.TenantId);
        Guid l2;
        await using (var ctx = fx.Db.CreateContext(t.TenantId))
        {
            var loc2 = new Location { Name = "Second", Address = "Street 2", Timezone = "Europe/Kyiv" };
            ctx.Locations.Add(loc2);
            ctx.SpecialistLocations.Add(new SpecialistLocation { SpecialistId = seed.SpecialistA, LocationId = seed.LocationId });
            ctx.SpecialistLocations.Add(new SpecialistLocation { SpecialistId = seed.SpecialistB, LocationId = seed.LocationId });
            await ctx.SaveChangesAsync();
            l2 = loc2.Id;
        }
        var spec = await fx.InviteAndLoginAsync(t, owner.AccessToken, Roles.Specialist, seed.SpecialistA); // preset: view=own, manage=none
        var own = PermissionPresets.For(Roles.Specialist).ToDictionary(kv => kv.Key, kv => kv.Value);
        own["inventory.manage"] = "own";
        var specOwn = await fx.CreateUserAsync(t, Roles.Specialist, seed.SpecialistB, own);
        var full = PermissionPresets.For(Roles.Admin).ToDictionary(kv => kv.Key, kv => kv.Value);
        full["clients.contacts"] = "full";
        full["clients.view"] = "all";
        var masked = PermissionPresets.For(Roles.Admin).ToDictionary(kv => kv.Key, kv => kv.Value);
        masked["clients.contacts"] = "masked";
        masked["clients.view"] = "own";
        var adminFull = await fx.CreateUserAsync(t, Roles.Admin, null, full);
        var adminMasked = await fx.CreateUserAsync(t, Roles.Admin, null, masked);
        return new Env(t, seed.LocationId, l2, seed.SpecialistA, seed.SpecialistB, owner.AccessToken, spec.AccessToken, specOwn.Tokens.AccessToken,
            adminFull.Tokens.AccessToken, adminMasked.Tokens.AccessToken);
    }

    // ---------- наповнення ----------

    private async Task<Guid> AddProductAsync(Env e, string name, decimal price, Guid? loc = null, params (decimal Qty, DateOnly Exp)[] batches)
    {
        Guid id;
        await using (var ctx = fx.Db.CreateContext(e.T.TenantId))
        {
            var p = new Product { Name = name, Unit = ProductUnit.Pcs, Purpose = ProductPurpose.Sale, SalePrice = price, IsActive = true, UpdatedByUserId = e.T.OwnerId, PurchasePrice = 7m };
            ctx.Products.Add(p);
            await ctx.SaveChangesAsync();
            id = p.Id;
        }
        foreach (var (qty, exp) in batches) await AddBatchAsync(e, id, qty, exp, loc ?? e.L1);
        return id;
    }

    private async Task<Guid> AddBatchAsync(Env e, Guid productId, decimal qty, DateOnly exp, Guid loc)
    {
        await using var ctx = fx.Db.CreateContext(e.T.TenantId);
        var at = DateTimeOffset.UtcNow.AddDays(-1);
        var op = new StockOperation { Id = Guid.NewGuid(), Kind = StockOperationKind.Receipt, LocationId = loc, CreatedByUserId = e.T.OwnerId, CreatedAt = at };
        var batch = new StockBatch
        {
            Id = Guid.NewGuid(), LocationId = loc, ProductId = productId, OperationId = op.Id, ReceivedAt = at, ExpiresOn = exp,
            InitialQuantity = qty, Quantity = qty, UnitCost = 10m, CreatedAt = at, UpdatedAt = at,
        };
        ctx.StockOperations.Add(op);
        ctx.StockBatches.Add(batch);
        ctx.StockMovements.Add(new StockMovement
        {
            Id = Guid.NewGuid(), OperationId = op.Id, LocationId = loc, ProductId = productId, BatchId = batch.Id, Type = StockMovementType.Receipt,
            Quantity = qty, UnitCost = 10m, CreatedByUserId = e.T.OwnerId, OccurredAt = at,
        });
        var level = await ctx.StockLevels.FirstOrDefaultAsync(l => l.LocationId == loc && l.ProductId == productId);
        if (level is null) ctx.StockLevels.Add(new StockLevel { LocationId = loc, ProductId = productId, Quantity = qty, CreatedAt = at, UpdatedAt = at });
        else level.Quantity += qty;
        await ctx.SaveChangesAsync();
        return batch.Id;
    }

    private static async Task<JsonElement> Expect(HttpResponseMessage r, HttpStatusCode status, string? code = null)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == status, $"expected {status}, got {(int)r.StatusCode}: {body}");
        var j = JsonDocument.Parse(body).RootElement.Clone();
        if (code is not null) Assert.Equal(code, j.GetProperty("code").GetString());
        return j;
    }

    private sealed record Placed(string Token, Guid Id, int Number);

    /// <summary>Створює замовлення через публічний API (стан pending_payment).</summary>
    private async Task<Placed> PlaceAsync(Host h, Env e, Guid loc, Guid product, int qty, string? phone = null)
    {
        var body = new { locationId = loc, customer = new { name = "Олена Тестова", phone = phone ?? Phone }, lines = new[] { new { productId = product, quantity = qty } } };
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/public/{e.Slug}/product-orders") { Content = JsonContent.Create(body) };
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var j = await Expect(await h.Client.SendAsync(req), HttpStatusCode.Created);
        var token = j.GetProperty("publicToken").GetString()!;
        var hash = PublicTokenService.Hash(token);
        await using var ctx = fx.Db.CreateContext(e.T.TenantId);
        var o = await ctx.ProductOrders.AsNoTracking().FirstAsync(x => x.PublicTokenHash == hash);
        return new Placed(token, o.Id, o.OrderNumber);
    }

    private async Task<Placed> PlacePaidAsync(Host h, Env e, Guid loc, Guid product, int qty, string? phone = null)
    {
        var o = await PlaceAsync(h, e, loc, product, qty, phone);
        var r = await h.Client.PostAsync($"/api/public/{e.Slug}/product-orders/{o.Token}/pay", JsonContent.Create(new { }));
        Assert.Equal("paid", (await Expect(r, HttpStatusCode.OK)).GetProperty("order").GetProperty("status").GetString());
        return o;
    }

    private async Task SetOrderAsync(Env e, Guid id, Action<ProductOrder> change)
    {
        await using var ctx = fx.Db.CreateContext(e.T.TenantId);
        var o = await ctx.ProductOrders.FirstAsync(x => x.Id == id);
        change(o);
        await ctx.SaveChangesAsync();
    }

    private static Task<HttpResponseMessage> Crm(Host h, HttpMethod m, string path, string? token, object? body = null)
    {
        var r = new HttpRequestMessage(m, "/api/beauty/inventory" + path);
        if (body is not null) r.Content = JsonContent.Create(body);
        if (token is not null) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return h.Client.SendAsync(r);
    }

    private static Task<HttpResponseMessage> CrmGet(Host h, string path, string? token) => Crm(h, HttpMethod.Get, "/product-orders" + path, token);
    private static Task<HttpResponseMessage> CrmPost(Host h, Guid id, string action, string? token, object? body = null) =>
        Crm(h, HttpMethod.Post, $"/product-orders/{id}/{action}", token, body ?? new { });

    private async Task<(decimal Level, decimal Batches, decimal Moves)> PairAsync(Env e, Guid loc, Guid productId)
    {
        await using var ctx = fx.Db.CreateContext(e.T.TenantId);
        var level = await ctx.StockLevels.AsNoTracking().Where(l => l.LocationId == loc && l.ProductId == productId).Select(l => l.Quantity).FirstOrDefaultAsync();
        var batches = await ctx.StockBatches.AsNoTracking().Where(b => b.LocationId == loc && b.ProductId == productId).SumAsync(b => (decimal?)b.Quantity) ?? 0m;
        var moves = await ctx.StockMovements.AsNoTracking().Where(m => m.LocationId == loc && m.ProductId == productId && !m.IsShortage).SumAsync(m => (decimal?)m.Quantity) ?? 0m;
        return (level, batches, moves);
    }

    private async Task AssertInvariantAsync(Env e, Guid loc, Guid productId)
    {
        var (level, batches, moves) = await PairAsync(e, loc, productId);
        Assert.Equal(level, batches);
        Assert.Equal(level, moves);
    }

    // ---------- читання ----------

    [SkippableFact]
    public async Task list_counts_detail_filters_effective_status_pagination_and_no_store()
    {
        NeedDb();
        var e = await NewEnvAsync();
        var h = NewHost();
        var p = await AddProductAsync(e, "Shampoo", 100m, null, (2, Mid), (8, Far));
        var p2 = await AddProductAsync(e, "Cream", 50m, e.L2, (5, Far));
        var paid1 = await PlacePaidAsync(h, e, e.L1, p, 3);
        var pending = await PlaceAsync(h, e, e.L1, p, 1, "+380671110001");
        var paid2 = await PlacePaidAsync(h, e, e.L2, p2, 1, "+380671110002");
        Assert.Equal([1, 2, 3], new[] { paid1.Number, pending.Number, paid2.Number });

        var all = await CrmGet(h, "", e.Owner);
        Assert.Equal("no-store", all.Headers.CacheControl?.ToString());
        var items = (await Expect(all, HttpStatusCode.OK)).GetProperty("items");
        Assert.Equal([3, 2, 1], items.EnumerateArray().Select(i => i.GetProperty("number").GetInt32()).ToArray());

        async Task<int[]> Numbers(string q) =>
            (await Expect(await CrmGet(h, q, e.Owner), HttpStatusCode.OK)).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("number").GetInt32()).ToArray();
        Assert.Equal([3, 1], await Numbers("?status=paid"));
        Assert.Equal([2], await Numbers("?status=pending_payment"));
        Assert.Empty(await Numbers("?status=fulfilled"));
        Assert.Equal([3], await Numbers($"?locationId={e.L2}"));
        Assert.Equal([2], await Numbers("?number=2"));
        Assert.Empty(await Numbers("?number=99"));
        Assert.Equal([3, 2, 1], await Numbers("?status=all"));
        Assert.Equal(2, (await Expect(await CrmGet(h, "/counts", e.Owner), HttpStatusCode.OK)).GetProperty("paid").GetInt32());

        // деталь: рядки, оплата, операція sale, без закупівельних цін
        var detailResp = await CrmGet(h, $"/{paid1.Id}", e.Owner);
        var raw = await detailResp.Content.ReadAsStringAsync();
        var d = (await Expect(detailResp, HttpStatusCode.OK));
        Assert.Equal("paid", d.GetProperty("status").GetString());
        Assert.Equal(300m, d.GetProperty("total").GetDecimal());
        Assert.Equal(1, d.GetProperty("lineCount").GetInt32());
        Assert.Equal(3, d.GetProperty("itemCount").GetInt32());
        var line = d.GetProperty("lines")[0];
        Assert.Equal(p, line.GetProperty("product").GetProperty("id").GetGuid());
        Assert.Equal("Shampoo", line.GetProperty("name").GetString());
        Assert.Equal(100m, line.GetProperty("unitPrice").GetDecimal());
        Assert.Equal("paid", d.GetProperty("payment").GetProperty("status").GetString());
        Assert.Equal(1, d.GetProperty("payment").GetProperty("attempts").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, d.GetProperty("saleOperationId").ValueKind);
        Assert.Equal(JsonValueKind.Null, d.GetProperty("returnOperationId").ValueKind);
        Assert.Equal("web", d.GetProperty("source").GetString());
        Assert.Equal("Main", d.GetProperty("location").GetProperty("name").GetString());
        Assert.False(d.TryGetProperty("purchasePrice", out _));
        Assert.DoesNotContain("purchasePrice", raw);
        Assert.DoesNotContain("unitCost", raw);

        // ефективний статус: прострочений резерв без оплати в процесі -> expired (навіть до відпрацювання воркера)
        await SetOrderAsync(e, pending.Id, o => o.ReservationExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5));
        Assert.Equal([3, 1], await Numbers(""));
        Assert.Empty(await Numbers("?status=pending_payment"));
        var expired = (await Expect(await CrmGet(h, "?status=expired", e.Owner), HttpStatusCode.OK)).GetProperty("items");
        Assert.Equal(1, expired.GetArrayLength());
        Assert.Equal("expired", expired[0].GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, expired[0].GetProperty("reservationExpiresAt").ValueKind);
        Assert.Equal("expired", (await Expect(await CrmGet(h, $"/{pending.Id}", e.Owner), HttpStatusCode.OK)).GetProperty("status").GetString());

        // курсорна пагінація
        var page1 = await Expect(await CrmGet(h, "?status=all&take=2", e.Owner), HttpStatusCode.OK);
        Assert.True(page1.GetProperty("hasMore").GetBoolean());
        var cursor = page1.GetProperty("nextCursor").GetString()!;
        var page2 = await Expect(await CrmGet(h, $"?status=all&take=2&cursor={Uri.EscapeDataString(cursor)}", e.Owner), HttpStatusCode.OK);
        Assert.False(page2.GetProperty("hasMore").GetBoolean());
        Assert.Equal([1], page2.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("number").GetInt32()).ToArray());
        await Expect(await CrmGet(h, "?status=all&take=2&cursor=garbage", e.Owner), HttpStatusCode.UnprocessableEntity, "invalid_cursor");
        await Expect(await CrmGet(h, $"?status=paid&take=2&cursor={Uri.EscapeDataString(cursor)}", e.Owner), HttpStatusCode.UnprocessableEntity, "invalid_cursor");

        // валідація
        await Expect(await CrmGet(h, "?status=bogus", e.Owner), HttpStatusCode.UnprocessableEntity, "invalid_status");
        await Expect(await CrmGet(h, "?from=2030-01-02&to=2030-01-01", e.Owner), HttpStatusCode.UnprocessableEntity, "invalid_range");
        await Expect(await CrmGet(h, "?from=2020-01-01&to=2022-01-01", e.Owner), HttpStatusCode.UnprocessableEntity, "invalid_range");
        await Expect(await CrmGet(h, "?number=abc", e.Owner), HttpStatusCode.UnprocessableEntity, "invalid_number");
        Assert.Empty(await Numbers("?from=2000-01-01&to=2000-06-01&status=all"));

        // sale-операція в журналі складу з productOrderId
        var ops = await Expect(await Crm(h, HttpMethod.Get, $"/operations?kind=sale&locationId={e.L1}", e.Owner), HttpStatusCode.OK);
        Assert.Contains(ops.GetProperty("items").EnumerateArray(), o => o.GetProperty("productOrderId").ValueKind == JsonValueKind.String
            && o.GetProperty("productOrderId").GetGuid() == paid1.Id);
        var mv = await Expect(await Crm(h, HttpMethod.Get, $"/movements?type=sale&locationId={e.L1}", e.Owner), HttpStatusCode.OK);
        Assert.NotEmpty(mv.GetProperty("items").EnumerateArray());
    }

    // ---------- видача ----------

    [SkippableFact]
    public async Task fulfill_paid_is_idempotent_and_rejects_unpaid_and_cancel_of_fulfilled()
    {
        NeedDb();
        var e = await NewEnvAsync();
        var h = NewHost();
        var p = await AddProductAsync(e, "Shampoo", 100m, null, (10, Far));
        var paid = await PlacePaidAsync(h, e, e.L1, p, 2);
        var pending = await PlaceAsync(h, e, e.L1, p, 1, "+380671110001");

        await Expect(await CrmPost(h, pending.Id, "fulfill", e.Owner), HttpStatusCode.Conflict, "order_not_paid");
        var f = await Expect(await CrmPost(h, paid.Id, "fulfill", e.Owner), HttpStatusCode.OK);
        Assert.Equal("fulfilled", f.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, f.GetProperty("fulfilledAt").ValueKind);
        Assert.Equal("Owner", f.GetProperty("fulfilledBy").GetProperty("name").GetString());
        var firstAt = f.GetProperty("fulfilledAt").GetDateTimeOffset();

        var again = await Expect(await CrmPost(h, paid.Id, "fulfill", e.Owner), HttpStatusCode.OK);
        Assert.Equal("fulfilled", again.GetProperty("status").GetString());
        Assert.Equal(firstAt, again.GetProperty("fulfilledAt").GetDateTimeOffset());

        await Expect(await CrmPost(h, paid.Id, "cancel", e.Owner, new { reason = "x" }), HttpStatusCode.Conflict, "order_fulfilled");
        Assert.Equal(0, h.Pay.RefundCalls);
        await Expect(await CrmPost(h, Guid.NewGuid(), "fulfill", e.Owner), HttpStatusCode.NotFound, "product_order_not_found");
        Assert.Equal(0, (await Expect(await CrmGet(h, "/counts", e.Owner), HttpStatusCode.OK)).GetProperty("paid").GetInt32());
    }

    // ---------- скасування ----------

    [SkippableFact]
    public async Task cancel_unpaid_by_staff_frees_reservation_is_idempotent_and_blocked_for_closed_and_in_progress()
    {
        NeedDb();
        var e = await NewEnvAsync();
        var h = NewHost();
        var p = await AddProductAsync(e, "Shampoo", 100m, null, (1, Far));
        var o = await PlaceAsync(h, e, e.L1, p, 1);

        var c = await Expect(await CrmPost(h, o.Id, "cancel", e.Owner, new { reason = "  out of stock  " }), HttpStatusCode.OK);
        Assert.Equal("cancelled", c.GetProperty("order").GetProperty("status").GetString());
        Assert.Equal("staff", c.GetProperty("order").GetProperty("closeReason").GetString());
        Assert.Equal(0m, c.GetProperty("refundAmount").GetDecimal());
        Assert.Equal("out of stock", c.GetProperty("order").GetProperty("cancelNote").GetString());
        Assert.Equal("Owner", c.GetProperty("order").GetProperty("cancelledBy").GetProperty("name").GetString());
        Assert.Equal(0, h.Pay.RefundCalls);

        // повтор - 200 ідемпотентно; публічний вигляд - cancelled/staff
        var replay = await Expect(await CrmPost(h, o.Id, "cancel", e.Owner), HttpStatusCode.OK);
        Assert.Equal("cancelled", replay.GetProperty("order").GetProperty("status").GetString());
        var pub = await Expect(await h.Client.GetAsync($"/api/public/{e.Slug}/product-orders/{o.Token}"), HttpStatusCode.OK);
        Assert.Equal("staff", pub.GetProperty("closeReason").GetString());
        // резерв знято: остання одиниця знову доступна
        var again = await PlaceAsync(h, e, e.L1, p, 1, "+380671110009");

        // скасований клієнтом / прострочений - order_closed; оплата в процесі - payment_in_progress
        await SetOrderAsync(e, again.Id, ord => ord.PaymentStartedAt = DateTimeOffset.UtcNow);
        await Expect(await CrmPost(h, again.Id, "cancel", e.Owner), HttpStatusCode.Conflict, "payment_in_progress");
        await SetOrderAsync(e, again.Id, ord => { ord.PaymentStartedAt = null; ord.ReservationExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1); });
        await Expect(await CrmPost(h, again.Id, "cancel", e.Owner), HttpStatusCode.Conflict, "order_closed");

        await Expect(await CrmPost(h, o.Id, "cancel", e.Owner, new { reason = new string('x', 301) }), HttpStatusCode.UnprocessableEntity, "invalid_reason");
    }

    [SkippableFact]
    public async Task cancel_client_cancelled_order_is_order_closed()
    {
        NeedDb();
        var e = await NewEnvAsync();
        var h = NewHost();
        var p = await AddProductAsync(e, "Shampoo", 100m, null, (5, Far));
        var o = await PlaceAsync(h, e, e.L1, p, 1);
        Assert.Equal(HttpStatusCode.OK, (await h.Client.PostAsync($"/api/public/{e.Slug}/product-orders/{o.Token}/cancel", JsonContent.Create(new { }))).StatusCode);
        await Expect(await CrmPost(h, o.Id, "cancel", e.Owner), HttpStatusCode.Conflict, "order_closed");
    }

    [SkippableFact]
    public async Task cancel_paid_refunds_100_percent_returns_to_same_batches_and_is_idempotent()
    {
        NeedDb();
        var e = await NewEnvAsync();
        var h = NewHost();
        var p = await AddProductAsync(e, "Shampoo", 100m, null, (2, Mid), (8, Far)); // FEFO: 2 з Mid + 1 з Far
        var paid = await PlacePaidAsync(h, e, e.L1, p, 3);
        var afterSale = await PairAsync(e, e.L1, p);
        Assert.Equal(7m, afterSale.Level);
        await AssertInvariantAsync(e, e.L1, p);

        var r = await Expect(await CrmPost(h, paid.Id, "cancel", e.Owner, new { reason = "damaged" }), HttpStatusCode.OK);
        var order = r.GetProperty("order");
        Assert.Equal("refunded", order.GetProperty("status").GetString());
        Assert.Equal("staff", order.GetProperty("closeReason").GetString());
        Assert.Equal(300m, order.GetProperty("refundAmount").GetDecimal());
        Assert.Equal(300m, r.GetProperty("refundAmount").GetDecimal());
        Assert.Equal("refunded", order.GetProperty("payment").GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, order.GetProperty("returnOperationId").ValueKind);
        Assert.Equal("damaged", order.GetProperty("cancelNote").GetString());
        Assert.Equal(1, h.Pay.RefundCalls);

        // рівень і партії відновлено, кожен sale-рух має дзеркальний sale_return у ту саму партію
        Assert.Equal(10m, (await PairAsync(e, e.L1, p)).Level);
        await AssertInvariantAsync(e, e.L1, p);
        await using (var ctx = fx.Db.CreateContext(e.T.TenantId))
        {
            var batches = await ctx.StockBatches.AsNoTracking().Where(b => b.ProductId == p).OrderBy(b => b.ExpiresOn).ToListAsync();
            Assert.Equal([2m, 8m], batches.Select(b => b.Quantity).ToArray());
            var sales = await ctx.StockMovements.AsNoTracking().Where(m => m.Type == StockMovementType.Sale).ToListAsync();
            var returns = await ctx.StockMovements.AsNoTracking().Where(m => m.Type == StockMovementType.SaleReturn).ToListAsync();
            Assert.Equal(sales.Count, returns.Count);
            Assert.Equal(sales.OrderBy(m => m.BatchId).Select(m => (m.BatchId, -m.Quantity, m.UnitCost)).ToArray(),
                returns.OrderBy(m => m.BatchId).Select(m => (m.BatchId, m.Quantity, m.UnitCost)).ToArray());
            Assert.Equal(1, await ctx.StockOperations.CountAsync(o => o.ProductOrderId == paid.Id && o.Kind == StockOperationKind.SaleReturn));
            Assert.Equal(1, await ctx.Payments.CountAsync(x => x.ProductOrderId == paid.Id && x.Status == PaymentStatus.Refunded));
            var po = await ctx.ProductOrders.AsNoTracking().FirstAsync(x => x.Id == paid.Id);
            Assert.Null(po.RefundStartedAt);
            Assert.NotNull(po.CancelledByUserId);
        }

        // журнал складу
        var ops = await Expect(await Crm(h, HttpMethod.Get, $"/operations?kind=sale_return&locationId={e.L1}", e.Owner), HttpStatusCode.OK);
        var opItem = Assert.Single(ops.GetProperty("items").EnumerateArray());
        Assert.Equal(paid.Id, opItem.GetProperty("productOrderId").GetGuid());
        var mv = await Expect(await Crm(h, HttpMethod.Get, $"/movements?type=sale_return&locationId={e.L1}", e.Owner), HttpStatusCode.OK);
        Assert.Equal(2, mv.GetProperty("items").GetArrayLength());

        // ідемпотентність: повтор - 200 без нового повернення й без нових рухів
        var replay = await Expect(await CrmPost(h, paid.Id, "cancel", e.Owner), HttpStatusCode.OK);
        Assert.Equal("refunded", replay.GetProperty("order").GetProperty("status").GetString());
        Assert.Equal(300m, replay.GetProperty("refundAmount").GetDecimal());
        Assert.Equal(1, h.Pay.RefundCalls);
        Assert.Equal(10m, (await PairAsync(e, e.L1, p)).Level);

        await Expect(await CrmPost(h, paid.Id, "fulfill", e.Owner), HttpStatusCode.Conflict, "order_not_paid");
        // публічно: refunded/staff
        var pub = await Expect(await h.Client.GetAsync($"/api/public/{e.Slug}/product-orders/{paid.Token}"), HttpStatusCode.OK);
        Assert.Equal("refunded", pub.GetProperty("status").GetString());
        Assert.Equal("staff", pub.GetProperty("closeReason").GetString());
        Assert.Equal(0, (await Expect(await CrmGet(h, "/counts", e.Owner), HttpStatusCode.OK)).GetProperty("paid").GetInt32());
    }

    [SkippableFact]
    public async Task refund_failed_changes_nothing_then_retry_succeeds_and_deficit_is_untouched()
    {
        NeedDb();
        var e = await NewEnvAsync();
        var h = NewHost();
        var p = await AddProductAsync(e, "Shampoo", 100m, null, (5, Far));
        var paid = await PlacePaidAsync(h, e, e.L1, p, 2);
        h.Pay.FailRefund = true;
        await Expect(await CrmPost(h, paid.Id, "cancel", e.Owner), HttpStatusCode.PaymentRequired, "refund_failed");
        var still = await Expect(await CrmGet(h, $"/{paid.Id}", e.Owner), HttpStatusCode.OK);
        Assert.Equal("paid", still.GetProperty("status").GetString());
        Assert.False(still.GetProperty("refundInProgress").GetBoolean());
        Assert.Equal(JsonValueKind.Null, still.GetProperty("returnOperationId").ValueKind);
        Assert.Equal(3m, (await PairAsync(e, e.L1, p)).Level);
        await using (var ctx = fx.Db.CreateContext(e.T.TenantId))
        {
            Assert.Equal(0, await ctx.StockOperations.CountAsync(o => o.Kind == StockOperationKind.SaleReturn));
            Assert.Equal("paid", (await ctx.Payments.AsNoTracking().FirstAsync(x => x.ProductOrderId == paid.Id)).Status.ToString().ToLowerInvariant());
        }

        h.Pay.FailRefund = false;
        var ok = await Expect(await CrmPost(h, paid.Id, "cancel", e.Owner), HttpStatusCode.OK);
        Assert.Equal("refunded", ok.GetProperty("order").GetProperty("status").GetString());
        Assert.Equal(5m, (await PairAsync(e, e.L1, p)).Level);
        await using var ctx2 = fx.Db.CreateContext(e.T.TenantId);
        Assert.Equal(0m, (await ctx2.StockLevels.AsNoTracking().FirstAsync(l => l.ProductId == p)).ShortageQuantity);
        await AssertInvariantAsync(e, e.L1, p);
    }

    [SkippableFact]
    public async Task refund_in_progress_blocks_fulfill_and_cancel_until_stale()
    {
        NeedDb();
        var e = await NewEnvAsync();
        var h = NewHost();
        var p = await AddProductAsync(e, "Shampoo", 100m, null, (5, Far));
        var paid = await PlacePaidAsync(h, e, e.L1, p, 1);
        await SetOrderAsync(e, paid.Id, o => o.RefundStartedAt = DateTimeOffset.UtcNow);
        var view = await Expect(await CrmGet(h, $"/{paid.Id}", e.Owner), HttpStatusCode.OK);
        Assert.True(view.GetProperty("refundInProgress").GetBoolean());
        await Expect(await CrmPost(h, paid.Id, "fulfill", e.Owner), HttpStatusCode.Conflict, "refund_in_progress");
        await Expect(await CrmPost(h, paid.Id, "cancel", e.Owner), HttpStatusCode.Conflict, "refund_in_progress");
        Assert.Equal(0, h.Pay.RefundCalls);

        // завис claim (старший за PaymentStaleSeconds) - повтор доводить повернення до кінця
        await SetOrderAsync(e, paid.Id, o => o.RefundStartedAt = DateTimeOffset.UtcNow.AddMinutes(-10));
        var done = await Expect(await CrmPost(h, paid.Id, "cancel", e.Owner), HttpStatusCode.OK);
        Assert.Equal("refunded", done.GetProperty("order").GetProperty("status").GetString());
        Assert.Equal(1, h.Pay.RefundCalls);
    }

    [SkippableFact]
    public async Task concurrent_fulfill_and_cancel_paid_exactly_one_wins_and_stock_stays_consistent()
    {
        NeedDb();
        var e = await NewEnvAsync();
        var h = NewHost();
        var p = await AddProductAsync(e, "Shampoo", 100m, null, (5, Far));
        for (var i = 0; i < 3; i++)
        {
            var paid = await PlacePaidAsync(h, e, e.L1, p, 1, $"+38067111100{i}");
            var fulfill = CrmPost(h, paid.Id, "fulfill", e.Owner);
            var cancel = CrmPost(h, paid.Id, "cancel", e.Owner);
            await Task.WhenAll(fulfill, cancel);
            var f = (await fulfill).StatusCode;
            var c = (await cancel).StatusCode;
            var final = (await Expect(await CrmGet(h, $"/{paid.Id}", e.Owner), HttpStatusCode.OK)).GetProperty("status").GetString();
            if (final == "fulfilled") Assert.Equal(HttpStatusCode.Conflict, c);
            else
            {
                Assert.Equal("refunded", final);
                Assert.Equal(HttpStatusCode.Conflict, f);
            }
            await AssertInvariantAsync(e, e.L1, p);
        }
    }

    // ---------- reconcile ----------

    [SkippableFact]
    public async Task reconcile_payment_completes_stuck_payment_and_rejects_other_states()
    {
        NeedDb();
        var e = await NewEnvAsync();
        var h = NewHost();
        var p = await AddProductAsync(e, "Shampoo", 100m, null, (5, Far));
        var o = await PlaceAsync(h, e, e.L1, p, 2);
        await Expect(await CrmPost(h, o.Id, "reconcile-payment", e.Owner), HttpStatusCode.Conflict, "nothing_to_reconcile");

        h.Pay.ThrowAfterChargeOnce();
        try { await h.Client.PostAsync($"/api/public/{e.Slug}/product-orders/{o.Token}/pay", JsonContent.Create(new { })); } catch (Exception) { /* збій провайдера */ }
        var mid = await Expect(await CrmGet(h, $"/{o.Id}", e.Owner), HttpStatusCode.OK);
        Assert.True(mid.GetProperty("paymentInProgress").GetBoolean());
        await Expect(await CrmPost(h, o.Id, "reconcile-payment", e.Owner), HttpStatusCode.Conflict, "payment_in_progress"); // свіжа оплата
        await Expect(await CrmPost(h, o.Id, "cancel", e.Owner), HttpStatusCode.Conflict, "payment_in_progress");

        await SetOrderAsync(e, o.Id, x => x.PaymentStartedAt = DateTimeOffset.UtcNow.AddMinutes(-10));
        var done = await Expect(await CrmPost(h, o.Id, "reconcile-payment", e.Owner), HttpStatusCode.OK);
        Assert.Equal("paid", done.GetProperty("status").GetString());
        Assert.False(done.GetProperty("paymentInProgress").GetBoolean());
        Assert.Single(h.Pay.Charged); // той самий PaymentId: одне списання
        Assert.Equal(3m, (await PairAsync(e, e.L1, p)).Level);
        await AssertInvariantAsync(e, e.L1, p);

        await Expect(await CrmPost(h, o.Id, "reconcile-payment", e.Owner), HttpStatusCode.Conflict, "nothing_to_reconcile");
    }

    [SkippableFact]
    public async Task reconcile_when_stock_vanished_refunds_and_closes_as_stock_unavailable()
    {
        NeedDb();
        var e = await NewEnvAsync();
        var h = NewHost();
        var p = await AddProductAsync(e, "Shampoo", 100m, null, (2, Far));
        var o = await PlaceAsync(h, e, e.L1, p, 2);
        h.Pay.ThrowAfterChargeOnce();
        try { await h.Client.PostAsync($"/api/public/{e.Slug}/product-orders/{o.Token}/pay", JsonContent.Create(new { })); } catch (Exception) { }
        // товар зник між списанням і фіналізацією
        await using (var ctx = fx.Db.CreateContext(e.T.TenantId))
        {
            await ctx.Database.ExecuteSqlRawAsync("UPDATE beauty_stock_batches SET quantity = 0");
            await ctx.Database.ExecuteSqlRawAsync("UPDATE beauty_stock_levels SET quantity = 0");
        }
        await SetOrderAsync(e, o.Id, x => x.PaymentStartedAt = DateTimeOffset.UtcNow.AddMinutes(-10));
        var r = await Expect(await CrmPost(h, o.Id, "reconcile-payment", e.Owner), HttpStatusCode.OK);
        Assert.Equal("refunded", r.GetProperty("status").GetString());
        Assert.Equal("stock_unavailable", r.GetProperty("closeReason").GetString());
        Assert.Equal(200m, r.GetProperty("refundAmount").GetDecimal());
        Assert.Equal(1, h.Pay.RefundCalls);
    }

    // ---------- права, маски, scope ----------

    [SkippableFact]
    public async Task permissions_scope_masks_and_client_id_follow_the_contract()
    {
        NeedDb();
        var e = await NewEnvAsync();
        var h = NewHost();
        var p1 = await AddProductAsync(e, "Shampoo", 100m, null, (10, Far));
        var p2 = await AddProductAsync(e, "Cream", 50m, e.L2, (10, Far));
        var o1 = await PlacePaidAsync(h, e, e.L1, p1, 1);
        var o2 = await PlacePaidAsync(h, e, e.L2, p2, 1, "+380671110002");

        // specialist-пресет: view=own (лише заклад профілю), manage=none
        var list = await Expect(await CrmGet(h, "?status=all", e.Spec), HttpStatusCode.OK);
        Assert.Equal([o1.Number], list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("number").GetInt32()).ToArray());
        Assert.Equal(1, (await Expect(await CrmGet(h, "/counts", e.Spec), HttpStatusCode.OK)).GetProperty("paid").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await CrmGet(h, $"/{o1.Id}", e.Spec)).StatusCode);
        await Expect(await CrmGet(h, $"/{o2.Id}", e.Spec), HttpStatusCode.NotFound, "product_order_not_found");
        await Expect(await CrmGet(h, $"?locationId={e.L2}", e.Spec), HttpStatusCode.NotFound, "location_not_found");
        foreach (var action in new[] { "fulfill", "cancel", "reconcile-payment" })
        {
            var r = await CrmPost(h, o1.Id, action, e.Spec);
            var body = await Expect(r, HttpStatusCode.Forbidden, "forbidden_role");
            Assert.Equal("inventory.manage", body.GetProperty("permission").GetString());
            Assert.Equal("no-store", r.Headers.CacheControl?.ToString());
        }
        Assert.Equal(0, h.Pay.RefundCalls);

        // manage=own: власний заклад - так, чужий - 404 без розкриття
        await Expect(await CrmPost(h, o2.Id, "fulfill", e.SpecOwn), HttpStatusCode.NotFound, "product_order_not_found");
        await Expect(await CrmPost(h, o2.Id, "cancel", e.SpecOwn), HttpStatusCode.NotFound, "product_order_not_found");
        Assert.Equal("fulfilled", (await Expect(await CrmPost(h, o1.Id, "fulfill", e.SpecOwn), HttpStatusCode.OK)).GetProperty("status").GetString());

        // телефон і clientId
        var full = await CrmGet(h, $"/{o2.Id}", e.AdminFull);
        var fullBody = await full.Content.ReadAsStringAsync();
        var fj = (await Expect(full, HttpStatusCode.OK));
        Assert.Equal("+380671110002", fj.GetProperty("customer").GetProperty("phone").GetString());
        Assert.False(fj.GetProperty("customer").TryGetProperty("phoneMasked", out _));
        Assert.True(fj.TryGetProperty("clientId", out var cid) && cid.ValueKind == JsonValueKind.String);
        Assert.Contains("Олена", fullBody);

        var masked = await CrmGet(h, $"/{o2.Id}", e.AdminMasked);
        var maskedBody = await masked.Content.ReadAsStringAsync();
        var mj = await Expect(masked, HttpStatusCode.OK);
        Assert.False(mj.GetProperty("customer").TryGetProperty("phone", out _));
        Assert.EndsWith("02", mj.GetProperty("customer").GetProperty("phoneMasked").GetString());
        Assert.DoesNotContain("380671110002", maskedBody);
        Assert.False(mj.TryGetProperty("clientId", out _)); // clients.view = own
        var maskedList = await CrmGet(h, "?status=all", e.AdminMasked);
        Assert.DoesNotContain("380671110002", await maskedList.Content.ReadAsStringAsync());
        Assert.DoesNotContain("+38067111", await maskedList.Content.ReadAsStringAsync());

        // без токена
        Assert.Equal(HttpStatusCode.Unauthorized, (await CrmGet(h, "", null)).StatusCode);
    }

    [SkippableFact]
    public async Task module_disabled_is_403_on_every_route_before_forbidden_role()
    {
        NeedDb();
        var e = await NewEnvAsync(modules: [.. BeautyModules.All]); // без beauty_inventory
        var h = NewHost();
        var id = Guid.NewGuid();
        foreach (var token in new[] { e.Owner, e.Spec })
            foreach (var (m, path) in new[]
                     {
                         (HttpMethod.Get, "/product-orders"), (HttpMethod.Get, "/product-orders/counts"), (HttpMethod.Get, $"/product-orders/{id}"),
                         (HttpMethod.Post, $"/product-orders/{id}/fulfill"), (HttpMethod.Post, $"/product-orders/{id}/cancel"),
                         (HttpMethod.Post, $"/product-orders/{id}/reconcile-payment"),
                     })
            {
                var r = await Crm(h, m, path, token, m == HttpMethod.Post ? new { } : null);
                await Expect(r, HttpStatusCode.Forbidden, "module_disabled");
                Assert.Equal("no-store", r.Headers.CacheControl?.ToString());
            }
    }

    [SkippableFact]
    public async Task cross_tenant_orders_are_invisible_rls()
    {
        NeedDb();
        var a = await NewEnvAsync();
        var b = await NewEnvAsync();
        var h = NewHost();
        var p = await AddProductAsync(a, "Shampoo", 100m, null, (5, Far));
        var o = await PlacePaidAsync(h, a, a.L1, p, 1);

        await Expect(await CrmGet(h, $"/{o.Id}", b.Owner), HttpStatusCode.NotFound, "product_order_not_found");
        foreach (var action in new[] { "fulfill", "cancel", "reconcile-payment" })
            await Expect(await CrmPost(h, o.Id, action, b.Owner), HttpStatusCode.NotFound, "product_order_not_found");
        Assert.Empty((await Expect(await CrmGet(h, "?status=all", b.Owner), HttpStatusCode.OK)).GetProperty("items").EnumerateArray());
        Assert.Equal(0, (await Expect(await CrmGet(h, "/counts", b.Owner), HttpStatusCode.OK)).GetProperty("paid").GetInt32());
        Assert.Equal("paid", (await Expect(await CrmGet(h, $"/{o.Id}", a.Owner), HttpStatusCode.OK)).GetProperty("status").GetString()); // не зачеплено
        Assert.Equal(0, h.Pay.RefundCalls);
    }

    [SkippableFact]
    public async Task crm_actions_work_when_product_orders_feature_is_disabled()
    {
        NeedDb();
        var e = await NewEnvAsync();
        var on = NewHost(enabled: true);
        var p = await AddProductAsync(e, "Shampoo", 100m, null, (5, Far));
        var paid1 = await PlacePaidAsync(on, e, e.L1, p, 1);
        var paid2 = await PlacePaidAsync(on, e, e.L1, p, 1, "+380671110002");

        var off = NewHost(enabled: false);
        Assert.Equal(2, (await Expect(await CrmGet(off, "/counts", e.Owner), HttpStatusCode.OK)).GetProperty("paid").GetInt32());
        Assert.Equal("fulfilled", (await Expect(await CrmPost(off, paid1.Id, "fulfill", e.Owner), HttpStatusCode.OK)).GetProperty("status").GetString());
        var refunded = await Expect(await CrmPost(off, paid2.Id, "cancel", e.Owner), HttpStatusCode.OK);
        Assert.Equal("refunded", refunded.GetProperty("order").GetProperty("status").GetString());
        Assert.Equal(1, off.Pay.RefundCalls);
        await AssertInvariantAsync(e, e.L1, p);
    }

    [SkippableFact]
    public async Task write_routes_are_rate_limited_per_user()
    {
        NeedDb();
        var e = await NewEnvAsync();
        var h = NewHost(writePerMinute: "10");
        var id = Guid.NewGuid();
        for (var i = 0; i < 10; i++)
            await Expect(await CrmPost(h, id, "fulfill", e.Owner), HttpStatusCode.NotFound, "product_order_not_found");
        var r = await CrmPost(h, id, "fulfill", e.Owner);
        await Expect(r, HttpStatusCode.TooManyRequests, "rate_limited");
        Assert.Equal("60", r.Headers.RetryAfter?.Delta?.TotalSeconds.ToString());
        // читання не лімітується
        Assert.Equal(HttpStatusCode.OK, (await CrmGet(h, "/counts", e.Owner)).StatusCode);
    }
}
