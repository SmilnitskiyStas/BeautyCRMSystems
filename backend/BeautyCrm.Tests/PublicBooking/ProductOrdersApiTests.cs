using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyProductOrders;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Tests.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using static BeautyCrm.Tests.Auth.AuthApiFixture;

namespace BeautyCrm.Tests.PublicBooking;

/// <summary>Фейкова оплата: рахує списання за PaymentId (гроші = унікальні PaymentId), відмови, затримку, збій після списання.</summary>
internal sealed class FakeOrderPayments : IPaymentService
{
    public readonly ConcurrentDictionary<Guid, int> Charged = new();
    public int ChargeCalls;
    public int RefundCalls;
    public bool DeclineAll;
    public bool FailRefund;
    public int ChargeDelayMs;
    /// <summary>Номер виклику Charge (з 1) -> затримка мс / відмова: сценарії «повільний A, швидкий B».</summary>
    public Func<int, int>? DelayFor;
    public Func<int, bool>? DeclineFor;
    /// <summary>Виняток замість результату повернення (невідомий результат); хук під час повернення (зміна стану замовлення).</summary>
    public bool ThrowOnRefund;
    public Func<Task>? OnRefund;
    private int _throwAfterCharge;
    public void ThrowAfterChargeOnce() => _throwAfterCharge = 1;

    public Task<PaymentResult> ChargeAsync(Guid appointmentId, decimal amount, CancellationToken ct) =>
        Task.FromResult(new PaymentResult(true, "fake_appt", null));

    public async Task<PaymentResult> ChargeAsync(PaymentCharge c, CancellationToken ct)
    {
        var call = Interlocked.Increment(ref ChargeCalls);
        if (ChargeDelayMs > 0) await Task.Delay(ChargeDelayMs, ct);
        if (DelayFor?.Invoke(call) is > 0 and var d) await Task.Delay(d, ct);
        if (DeclineAll || DeclineFor?.Invoke(call) == true) return new PaymentResult(false, null, "declined");
        Charged.AddOrUpdate(c.PaymentId, 1, (_, v) => v + 1);
        if (Interlocked.Exchange(ref _throwAfterCharge, 0) == 1) throw new InvalidOperationException("injected failure after charge");
        return new PaymentResult(true, $"fake_{c.PaymentId:N}", null);
    }

    public async Task<RefundResult> RefundAsync(Guid paymentId, decimal amount, CancellationToken ct)
    {
        Interlocked.Increment(ref RefundCalls);
        if (OnRefund is not null) await OnRefund();
        if (ThrowOnRefund) throw new InvalidOperationException("injected refund failure");
        return await Task.FromResult(FailRefund ? new RefundResult(false, null, "down") : new RefundResult(true, "fake_refund", null));
    }
}

internal sealed class CapturingLogProvider(ConcurrentQueue<string> sink) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new L(sink);
    public void Dispose() { }

    private sealed class L(ConcurrentQueue<string> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            sink.Enqueue(formatter(state, exception) + (exception is null ? "" : " " + exception));
    }
}

