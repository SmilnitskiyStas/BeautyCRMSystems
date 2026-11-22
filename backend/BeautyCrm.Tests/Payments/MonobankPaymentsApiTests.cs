using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BeautyCrm.Api.Payments.Monobank;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyPublicBooking;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Tests.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using static BeautyCrm.Tests.Auth.AuthApiFixture;

namespace BeautyCrm.Tests.Payments;

/// <summary>
/// ADR-028: оплата замовлень товарів через рахунок monobank (тестовий режим) на реальному PostgreSQL через повний HTTP-конвеєр.
/// API monobank - фейк у пам'яті з власним ECDSA-ключем; вебхуки підписуються як у банку.
/// </summary>
public sealed class MonobankPaymentsApiTests(AuthApiFixture fx) : IClassFixture<AuthApiFixture>, IDisposable
{
    private readonly List<IDisposable> _disposables = [];
    private static readonly DateOnly Far = new(2099, 1, 1);

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
    }

    private void NeedDb() => Skip.If(fx.SkipReason is not null, fx.SkipReason);

    private sealed record Env(TenantSeed T, Guid Loc, Guid Owner, Guid Product)
    {
        public string Slug => T.Slug;
    }

    private (HttpClient Client, FakeMonobankClient Mono) NewHost()
    {
        var opts = new MonobankOptions { Token = "test-token", TestMode = true, PublicBaseUrl = new Uri("https://api.example.test/") };
        var mono = new FakeMonobankClient(opts);
        var f = fx.CreateFactory(10_000).WithWebHostBuilder(b =>
        {
            b.UseSetting("ProductOrders:Enabled", "true");
            b.UseSetting("Payments:Provider", "monobank");
            b.UseSetting("Monobank:Token", "test-token");
            b.UseSetting("Monobank:TestMode", "true");
            b.UseSetting("Monobank:PublicBaseUrl", "https://api.example.test/");
            b.UseSetting("PublicBooking:RateLimit:ReadPermit", "100000");
            b.UseSetting("PublicBooking:RateLimit:TokenPermit", "100000");
            b.UseSetting("PublicBooking:RateLimit:WritePermit", "100000");
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<MonobankClient>();
                s.AddSingleton<MonobankClient>(mono);
                s.RemoveAll<MonobankSignature>();
                s.AddSingleton(new MonobankSignature(mono, TimeProvider.System, NullLogger<MonobankSignature>.Instance));
            });
        });
        _disposables.Add(f);
        return (f.CreateClient(), mono);
    }

    private async Task<Env> NewEnvAsync(decimal price = 420.50m, decimal stock = 5)
    {
        var t = await fx.CreateTenantAsync([.. BeautyModules.Known]);
        await using var ctx = fx.Db.CreateContext(t.TenantId);
        var loc = new Location { Name = "Main", Address = "Street 1", Timezone = "Europe/Kyiv" };
        ctx.Locations.Add(loc);
        var p = new Product { Name = "Shampoo", Unit = ProductUnit.Pcs, Purpose = ProductPurpose.Sale, SalePrice = price, IsActive = true, UpdatedByUserId = t.OwnerId };
        ctx.Products.Add(p);
        await ctx.SaveChangesAsync();
        var at = DateTimeOffset.UtcNow.AddDays(-1);
        var op = new StockOperation { Id = Guid.NewGuid(), Kind = StockOperationKind.Receipt, LocationId = loc.Id, CreatedByUserId = t.OwnerId, CreatedAt = at };
        var batch = new StockBatch
        {
            Id = Guid.NewGuid(), LocationId = loc.Id, ProductId = p.Id, OperationId = op.Id, ReceivedAt = at, ExpiresOn = Far,
            InitialQuantity = stock, Quantity = stock, UnitCost = 10m, CreatedAt = at, UpdatedAt = at,
        };
        ctx.StockOperations.Add(op);
        ctx.StockBatches.Add(batch);
        ctx.StockMovements.Add(new StockMovement
        {
            Id = Guid.NewGuid(), OperationId = op.Id, LocationId = loc.Id, ProductId = p.Id, BatchId = batch.Id, Type = StockMovementType.Receipt,
            Quantity = stock, UnitCost = 10m, CreatedByUserId = t.OwnerId, OccurredAt = at,
        });
        ctx.StockLevels.Add(new StockLevel { LocationId = loc.Id, ProductId = p.Id, Quantity = stock, CreatedAt = at, UpdatedAt = at });
        await ctx.SaveChangesAsync();
        return new Env(t, loc.Id, t.OwnerId, p.Id);
    }

    private static async Task<JsonElement> Expect(HttpResponseMessage r, HttpStatusCode status, string? code = null)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == status, $"expected {status}, got {(int)r.StatusCode}: {body}");
        var j = body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
        if (code is not null) Assert.Equal(code, j.GetProperty("code").GetString());
        return j;
    }

    private static async Task<string> CreateOrderAsync(HttpClient c, Env e, int qty = 2)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/public/{e.Slug}/product-orders")
        {
            Content = JsonContent.Create(new { locationId = e.Loc, customer = new { name = "Олена Тестова", phone = "+38067" + Random.Shared.Next(1_000_000, 9_999_999) }, lines = new[] { new { productId = e.Product, quantity = qty } } }, options: Json),
        };
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return (await Expect(await c.SendAsync(req), HttpStatusCode.Created)).GetProperty("publicToken").GetString()!;
    }

    private static Task<HttpResponseMessage> Pay(HttpClient c, Env e, string token) =>
        c.PostAsync($"/api/public/{e.Slug}/product-orders/{token}/pay", JsonContent.Create(new { }));

    private static async Task<JsonElement> Order(HttpClient c, Env e, string token) =>
        await Expect(await c.GetAsync($"/api/public/{e.Slug}/product-orders/{token}"), HttpStatusCode.OK);

    private static Task<HttpResponseMessage> Webhook(HttpClient c, FakeMonobankClient mono, string slug, FakeMonobankClient.Invoice inv, string status,
        long? amount = null, string? sign = null)
    {
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            invoiceId = inv.Id, status, amount = amount ?? inv.Amount, ccy = 980, finalAmount = amount ?? inv.Amount, reference = inv.Reference,
            createdDate = DateTimeOffset.UtcNow.AddMinutes(-1), modifiedDate = DateTimeOffset.UtcNow,
        }));
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/public/{slug}/payments/monobank/webhook") { Content = new ByteArrayContent(body) };
        req.Content.Headers.ContentType = new("application/json");
        req.Headers.Add("X-Sign", sign ?? mono.Sign(body));
        return c.SendAsync(req);
    }

    private async Task<(int Sales, decimal Level, string? PaymentStatus)> StateAsync(Env e)
    {
        await using var ctx = fx.Db.CreateContext(e.T.TenantId);
        var sales = await ctx.StockOperations.CountAsync(o => o.Kind == StockOperationKind.Sale);
        var level = await ctx.StockLevels.Where(l => l.ProductId == e.Product).Select(l => l.Quantity).SingleAsync();
        var pay = await ctx.Payments.AsNoTracking().OrderByDescending(p => p.CreatedAt).FirstOrDefaultAsync();
        return (sales, level, pay is null ? null : pay.Status.ToString());
    }

    [SkippableFact]
    public async Task pay_returns_payment_url_then_signed_success_webhook_finalizes_once()
    {
        NeedDb();
        var e = await NewEnvAsync();
        var (c, mono) = NewHost();
        var token = await CreateOrderAsync(c, e);

        var paid = await Expect(await Pay(c, e, token), HttpStatusCode.OK);
        var inv = mono.Single();
        Assert.Equal($"https://pay.mbnk.biz/{inv.Id}", paid.GetProperty("paymentUrl").GetString());
        Assert.Equal(84100, inv.Amount);
        Assert.Equal($"https://api.example.test/api/public/{e.Slug}/payments/monobank/webhook", inv.WebHookUrl);
        var o = paid.GetProperty("order");
        Assert.Equal("pending_payment", o.GetProperty("status").GetString());
        Assert.True(o.GetProperty("paymentInProgress").GetBoolean());
        Assert.False(o.GetProperty("canPay").GetBoolean());
        Assert.False(o.GetProperty("canCancel").GetBoolean());

        // повторний P4, поки клієнт на сторінці оплати: другого рахунку немає
        await Expect(await Pay(c, e, token), HttpStatusCode.Conflict, "payment_in_progress");
        Assert.Single(mono.Invoices);

        // проміжний статус - без змін
        await Expect(await Webhook(c, mono, e.Slug, inv, "processing"), HttpStatusCode.OK);
        Assert.Equal("pending_payment", (await Order(c, e, token)).GetProperty("status").GetString());

        inv.Status = "success";
        await Expect(await Webhook(c, mono, e.Slug, inv, "success"), HttpStatusCode.OK);
        var after = await Order(c, e, token);
        Assert.Equal("paid", after.GetProperty("status").GetString());
        Assert.Equal((1, 3m, "Paid"), await StateAsync(e));

        // повтор того самого вебхука і запізнілий processing (порядок не гарантовано) - стан той самий
        await Expect(await Webhook(c, mono, e.Slug, inv, "success"), HttpStatusCode.OK);
        await Expect(await Webhook(c, mono, e.Slug, inv, "processing"), HttpStatusCode.OK);
        await Expect(await Webhook(c, mono, e.Slug, inv, "failure"), HttpStatusCode.OK);
        Assert.Equal("paid", (await Order(c, e, token)).GetProperty("status").GetString());
        Assert.Equal((1, 3m, "Paid"), await StateAsync(e));
        // P4 після оплати - повтор
        var replay = await Pay(c, e, token);
        await Expect(replay, HttpStatusCode.OK);
        Assert.Equal("true", replay.Headers.GetValues("Idempotent-Replayed").Single());
    }

    [SkippableFact]
    public async Task bad_signature_wrong_amount_and_foreign_tenant_do_not_change_order()
    {
        NeedDb();
        var e = await NewEnvAsync();
        var other = await NewEnvAsync();
        var (c, mono) = NewHost();
        var token = await CreateOrderAsync(c, e);
        await Expect(await Pay(c, e, token), HttpStatusCode.OK);
        var inv = mono.Single();
        inv.Status = "success";

        await Expect(await Webhook(c, mono, e.Slug, inv, "success", sign: Convert.ToBase64String(new byte[70])), HttpStatusCode.Unauthorized);
        var forged = new FakeMonobankClient(new MonobankOptions { Token = "x", PublicBaseUrl = new Uri("https://x.test/") });
        var body = Encoding.UTF8.GetBytes("{}");
        await Expect(await Webhook(c, mono, e.Slug, inv, "success", sign: forged.Sign(body)), HttpStatusCode.Unauthorized);
        await Expect(await Webhook(c, mono, e.Slug, inv, "success", amount: 1), HttpStatusCode.OK); // підпис валідний, сума не та - ігнор
        await Expect(await Webhook(c, mono, other.Slug, inv, "success"), HttpStatusCode.OK); // чужий tenant: платежу під його RLS немає

        Assert.Equal("pending_payment", (await Order(c, e, token)).GetProperty("status").GetString());
        Assert.Equal((0, 5m, "Pending"), await StateAsync(e));
    }

    [SkippableFact]
    public async Task failure_webhook_releases_claim_and_next_pay_creates_new_invoice()
    {
        NeedDb();
        var e = await NewEnvAsync();
        var (c, mono) = NewHost();
        var token = await CreateOrderAsync(c, e);
        await Expect(await Pay(c, e, token), HttpStatusCode.OK);
        var first = mono.Single();
        first.Status = "failure";

        await Expect(await Webhook(c, mono, e.Slug, first, "failure"), HttpStatusCode.OK);
        var o = await Order(c, e, token);
        Assert.Equal("pending_payment", o.GetProperty("status").GetString());
        Assert.False(o.GetProperty("paymentInProgress").GetBoolean());
        Assert.True(o.GetProperty("canPay").GetBoolean());
        Assert.Equal((0, 5m, "Failed"), await StateAsync(e));

        var again = await Expect(await Pay(c, e, token), HttpStatusCode.OK);
        Assert.Equal(2, mono.Invoices.Count);
        var second = mono.Invoices.Values.Single(i => i.Id != first.Id);
        Assert.NotEqual(first.Reference, second.Reference); // новий PaymentId = нова спроба
        Assert.EndsWith(second.Id, again.GetProperty("paymentUrl").GetString());
    }

    [SkippableFact]
    public async Task reconcile_without_webhook_reads_invoice_status()
    {
        NeedDb();
        var e = await NewEnvAsync();
        var (c, mono) = NewHost();
        var token = await CreateOrderAsync(c, e);
        await Expect(await Pay(c, e, token), HttpStatusCode.OK);
        var inv = mono.Single();
        async Task Stale()
        {
            await using var ctx = fx.Db.CreateContext(e.T.TenantId);
            var hash = PublicTokenService.Hash(token);
            var o = await ctx.ProductOrders.FirstAsync(x => x.PublicTokenHash == hash);
            o.PaymentStartedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
            await ctx.SaveChangesAsync();
        }

        // клієнт ще на сторінці (created): лишається «в процесі», нового рахунку немає
        await Stale();
        await Expect(await Pay(c, e, token), HttpStatusCode.Conflict, "payment_in_progress");
        Assert.Single(mono.Invoices);

        // вебхук загубився, але банк каже success -> reconcile фіналізує
        inv.Status = "success";
        await Stale();
        var paid = await Expect(await Pay(c, e, token), HttpStatusCode.OK);
        Assert.Equal("paid", paid.GetProperty("order").GetProperty("status").GetString());
        Assert.Equal((1, 3m, "Paid"), await StateAsync(e));

        // запізнілий вебхук після reconcile - без другої фіналізації
        await Expect(await Webhook(c, mono, e.Slug, inv, "success"), HttpStatusCode.OK);
        Assert.Equal((1, 3m, "Paid"), await StateAsync(e));
    }

    [SkippableFact]
    public async Task expired_invoice_on_reconcile_is_payment_failed_and_retry_allowed()
    {
        NeedDb();
        var e = await NewEnvAsync();
        var (c, mono) = NewHost();
        var token = await CreateOrderAsync(c, e);
        await Expect(await Pay(c, e, token), HttpStatusCode.OK);
        mono.Single().Status = "expired"; // monobank не шле вебхук про expired
        await using (var ctx = fx.Db.CreateContext(e.T.TenantId))
        {
            var hash = PublicTokenService.Hash(token);
            var o = await ctx.ProductOrders.FirstAsync(x => x.PublicTokenHash == hash);
            o.PaymentStartedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
            await ctx.SaveChangesAsync();
        }
        var failed = await Expect(await Pay(c, e, token), HttpStatusCode.PaymentRequired, "payment_failed");
        Assert.True(failed.GetProperty("order").GetProperty("canPay").GetBoolean());
        await Expect(await Pay(c, e, token), HttpStatusCode.OK);
        Assert.Equal(2, mono.Invoices.Count);
    }

    [SkippableFact]
    public async Task return_page_links_back_to_app()
    {
        NeedDb();
        var (c, _) = NewHost();
        var r = await c.GetAsync("/api/public/payments/return");
        var html = await r.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("beautycrm://order", html);
        Assert.Equal("no-store", r.Headers.CacheControl?.ToString());
    }
}