/// <summary>
/// TASK-902 (ADR-026, §33): публічні маршрути замовлень товарів P1-P5 на реальному PostgreSQL (роль без BYPASSRLS) через повний HTTP-конвеєр.
/// Оплата - фейк (рахує списання за PaymentId); прострочення резерву/зависання оплати відтворюються прямим UPDATE у БД.
/// </summary>
public sealed class ProductOrdersApiTests(AuthApiFixture fx) : IClassFixture<AuthApiFixture>, IDisposable
{
    private readonly List<IDisposable> _disposables = [];
    private static readonly DateOnly Far = new(2099, 1, 1);
    private static readonly DateOnly Expired = new(2020, 1, 1);
    private const string Name = "Олена Тестова";
    private const string Phone = "+380671234567";

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
    }

    private void NeedDb() => Skip.If(fx.SkipReason is not null, fx.SkipReason);

    private sealed record Env(TenantSeed T, Guid Loc, Guid Owner)
    {
        public string Slug => T.Slug;
    }

    private sealed record Host(HttpClient Client, FakeOrderPayments Pay, ConcurrentQueue<string> Logs);

    private Host NewHost(string? enabled = "true", Action<IWebHostBuilder>? configure = null, FakeOrderPayments? pay = null, bool fake = true)
    {
        pay ??= new FakeOrderPayments();
        var logs = new ConcurrentQueue<string>();
        var f = fx.CreateFactory(10_000).WithWebHostBuilder(b =>
        {
            if (enabled is not null) b.UseSetting("ProductOrders:Enabled", enabled);
            b.UseSetting("PublicBooking:RateLimit:ReadPermit", "100000");
            b.UseSetting("PublicBooking:RateLimit:TokenPermit", "100000");
            b.UseSetting("PublicBooking:RateLimit:WritePermit", "100000");
            b.ConfigureLogging(l => l.AddProvider(new CapturingLogProvider(logs)));
            if (fake)
                b.ConfigureTestServices(s =>
                {
                    s.RemoveAll<IPaymentService>();
                    s.AddSingleton<IPaymentService>(pay);
                });
            configure?.Invoke(b);
        });
        _disposables.Add(f);
        return new Host(f.CreateClient(), pay, logs);
    }

    // ---------- наповнення ----------

    private async Task<Env> NewEnvAsync(string[]? modules = null)
    {
        var t = await fx.CreateTenantAsync(modules ?? [.. BeautyModules.Known]);
        await using var ctx = fx.Db.CreateContext(t.TenantId);
        var loc = new Location { Name = "Main", Address = "Street 1", Timezone = "Europe/Kyiv" };
        ctx.Locations.Add(loc);
        await ctx.SaveChangesAsync();
        return new Env(t, loc.Id, t.OwnerId);
    }

    private async Task<Guid> AddProductAsync(Env e, string name, decimal? price, decimal stock = 0, ProductPurpose purpose = ProductPurpose.Sale,
        bool active = true, DateOnly? expires = null)
    {
        Guid id;
        await using (var ctx = fx.Db.CreateContext(e.T.TenantId))
        {
            var p = new Product { Name = name, Unit = ProductUnit.Pcs, Purpose = purpose, SalePrice = price, IsActive = active, UpdatedByUserId = e.Owner };
            ctx.Products.Add(p);
            await ctx.SaveChangesAsync();
            id = p.Id;
        }
        if (stock > 0) await AddBatchAsync(e, id, stock, expires ?? Far);
        return id;
    }

    private async Task AddBatchAsync(Env e, Guid productId, decimal qty, DateOnly? expires, DateTimeOffset? receivedAt = null)
    {
        await using var ctx = fx.Db.CreateContext(e.T.TenantId);
        var at = receivedAt ?? DateTimeOffset.UtcNow.AddDays(-1);
        var op = new StockOperation { Id = Guid.NewGuid(), Kind = StockOperationKind.Receipt, LocationId = e.Loc, CreatedByUserId = e.Owner, CreatedAt = at };
        var batch = new StockBatch
        {
            Id = Guid.NewGuid(), LocationId = e.Loc, ProductId = productId, OperationId = op.Id, ReceivedAt = at, ExpiresOn = expires,
            InitialQuantity = qty, Quantity = qty, UnitCost = 10m, CreatedAt = at, UpdatedAt = at,
        };
        ctx.StockOperations.Add(op);
        ctx.StockBatches.Add(batch);
        ctx.StockMovements.Add(new StockMovement
        {
            Id = Guid.NewGuid(), OperationId = op.Id, LocationId = e.Loc, ProductId = productId, BatchId = batch.Id, Type = StockMovementType.Receipt,
            Quantity = qty, UnitCost = 10m, CreatedByUserId = e.Owner, OccurredAt = at,
        });
        var level = await ctx.StockLevels.FirstOrDefaultAsync(l => l.LocationId == e.Loc && l.ProductId == productId);
        if (level is null) ctx.StockLevels.Add(new StockLevel { LocationId = e.Loc, ProductId = productId, Quantity = qty, CreatedAt = at, UpdatedAt = at });
        else level.Quantity += qty;
        await ctx.SaveChangesAsync();
    }

    /// <summary>Інваріант пари: level = Σ партій = Σ рухів (без нестачі). Повертає рівень.</summary>
    private async Task<decimal> AssertInvariantAsync(Env e, Guid productId)
    {
        await using var ctx = fx.Db.CreateContext(e.T.TenantId);
        var level = await ctx.StockLevels.AsNoTracking().Where(l => l.LocationId == e.Loc && l.ProductId == productId).Select(l => l.Quantity).FirstOrDefaultAsync();
        var batches = await ctx.StockBatches.AsNoTracking().Where(b => b.LocationId == e.Loc && b.ProductId == productId).SumAsync(b => (decimal?)b.Quantity) ?? 0m;
        var moves = await ctx.StockMovements.AsNoTracking().Where(m => m.LocationId == e.Loc && m.ProductId == productId && !m.IsShortage).SumAsync(m => (decimal?)m.Quantity) ?? 0m;
        Assert.Equal(level, batches);
        Assert.Equal(level, moves);
        return level;
    }

    private static object Line(Guid productId, object quantity) => new { productId, quantity };

    private static async Task<JsonElement> J(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task<JsonElement> Expect(HttpResponseMessage r, HttpStatusCode status, string? code = null)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == status, $"expected {status}, got {(int)r.StatusCode}: {body}");
        var j = JsonDocument.Parse(body).RootElement.Clone();
        if (code is not null) Assert.Equal(code, j.GetProperty("code").GetString());
        return j;
    }

    private Task<HttpResponseMessage> CreateRawAsync(Host h, Env e, object[] lines, string phone = Phone, string name = Name, decimal? expected = null,
        string? key = null, string slug = "", Action<HttpRequestMessage>? tweak = null, object? extra = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["locationId"] = e.Loc, ["customer"] = new { name, phone }, ["lines"] = lines,
        };
        if (expected is not null) body["expectedTotal"] = expected;
        if (extra is not null) foreach (var p in extra.GetType().GetProperties()) body[p.Name] = p.GetValue(extra);
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/public/{(slug == "" ? e.Slug : slug)}/product-orders") { Content = JsonContent.Create(body, options: Json) };
        if (key != "none") req.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString("N"));
        tweak?.Invoke(req);
        return h.Client.SendAsync(req);
    }

    private async Task<(string Token, JsonElement Order)> CreateAsync(Host h, Env e, object[] lines, string phone = Phone, decimal? expected = null)
    {
        var j = await Expect(await CreateRawAsync(h, e, lines, phone, expected: expected), HttpStatusCode.Created);
        return (j.GetProperty("publicToken").GetString()!, j.GetProperty("order"));
    }

    private static string RandPhone() => "+38067" + Random.Shared.Next(1_000_000, 9_999_999);

    private Task<HttpResponseMessage> Post(Host h, Env e, string token, string action) =>
        h.Client.PostAsync($"/api/public/{e.Slug}/product-orders/{token}/{action}", JsonContent.Create(new { }));

    private Task<HttpResponseMessage> GetOrder(Host h, Env e, string token) => h.Client.GetAsync($"/api/public/{e.Slug}/product-orders/{token}");

    private async Task SetOrderAsync(Env e, string token, Action<ProductOrder> change)
    {
        await using var ctx = fx.Db.CreateContext(e.T.TenantId);
        var hash = BeautyCrm.Application.Features.BeautyPublicBooking.PublicTokenService.Hash(token);
        var o = await ctx.ProductOrders.FirstAsync(x => x.PublicTokenHash == hash);
        change(o);
        await ctx.SaveChangesAsync();
    }

    // ---------- повний шлях ----------

    [SkippableFact]
    public async Task full_path_catalog_order_pay_stock_decreased_fefo_and_client_created_only_after_payment()
    {
        NeedDb();
        var e = await NewEnvAsync();
        var h = NewHost();
        var p = await AddProductAsync(e, "Shampoo", 420m);
        await AddBatchAsync(e, p, 3, new DateOnly(2098, 1, 1)); // раніший строк -> перша
        await AddBatchAsync(e, p, 5, Far);
        var other = await AddProductAsync(e, "Cream", 199m, stock: 4);

        var catalog = await Expect(await h.Client.GetAsync($"/api/public/{e.Slug}/locations/{e.Loc}/products"), HttpStatusCode.OK);
        Assert.Equal(2, catalog.GetProperty("items").GetArrayLength());
        Assert.Equal(15, catalog.GetProperty("reservationMinutes").GetInt32());
        Assert.False(catalog.GetProperty("truncated").GetBoolean());

        var (token, order) = await CreateAsync(h, e, [Line(p, 4), Line(other, 1)], expected: 4 * 420m + 199m);
        Assert.Equal(43, token.Length);
        Assert.Equal("pending_payment", order.GetProperty("status").GetString());
        Assert.Equal(1879m, order.GetProperty("total").GetDecimal());
        Assert.True(order.GetProperty("canPay").GetBoolean());
        var raw = order.GetRawText();
        Assert.DoesNotContain("Олена", raw);
        Assert.DoesNotContain("380671234567", raw);

        await using (var ctx = fx.Db.CreateContext(e.T.TenantId))
            Assert.False(await ctx.Clients.AnyAsync(c => c.Phone == Phone), "клієнт не створюється до оплати");

        var view = await GetOrder(h, e, token);
        Assert.Equal("no-store", view.Headers.CacheControl?.ToString());
        Assert.Equal("pending_payment", (await Expect(view, HttpStatusCode.OK)).GetProperty("status").GetString());

        var paid = await Expect(await Post(h, e, token, "pay"), HttpStatusCode.OK);
        Assert.Equal("paid", paid.GetProperty("order").GetProperty("status").GetString());
        Assert.Single(h.Pay.Charged);

        // FEFO: 3 з ранішої партії + 1 з пізнішої
        await using (var ctx = fx.Db.CreateContext(e.T.TenantId))
        {
            var batches = await ctx.StockBatches.AsNoTracking().Where(b => b.ProductId == p).OrderBy(b => b.ExpiresOn).Select(b => b.Quantity).ToListAsync();
            Assert.Equal(new[] { 0m, 4m }, batches);
            var op = await ctx.StockOperations.AsNoTracking().SingleAsync(o => o.Kind == StockOperationKind.Sale);
            Assert.Null(op.CreatedByUserId);
            Assert.NotNull(op.ProductOrderId);
            Assert.Equal(3, await ctx.StockMovements.CountAsync(m => m.Type == StockMovementType.Sale));
            Assert.True(await ctx.Clients.AnyAsync(c => c.Phone == Phone));
            Assert.Equal(1, await ctx.Payments.CountAsync(x => x.ProductOrderId != null && x.Status == PaymentStatus.Paid));
        }
        Assert.Equal(4m, await AssertInvariantAsync(e, p));
        Assert.Equal(3m, await AssertInvariantAsync(e, other));

        // повтор оплати - 200 + Idempotent-Replayed, без другого списання
        var again = await Post(h, e, token, "pay");
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal("true", again.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Single(h.Pay.Charged);
        Assert.Equal(1, h.Pay.ChargeCalls);

        // логи без ПД, токена, Idempotency-Key
        var all = string.Join('\n', h.Logs);
        Assert.DoesNotContain("Олена", all);
        Assert.DoesNotContain("380671234567", all);
        Assert.DoesNotContain(token, all);
        Assert.Contains("product_order_created", all);
        Assert.Contains("product_order_payment", all);
    }

    // ---------- наявність, резерв, гонки ----------

    [SkippableFact]
    public async Task insufficient_stock_is_422_with_product_ids_and_no_quantities()
    {
        NeedDb();
        var e = await NewEnvAsync();
        var h = NewHost();
        var p = await AddProductAsync(e, "Shampoo", 100m, stock: 2);
        var j = await Expect(await CreateRawAsync(h, e, [Line(p, 3)]), HttpStatusCode.UnprocessableEntity, "insufficient_stock");
        Assert.Equal(p, j.GetProperty("productIds")[0].GetGuid());
        Assert.DoesNotContain("available", j.GetRawText());
    }

    [SkippableFact]
    public async Task two_parallel_orders_for_last_unit_exactly_one_is_created()
    {
        NeedDb();
        var e = await NewEnvAsync();
        var h = NewHost();
        var p = await AddProductAsync(e, "Last one", 100m, stock: 1);
        var rs = await Task.WhenAll(CreateRawAsync(h, e, [Line(p, 1)], RandPhone()), CreateRawAsync(h, e, [Line(p, 1)], RandPhone()));
        Assert.Equal(new[] { HttpStatusCode.Created, HttpStatusCode.UnprocessableEntity }, rs.Select(r => r.StatusCode).OrderBy(x => x).ToArray());
        await using var ctx = fx.Db.CreateContext(e.T.TenantId);
        Assert.Equal(1, await ctx.ProductOrders.CountAsync());
    }

    [SkippableFact]
    public async Task reservation_blocks_others_until_ttl_then_expired_order_cannot_be_paid()
    {
        NeedDb();
        var e = await NewEnvAsync();
        var h = NewHost();
        var p = await AddProductAsync(e, "One", 100m, stock: 1);
        var (token, _) = await CreateAsync(h, e, [Line(p, 1)], RandPhone());
        await Expect(await CreateRawAsync(h, e, [Line(p, 1)], RandPhone()), HttpStatusCode.UnprocessableEntity, "insufficient_stock");
        var cat = await Expect(await h.Client.GetAsync($"/api/public/{e.Slug}/locations/{e.Loc}/products"), HttpStatusCode.OK);
        Assert.False(cat.GetProperty("items")[0].GetProperty("inStock").GetBoolean()); // резерв враховано в inStock

        await SetOrderAsync(e, token, o => o.ReservationExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1));
        var view = await Expect(await GetOrder(h, e, token), HttpStatusCode.OK);
        Assert.Equal("expired", view.GetProperty("status").GetString()); // ефективний статус без воркера
        Assert.Equal("expired", view.GetProperty("closeReason").GetString());
        await CreateAsync(h, e, [Line(p, 1)], RandPhone()); // прострочений резерв не блокує
        var pay = await Expect(await Post(h, e, token, "pay"), HttpStatusCode.Conflict, "order_expired");
        Assert.Equal("expired", pay.GetProperty("order").GetProperty("status").GetString());
        Assert.Equal(0, h.Pay.ChargeCalls);
    }

    [SkippableFact]
    public async Task cancel_releases_reservation_repeat_is_replayed_and_paid_cannot_be_cancelled()
    {
        NeedDb();
        var e = await NewEnvAsync();
        var h = NewHost();
        var p = await AddProductAsync(e, "One", 100m, stock: 1);
        var (token, _) = await CreateAsync(h, e, [Line(p, 1)], RandPhone());
        var c1 = await Expect(await Post(h, e, token, "cancel"), HttpStatusCode.OK);
        Assert.Equal("cancelled", c1.GetProperty("order").GetProperty("status").GetString());
        Assert.Equal("client", c1.GetProperty("order").GetProperty("closeReason").GetString());
        var c2 = await Post(h, e, token, "cancel");
        Assert.Equal(HttpStatusCode.OK, c2.StatusCode);
        Assert.True(c2.Headers.Contains("Idempotent-Replayed"));
        await Expect(await Post(h, e, token, "pay"), HttpStatusCode.Conflict, "order_closed");

        var (token2, _) = await CreateAsync(h, e, [Line(p, 1)], RandPhone()); // резерв знято
        await Expect(await Post(h, e, token2, "pay"), HttpStatusCode.OK);
        await Expect(await Post(h, e, token2, "cancel"), HttpStatusCode.Conflict, "cannot_cancel");
    }

    // ---------- ідемпотентність ----------

    [SkippableFact]
    public async Task create_is_idempotent_same_body_replays_other_body_is_reused_and_parallel_creates_one()
    {
        NeedDb();
        var e = await NewEnvAsync();
        var h = NewHost();
        var p = await AddProductAsync(e, "Shampoo", 100m, stock: 10);
        var key = Guid.NewGuid().ToString("N");
        var r1 = await CreateRawAsync(h, e, [Line(p, 2)], key: key);
        var j1 = await Expect(r1, HttpStatusCode.Created);
        var r2 = await CreateRawAsync(h, e, [Line(p, 2)], key: key);
        var j2 = await Expect(r2, HttpStatusCode.OK);
        Assert.Equal("true", r2.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(j1.GetProperty("publicToken").GetString(), j2.GetProperty("publicToken").GetString());
        Assert.Equal(j1.GetProperty("order").GetProperty("number").GetInt32(), j2.GetProperty("order").GetProperty("number").GetInt32());
        await Expect(await CreateRawAsync(h, e, [Line(p, 3)], key: key), HttpStatusCode.UnprocessableEntity, "idempotency_key_reused");

        var k2 = Guid.NewGuid().ToString("N");
        var rs = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => CreateRawAsync(h, e, [Line(p, 1)], "+380671230000", key: k2)));
        Assert.Equal(1, rs.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.All(rs.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        await using var ctx = fx.Db.CreateContext(e.T.TenantId);
        Assert.Equal(2, await ctx.ProductOrders.CountAsync());
    }

    [SkippableFact]
    public async Task parallel_pay_charges_once_and_second_gets_payment_in_progress()
    {
        NeedDb();
        var e = await NewEnvAsync();
        var pay = new FakeOrderPayments { ChargeDelayMs = 700 };
        var h = NewHost(pay: pay);
        var p = await AddProductAsync(e, "Shampoo", 100m, stock: 5);
        var (token, _) = await CreateAsync(h, e, [Line(p, 1)]);
        var first = Post(h, e, token, "pay");
        await Task.Delay(200);
        var second = await Post(h, e, token, "pay");
        var inProgress = await Expect(second, HttpStatusCode.Conflict, "payment_in_progress");
        Assert.True(inProgress.GetProperty("order").GetProperty("paymentInProgress").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await first).StatusCode);
        Assert.Equal(1, pay.ChargeCalls);
        Assert.Single(pay.Charged);
    }

    // ---------- оплата: відмова, спроби, зникнення товару, reconcile ----------

    [SkippableFact]
    public async Task mock_payment_declines_cents_13_then_attempts_are_exhausted()
    {
        NeedDb();
        var e = await NewEnvAsync();
        var h = NewHost(configure: b => b.UseEnvironment("Staging"), fake: false); // Staging: mock-оплата дозволена
        var p = await AddProductAsync(e, "Odd price", 100.13m, stock: 5);
        var (token, order) = await CreateAsync(h, e, [Line(p, 1)]);
        Assert.Equal(5, order.GetProperty("paymentAttemptsLeft").GetInt32());
        var f1 = await Expect(await Post(h, e, token, "pay"), HttpStatusCode.PaymentRequired, "payment_failed");
        var o1 = f1.GetProperty("order");
        Assert.Equal("pending_payment", o1.GetProperty("status").GetString());
        Assert.Equal(4, o1.GetProperty("paymentAttemptsLeft").GetInt32());
        Assert.True(o1.GetProperty("canPay").GetBoolean());
        for (var i = 0; i < 4; i++) await Expect(await Post(h, e, token, "pay"), HttpStatusCode.PaymentRequired, "payment_failed");
        var done = await Expect(await Post(h, e, token, "pay"), HttpStatusCode.UnprocessableEntity, "payment_attempts_exceeded");
        Assert.False(done.GetProperty("order").GetProperty("canPay").GetBoolean());
        await using var ctx = fx.Db.CreateContext(e.T.TenantId);
        Assert.Equal(5, await ctx.Payments.CountAsync(x => x.ProductOrderId != null && x.Status == PaymentStatus.Failed));
    }

    [SkippableFact]
    public async Task payment_failure_then_retry_succeeds()
    {
        NeedDb();
        var e = await NewEnvAsync();
        var h = NewHost();
        var p = await AddProductAsync(e, "Shampoo", 100m, stock: 5);
        var (token, _) = await CreateAsync(h, e, [Line(p, 1)]);
        h.Pay.DeclineAll = true;
        await Expect(await Post(h, e, token, "pay"), HttpStatusCode.PaymentRequired, "payment_failed");
        h.Pay.DeclineAll = false;
        await Expect(await Post(h, e, token, "pay"), HttpStatusCode.OK);
        Assert.Equal(4m, await AssertInvariantAsync(e, p));
    }

    [SkippableFact]
    public async Task stock_vanishing_during_payment_refunds_100_percent_without_sale_or_client()
    {
        NeedDb();
        var e = await NewEnvAsync();
        var h = NewHost();
        var p = await AddProductAsync(e, "Shampoo", 100m, stock: 2);
        var (token, _) = await CreateAsync(h, e, [Line(p, 2)]);
        await using (var ctx = fx.Db.CreateContext(e.T.TenantId)) // заклад списав усе
        {
            await ctx.StockBatches.Where(b => b.ProductId == p).ExecuteUpdateAsync(s => s.SetProperty(b => b.Quantity, 0m));
            await ctx.StockLevels.Where(l => l.ProductId == p).ExecuteUpdateAsync(s => s.SetProperty(l => l.Quantity, 0m));
        }
        var r = await Expect(await Post(h, e, token, "pay"), HttpStatusCode.Conflict, "stock_unavailable");
        var order = r.GetProperty("order");
        Assert.Equal("refunded", order.GetProperty("status").GetString());
        Assert.Equal("stock_unavailable", order.GetProperty("closeReason").GetString());
        Assert.Equal(200m, order.GetProperty("refundAmount").GetDecimal());
        Assert.Equal(1, h.Pay.RefundCalls);
        await using var c2 = fx.Db.CreateContext(e.T.TenantId);
        Assert.False(await c2.StockOperations.AnyAsync(o => o.Kind == StockOperationKind.Sale));
        Assert.False(await c2.Clients.AnyAsync(c => c.Phone == Phone));
        Assert.Equal(1, await c2.Payments.CountAsync(x => x.ProductOrderId != null && x.Status == PaymentStatus.Refunded));
    }

    [SkippableFact]
    public async Task failed_refund_keeps_payment_in_progress_and_reconcile_completes_it()
    {
        NeedDb();
        var e = await NewEnvAsync();
        var h = NewHost();
        var p = await AddProductAsync(e, "Shampoo", 100m, stock: 1);
        var (token, _) = await CreateAsync(h, e, [Line(p, 1)]);
        await using (var ctx = fx.Db.CreateContext(e.T.TenantId))
        {
            await ctx.StockBatches.ExecuteUpdateAsync(s => s.SetProperty(b => b.Quantity, 0m));
            await ctx.StockLevels.ExecuteUpdateAsync(s => s.SetProperty(l => l.Quantity, 0m));
        }
        h.Pay.FailRefund = true;
        var r = await Expect(await Post(h, e, token, "pay"), HttpStatusCode.Conflict, "stock_unavailable");
        Assert.Equal("pending_payment", r.GetProperty("order").GetProperty("status").GetString());
        Assert.True(r.GetProperty("order").GetProperty("paymentInProgress").GetBoolean());
        Assert.Contains(h.Logs, l => l.Contains("product_order_compensation_failed"));

        // завислу оплату довершує повтор після PaymentStaleSeconds
        await SetOrderAsync(e, token, o => o.PaymentStartedAt = DateTimeOffset.UtcNow.AddMinutes(-10));
        h.Pay.FailRefund = false;
        var again = await Expect(await Post(h, e, token, "pay"), HttpStatusCode.Conflict, "stock_unavailable");
        Assert.Equal("refunded", again.GetProperty("order").GetProperty("status").GetString());
        Assert.Single(h.Pay.Charged); // гроші списано один раз
    }

    [SkippableFact]
    public async Task failure_between_charge_and_finalize_is_reconciled_with_same_payment_id()
    {
        NeedDb();
        var e = await NewEnvAsync();
        var h = NewHost();
        var p = await AddProductAsync(e, "Shampoo", 100m, stock: 3);
        var (token, _) = await CreateAsync(h, e, [Line(p, 2)]);
        h.Pay.ThrowAfterChargeOnce();
        try { await Post(h, e, token, "pay"); } catch (Exception) { /* збій провайдера/мережі: 500 або виняток хоста */ }
        var mid = await Expect(await GetOrder(h, e, token), HttpStatusCode.OK);
        Assert.Equal("pending_payment", mid.GetProperty("status").GetString());
        Assert.True(mid.GetProperty("paymentInProgress").GetBoolean());

        // до закінчення PaymentStaleSeconds - payment_in_progress, потім reconcile
        await Expect(await Post(h, e, token, "pay"), HttpStatusCode.Conflict, "payment_in_progress");
        await SetOrderAsync(e, token, o => o.PaymentStartedAt = DateTimeOffset.UtcNow.AddMinutes(-10));
        var done = await Expect(await Post(h, e, token, "pay"), HttpStatusCode.OK);
        Assert.Equal("paid", done.GetProperty("order").GetProperty("status").GetString());
        Assert.Single(h.Pay.Charged); // той самий PaymentId: одне списання
        Assert.Equal(2, h.Pay.ChargeCalls);
        Assert.Equal(1m, await AssertInvariantAsync(e, p));
        await using var ctx = fx.Db.CreateContext(e.T.TenantId);
        Assert.Equal(1, await ctx.StockOperations.CountAsync(o => o.Kind == StockOperationKind.Sale));
    }

    // ---------- security-рев'ю: M1/M3/M4/L1-L5 ----------

    [SkippableFact]
    public async Task client_abort_after_claim_still_completes_payment_with_own_token()
    {
        NeedDb();
        var e = await NewEnvAsync();
        var pay = new FakeOrderPayments { ChargeDelayMs = 700 };
        var h = NewHost(pay: pay);
        var p = await AddProductAsync(e, "Shampoo", 100m, stock: 3);
        var (token, _) = await CreateAsync(h, e, [Line(p, 1)]);
        using var cts = new CancellationTokenSource();
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/public/{e.Slug}/product-orders/{token}/pay") { Content = JsonContent.Create(new { }) };
        var call = h.Client.SendAsync(req, cts.Token);
        await Task.Delay(300); // claim зроблено, Charge триває
        cts.Cancel();
        try { await call; } catch (OperationCanceledException) { /* клієнт обірвав запит */ }

        // платіж завершується попри обрив: гроші списано -> замовлення paid, а не «в процесі» до reconcile
        JsonElement j = default;
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(250);
            j = await Expect(await GetOrder(h, e, token), HttpStatusCode.OK);
            if (j.GetProperty("status").GetString() == "paid") break;
        }
        Assert.Equal("paid", j.GetProperty("status").GetString());
        Assert.Single(pay.Charged);
        Assert.Equal(2m, await AssertInvariantAsync(e, p));
    }

    [SkippableFact]
    public async Task in_progress_reserve_is_bounded_stale_payment_does_not_hold_stock_forever()
    {
        NeedDb();
        var e = await NewEnvAsync();
        var h = NewHost();
        var p = await AddProductAsync(e, "Last unit", 100m, stock: 1);
        var (tokenA, _) = await CreateAsync(h, e, [Line(p, 1)], RandPhone());
        // оплата в процесі свіжа: резерв тримається навіть після TTL
        await SetOrderAsync(e, tokenA, o => { o.PaymentStartedAt = DateTimeOffset.UtcNow.AddMinutes(-1); o.ReservationExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-2); });
        await Expect(await CreateRawAsync(h, e, [Line(p, 1)], RandPhone()), HttpStatusCode.UnprocessableEntity, "insufficient_stock");
        // оплата «зависла» (старша за межу резерву): товар знову доступний, фіналізація й так перевіряє наявність під блокуванням
        await SetOrderAsync(e, tokenA, o => o.PaymentStartedAt = DateTimeOffset.UtcNow.AddMinutes(-ProductOrdersOptions.InProgressReserveMinutes - 5));
        await Expect(await CreateRawAsync(h, e, [Line(p, 1)], RandPhone()), HttpStatusCode.Created);
    }

    [SkippableFact]
    public async Task provider_exception_keeps_claim_and_payment_pending_for_reconcile()
    {
        NeedDb();
        var e = await NewEnvAsync();
        var h = NewHost();
        var p = await AddProductAsync(e, "Shampoo", 100m, stock: 3);
        var (token, _) = await CreateAsync(h, e, [Line(p, 1)]);
        h.Pay.ThrowAfterChargeOnce(); // провайдер списав і кинув виняток: результат невідомий
        var r = await Expect(await Post(h, e, token, "pay"), HttpStatusCode.Conflict, "payment_in_progress");
        Assert.True(r.GetProperty("order").GetProperty("paymentInProgress").GetBoolean());
        Assert.Contains(h.Logs, l => l.Contains("product_order_payment_unknown"));
        Assert.DoesNotContain(h.Logs, l => l.Contains("injected failure")); // текст винятку (потенційні ПД) не логується
        await using var ctx = fx.Db.CreateContext(e.T.TenantId);
        var po = await ctx.ProductOrders.AsNoTracking().FirstAsync();
        Assert.NotNull(po.PaymentStartedAt);
        Assert.Equal(PaymentStatus.Pending, (await ctx.Payments.AsNoTracking().FirstAsync(x => x.ProductOrderId == po.Id)).Status);
        Assert.Equal(1, po.PaymentAttempts);
    }

    [SkippableFact]
    public async Task slow_decline_of_old_claim_does_not_erase_claim_or_payment_of_reconciler()
    {
        NeedDb();
        var e = await NewEnvAsync();
        var pay = new FakeOrderPayments { DelayFor = c => c == 1 ? 1200 : 0, DeclineFor = c => c == 1 };
        var h = NewHost(pay: pay);
        var p = await AddProductAsync(e, "Shampoo", 100m, stock: 3);
        var (token, _) = await CreateAsync(h, e, [Line(p, 1)]);
        var a = Post(h, e, token, "pay"); // A: claim, повільна відмова
        await Task.Delay(400);
        await SetOrderAsync(e, token, o => o.PaymentStartedAt = DateTimeOffset.UtcNow.AddMinutes(-10)); // A «завис»
        var b = await Post(h, e, token, "pay"); // B: reconcile-claim, швидкий успіх
        Assert.Equal(HttpStatusCode.OK, b.StatusCode);
        var aRes = await a; // A відмову не застосовує (CAS): не payment_failed
        Assert.NotEqual(HttpStatusCode.PaymentRequired, aRes.StatusCode);
        var j = await Expect(await GetOrder(h, e, token), HttpStatusCode.OK);
        Assert.Equal("paid", j.GetProperty("status").GetString());
        await using var ctx = fx.Db.CreateContext(e.T.TenantId);
        Assert.Equal(0, await ctx.Payments.CountAsync(x => x.ProductOrderId != null && x.Status == PaymentStatus.Failed));
        Assert.Equal(1, await ctx.StockOperations.CountAsync(o => o.Kind == StockOperationKind.Sale));
        Assert.Equal(2m, await AssertInvariantAsync(e, p));
    }

    [SkippableFact]
    public async Task compensation_records_refund_intent_and_reconcile_only_refunds_never_finalizes()
    {
        NeedDb();
        var e = await NewEnvAsync();
        var h = NewHost();
        var p = await AddProductAsync(e, "Shampoo", 100m, stock: 1);
        var (token, _) = await CreateAsync(h, e, [Line(p, 1)]);
        await using (var ctx = fx.Db.CreateContext(e.T.TenantId))
        {
            await ctx.StockBatches.ExecuteUpdateAsync(s => s.SetProperty(b => b.Quantity, 0m));
            await ctx.StockLevels.ExecuteUpdateAsync(s => s.SetProperty(l => l.Quantity, 0m));
        }
        h.Pay.ThrowOnRefund = true; // повернення: результат невідомий
        var r = await Expect(await Post(h, e, token, "pay"), HttpStatusCode.Conflict, "stock_unavailable");
        Assert.Equal("pending_payment", r.GetProperty("order").GetProperty("status").GetString());
        Assert.Contains(h.Logs, l => l.Contains("product_order_compensation_failed"));
        await using (var ctx = fx.Db.CreateContext(e.T.TenantId))
        {
            var pm = await ctx.Payments.AsNoTracking().FirstAsync(x => x.ProductOrderId != null);
            Assert.Equal(PaymentStatus.Paid, pm.Status);
            Assert.Equal(ProductOrdersOptions.RefundIntentMarker, pm.Provider); // намір зафіксовано ДО повернення
        }

        // товар «повернувся» на склад, але reconcile лише повторює повернення і не фіналізує
        await AddBatchAsync(e, p, 5, Far);
        await SetOrderAsync(e, token, o => o.PaymentStartedAt = DateTimeOffset.UtcNow.AddMinutes(-10));
        h.Pay.ThrowOnRefund = false;
        var again = await Expect(await Post(h, e, token, "pay"), HttpStatusCode.Conflict, "stock_unavailable");
        Assert.Equal("refunded", again.GetProperty("order").GetProperty("status").GetString());
        Assert.Equal(2, h.Pay.RefundCalls);
        Assert.Single(h.Pay.Charged);
        await using var c2 = fx.Db.CreateContext(e.T.TenantId);
        Assert.False(await c2.StockOperations.AnyAsync(o => o.Kind == StockOperationKind.Sale));
        Assert.Equal(1, await c2.Payments.CountAsync(x => x.ProductOrderId != null && x.Status == PaymentStatus.Refunded));
    }

    [SkippableFact]
    public async Task customer_name_with_format_or_control_characters_is_rejected()
    {
        NeedDb();
        var e = await NewEnvAsync();
        var h = NewHost();
        var p = await AddProductAsync(e, "Shampoo", 100m, stock: 5);
        foreach (var bad in new[] { "Оле​на", "Олена‮", "⁦Олена", "Ол\u0007ена", "Олена﻿", "Олена\U000E0001" })
            await Expect(await CreateRawAsync(h, e, [Line(p, 1)], RandPhone(), bad), HttpStatusCode.UnprocessableEntity, "invalid_name");
        await Expect(await CreateRawAsync(h, e, [Line(p, 1)], RandPhone(), "Оленка-Марія"), HttpStatusCode.Created);
    }

    [SkippableFact]
    public async Task price_changed_is_409_with_current_total_and_other_creation_errors_are_typed()
    {
        NeedDb();
        var e = await NewEnvAsync();
        var h = NewHost();
        var p = await AddProductAsync(e, "Shampoo", 100m, stock: 50);
        var j = await Expect(await CreateRawAsync(h, e, [Line(p, 2)], expected: 150m), HttpStatusCode.Conflict, "price_changed");
        Assert.Equal(200m, j.GetProperty("total").GetDecimal());
        await Expect(await CreateRawAsync(h, e, [Line(p, 1)], expected: 100.123m), HttpStatusCode.UnprocessableEntity, "invalid_expected_total");

        await Expect(await CreateRawAsync(h, e, [Line(p, 11)]), HttpStatusCode.UnprocessableEntity, "invalid_quantity");
        var q = await Expect(await CreateRawAsync(h, e, [Line(p, 1), Line(p, 1.5m)]), HttpStatusCode.UnprocessableEntity, "duplicate_line");
        Assert.Equal(1, q.GetProperty("line").GetInt32());
        await Expect(await CreateRawAsync(h, e, [Line(p, 1.5m)]), HttpStatusCode.UnprocessableEntity, "invalid_quantity");
        await Expect(await CreateRawAsync(h, e, []), HttpStatusCode.UnprocessableEntity, "invalid_lines");
        await Expect(await CreateRawAsync(h, e, [.. Enumerable.Range(0, 21).Select(_ => Line(Guid.NewGuid(), 1))]), HttpStatusCode.UnprocessableEntity, "too_many_lines");
        await Expect(await CreateRawAsync(h, e, [Line(p, 1)], phone: "12"), HttpStatusCode.UnprocessableEntity, "invalid_phone");
        await Expect(await CreateRawAsync(h, e, [Line(p, 1)], name: "A"), HttpStatusCode.UnprocessableEntity, "invalid_name");
        await Expect(await CreateRawAsync(h, e, [Line(p, 1)], extra: new { website = "bot" }), HttpStatusCode.UnprocessableEntity, "invalid_request");
        await Expect(await CreateRawAsync(h, e, [Line(p, 1)], key: "none"), HttpStatusCode.UnprocessableEntity, "idempotency_key_required");
        await Expect(await CreateRawAsync(h, e, [Line(p, 1)], key: "short"), HttpStatusCode.UnprocessableEntity, "invalid_idempotency_key");
        await Expect(await CreateRawAsync(h, e, [Line(Guid.NewGuid(), 1)]), HttpStatusCode.UnprocessableEntity, "product_unavailable");
        var hp = await h.Client.PostAsync($"/api/public/{e.Slug}/product-orders", new StringContent("not json", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, hp.StatusCode);
        var big = await AddProductAsync(e, "Expensive", 9000m, stock: 50);
        await Expect(await CreateRawAsync(h, e, [Line(big, 6)]), HttpStatusCode.UnprocessableEntity, "order_total_too_large");
    }

    [SkippableFact]
    public async Task pending_per_phone_limit_is_enforced()
    {
        NeedDb();
        var e = await NewEnvAsync();
        var h = NewHost();
        var p = await AddProductAsync(e, "Shampoo", 100m, stock: 50);
        await CreateAsync(h, e, [Line(p, 1)]);
        await CreateAsync(h, e, [Line(p, 1)]);
        await Expect(await CreateRawAsync(h, e, [Line(p, 1)]), HttpStatusCode.UnprocessableEntity, "order_limit_reached");
        await CreateAsync(h, e, [Line(p, 1)], RandPhone()); // інший телефон - окей
    }

    // ---------- видимість P1 ----------

    [SkippableFact]
    public async Task catalog_shows_only_sellable_products_without_quantities_or_costs()
    {
        NeedDb();
        var e = await NewEnvAsync();
        var h = NewHost();
        var ok = await AddProductAsync(e, "Visible", 50m, stock: 3);
        var both = await AddProductAsync(e, "Both", 60m, stock: 1, purpose: ProductPurpose.Both);
        var noStock = await AddProductAsync(e, "No stock", 70m);
        var expiredOnly = await AddProductAsync(e, "Expired only", 80m, stock: 5, expires: Expired);
        await AddProductAsync(e, "Salon", 10m, stock: 3, purpose: ProductPurpose.SalonUse);
        await AddProductAsync(e, "No price", null, stock: 3);
        await AddProductAsync(e, "Zero price", 0m, stock: 3);
        await AddProductAsync(e, "Inactive", 10m, stock: 3, active: false);
        var disabled = await AddProductAsync(e, "Disabled here", 10m, stock: 3);
        await using (var ctx = fx.Db.CreateContext(e.T.TenantId))
        {
            ctx.ProductLocationSettings.Add(new ProductLocationSetting { ProductId = disabled, LocationId = e.Loc, IsEnabled = false, UpdatedByUserId = e.Owner });
            await ctx.SaveChangesAsync();
        }
        var r = await h.Client.GetAsync($"/api/public/{e.Slug}/locations/{e.Loc}/products");
        Assert.Equal("no-store", r.Headers.CacheControl?.ToString());
        var j = await Expect(r, HttpStatusCode.OK);
        var items = j.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("name").GetString()!);
        Assert.Equal(["Both", "Expired only", "No stock", "Visible"], items.Keys.OrderBy(x => x).ToArray());
        Assert.True(items["Visible"].GetProperty("inStock").GetBoolean());
        Assert.True(items["Both"].GetProperty("inStock").GetBoolean());
        Assert.False(items["No stock"].GetProperty("inStock").GetBoolean());
        Assert.False(items["Expired only"].GetProperty("inStock").GetBoolean());
        Assert.Equal(["categoryPath", "id", "inStock", "name", "price", "unit"], items["Visible"].EnumerateObject().Select(x => x.Name).OrderBy(x => x).ToArray());
        Assert.DoesNotContain("purchase", j.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["currency", "items", "location", "maxLines", "maxQuantityPerLine", "reservationMinutes", "truncated"],
            j.EnumerateObject().Select(x => x.Name).OrderBy(x => x).ToArray());
        _ = (ok, both, noStock, expiredOnly);
        await Expect(await h.Client.GetAsync($"/api/public/{e.Slug}/locations/{Guid.NewGuid()}/products"), HttpStatusCode.NotFound, "location_not_found");
        await Expect(await h.Client.GetAsync($"/api/public/{e.Slug}/locations/not-a-guid/products"), HttpStatusCode.NotFound, "location_not_found");
    }

    // ---------- вмикання ----------

    [SkippableTheory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("1")]
    [InlineData("")]
    public async Task disabled_flag_gives_product_orders_disabled_but_view_by_token_still_works(string? flag)
    {
        NeedDb();
        var e = await NewEnvAsync();
        var p = await AddProductAsync(e, "Shampoo", 100m, stock: 5);
        var on = NewHost();
        var (token, _) = await CreateAsync(on, e, [Line(p, 1)]);

        var off = NewHost(enabled: flag);
        await Expect(await off.Client.GetAsync($"/api/public/{e.Slug}/locations/{e.Loc}/products"), HttpStatusCode.NotFound, ProductOrderService.Disabled);
        await Expect(await CreateRawAsync(off, e, [Line(p, 1)]), HttpStatusCode.NotFound, ProductOrderService.Disabled);
        await Expect(await Post(off, e, token, "pay"), HttpStatusCode.NotFound, ProductOrderService.Disabled);
        await Expect(await Post(off, e, token, "cancel"), HttpStatusCode.NotFound, ProductOrderService.Disabled);
        Assert.Equal("pending_payment", (await Expect(await GetOrder(off, e, token), HttpStatusCode.OK)).GetProperty("status").GetString());
    }

    [SkippableFact]
    public async Task tenant_without_inventory_module_is_disabled_but_unknown_slug_is_plain_404()
    {
        NeedDb();
        var e = await NewEnvAsync([.. BeautyModules.All]); // без beauty_inventory
        var h = NewHost();
        await Expect(await h.Client.GetAsync($"/api/public/{e.Slug}/locations/{e.Loc}/products"), HttpStatusCode.NotFound, ProductOrderService.Disabled);
        await Expect(await h.Client.GetAsync($"/api/public/no-such-salon-xyz/locations/{e.Loc}/products"), HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public void prod_guard_rejects_enabled_with_mock_payments_in_production_only()
    {
        Assert.Throws<InvalidOperationException>(() => ProductOrdersOptions.ValidateForHost(true, true, true));
        ProductOrdersOptions.ValidateForHost(true, true, false);
        ProductOrdersOptions.ValidateForHost(true, false, true); // staging/dev
        ProductOrdersOptions.ValidateForHost(false, true, true);
        Assert.True(ProductOrdersOptions.IsOn("TRUE"));
        Assert.False(ProductOrdersOptions.IsOn("1"));
        Assert.False(ProductOrdersOptions.IsOn(""));
        Assert.False(ProductOrdersOptions.IsOn(null));
    }

    [SkippableFact]
    public void production_host_with_flag_and_mock_payments_fails_to_start()
    {
        NeedDb();
        var f = fx.CreateFactory(10_000).WithWebHostBuilder(b => b.UseSetting("ProductOrders:Enabled", "true"));
        _disposables.Add(f);
        Assert.ThrowsAny<Exception>(() => f.CreateClient());
    }

    [SkippableFact]
    public void out_of_range_config_value_fails_startup()
    {
        NeedDb();
        var f = fx.CreateFactory(10_000).WithWebHostBuilder(b => b.UseSetting("ProductOrders:ReservationMinutes", "3"));
        _disposables.Add(f);
        Assert.ThrowsAny<Exception>(() => f.CreateClient());
    }

    // ---------- токен, tenant, 426 ----------

    [SkippableFact]
    public async Task unknown_malformed_and_foreign_tokens_give_identical_404()
    {
        NeedDb();
        var a = await NewEnvAsync();
        var b = await NewEnvAsync();
        var h = NewHost();
        var p = await AddProductAsync(a, "Shampoo", 100m, stock: 5);
        var (token, _) = await CreateAsync(h, a, [Line(p, 1)]);

        var bodies = new List<string>();
        foreach (var t in new[] { new string('A', 43), "short", token[..42] + "!", Guid.NewGuid().ToString("N") })
        {
            foreach (var r in new[] { await GetOrder(h, a, t), await Post(h, a, t, "pay"), await Post(h, a, t, "cancel") })
            {
                Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
                bodies.Add(await r.Content.ReadAsStringAsync());
            }
        }
        // чужий tenant: токен tenant A на slug tenant B (RLS)
        foreach (var r in new[] { await GetOrder(h, b, token), await Post(h, b, token, "pay"), await Post(h, b, token, "cancel") })
        {
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
            bodies.Add(await r.Content.ReadAsStringAsync());
        }
        Assert.Single(bodies.Distinct());
        // чужий заклад/товар у tenant B
        await Expect(await h.Client.GetAsync($"/api/public/{b.Slug}/locations/{a.Loc}/products"), HttpStatusCode.NotFound, "location_not_found");
        await Expect(await CreateRawAsync(h, b, [Line(p, 1)]), HttpStatusCode.UnprocessableEntity, "product_unavailable");
        // замовлення A недоступне по slug B, але доступне по A
        Assert.Equal(HttpStatusCode.OK, (await GetOrder(h, a, token)).StatusCode);
    }

    [SkippableFact]
    public async Task old_ios_app_gets_426_before_tenant_resolution_on_all_routes()
    {
        NeedDb();
        var e = await NewEnvAsync();
        var h = NewHost(configure: b => b.UseSetting("PublicBooking:MinAppVersion:Ios", "2.0.0"));
        var p = await AddProductAsync(e, "Shampoo", 100m, stock: 5);
        void Old(HttpRequestMessage r) { r.Headers.Add("X-Booking-Client", "ios"); r.Headers.Add("X-App-Version", "1.0.0"); }
        var rs = new List<HttpResponseMessage>
        {
            await CreateRawAsync(h, e, [Line(p, 1)], slug: "no-such-salon-xyz", tweak: Old),
        };
        foreach (var (method, url) in new[]
                 {
                     (HttpMethod.Get, $"/api/public/no-such-salon-xyz/locations/{e.Loc}/products"),
                     (HttpMethod.Get, "/api/public/no-such-salon-xyz/product-orders/" + new string('A', 43)),
                     (HttpMethod.Post, "/api/public/no-such-salon-xyz/product-orders/" + new string('A', 43) + "/pay"),
                     (HttpMethod.Post, "/api/public/no-such-salon-xyz/product-orders/" + new string('A', 43) + "/cancel"),
                 })
        {
            var req = new HttpRequestMessage(method, url);
            Old(req);
            rs.Add(await h.Client.SendAsync(req));
        }
        Assert.All(rs, r => Assert.Equal((HttpStatusCode)426, r.StatusCode));
    }

    [SkippableFact]
    public async Task write_routes_are_rate_limited_with_retry_after()
    {
        NeedDb();
        var e = await NewEnvAsync();
        var h = NewHost(configure: b => b.UseSetting("PublicBooking:RateLimit:WritePermit", "2"));
        var codes = new List<HttpResponseMessage>();
        for (var i = 0; i < 4; i++) codes.Add(await Post(h, e, new string('B', 43), "pay"));
        Assert.Contains(codes, r => r.StatusCode == HttpStatusCode.TooManyRequests);
    }
}
