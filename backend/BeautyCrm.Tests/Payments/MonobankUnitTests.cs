using System.Text;
using BeautyCrm.Api.Payments;
using BeautyCrm.Api.Payments.Monobank;
using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyProductOrders;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeautyCrm.Tests.Payments;

internal sealed class ManualClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

internal sealed class FakeRefs : IPaymentRefStore
{
    public readonly Dictionary<Guid, StoredPaymentRef> Rows = new();

    public Task<StoredPaymentRef?> GetPaymentRefAsync(Guid paymentId, CancellationToken ct) =>
        Task.FromResult(Rows.TryGetValue(paymentId, out var r) ? r : null);

    public Task<bool> AttachProviderRefAsync(Guid paymentId, string provider, string providerPaymentId, CancellationToken ct)
    {
        if (!Rows.TryGetValue(paymentId, out var r) || r.Status != "pending" || r.ProviderPaymentId is not null) return Task.FromResult(false);
        Rows[paymentId] = r with { Provider = provider, ProviderPaymentId = providerPaymentId };
        return Task.FromResult(true);
    }

    public Guid AddOrderPayment(decimal amount, string status = "pending")
    {
        var id = Guid.NewGuid();
        Rows[id] = new StoredPaymentRef(id, null, Guid.NewGuid(), amount, status, null, null);
        return id;
    }
}

/// <summary>ADR-028: monobank без БД - підпис вебхука, ідемпотентність рахунку за PaymentId, статуси, повернення, конфіг.</summary>
public sealed class MonobankUnitTests
{
    private static readonly MonobankOptions Opt = new() { Token = "test-token", TestMode = true, PublicBaseUrl = new Uri("https://api.example.test/") };

    private static (MonobankPaymentService Svc, FakeMonobankClient Api, FakeRefs Refs) NewSvc()
    {
        var api = new FakeMonobankClient(Opt);
        var refs = new FakeRefs();
        return (new MonobankPaymentService(api, refs, Opt, new MockPaymentService(), NullLogger<MonobankPaymentService>.Instance), api, refs);
    }

    private static PaymentCharge Charge(Guid paymentId, decimal amount = 420.50m, string? slug = "salon") =>
        new(paymentId, PaymentPurpose.ProductOrder, Guid.NewGuid(), amount, "UAH", slug);

    [Fact]
    public async Task signature_valid_tampered_missing_and_rotated_key()
    {
        var api = new FakeMonobankClient(Opt);
        var sig = new MonobankSignature(api, TimeProvider.System, NullLogger<MonobankSignature>.Instance);
        var body = Encoding.UTF8.GetBytes("""{"invoiceId":"p2_x","status":"success"}""");

        Assert.True(await sig.VerifyAsync(body, api.Sign(body), default));
        Assert.False(await sig.VerifyAsync(Encoding.UTF8.GetBytes("""{"invoiceId":"p2_x","status":"failure"}"""), api.Sign(body), default));
        Assert.False(await sig.VerifyAsync(body, null, default));
        Assert.False(await sig.VerifyAsync(body, "not base64 !!", default));
        Assert.Equal(1, api.PubKeyCalls); // ключ кешується; невдалі перевірки в межах хвилини не перезапитують його

        // ротація ключа банком: старий кеш не підходить -> один перезапит (після cooldown)
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var sig2 = new MonobankSignature(api, clock, NullLogger<MonobankSignature>.Instance);
        Assert.True(await sig2.VerifyAsync(body, api.Sign(body), default));
        api.RotateKey();
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True(await sig2.VerifyAsync(body, api.Sign(body), default));
    }

    [Fact]
    public async Task charge_creates_one_invoice_per_payment_then_reads_its_status()
    {
        var (svc, api, refs) = NewSvc();
        var pid = refs.AddOrderPayment(420.50m);

        var first = await svc.ChargeAsync(Charge(pid), default);
        Assert.True(first.Pending);
        Assert.False(first.Success);
        var inv = api.Single();
        Assert.Equal(first.ProviderPaymentId, inv.Id);
        Assert.Equal($"https://pay.mbnk.biz/{inv.Id}", first.CheckoutUrl);
        Assert.Equal(42050, inv.Amount);
        Assert.Equal(pid.ToString("N"), inv.Reference);
        Assert.Equal("https://api.example.test/api/public/salon/payments/monobank/webhook", inv.WebHookUrl);
        Assert.Equal("https://api.example.test/api/public/payments/return", inv.RedirectUrl);
        Assert.Equal(300, inv.Validity);
        Assert.Equal(inv.Id, refs.Rows[pid].ProviderPaymentId);

        // повтор (reconcile) - той самий рахунок, без URL, новий не створюється
        var again = await svc.ChargeAsync(Charge(pid), default);
        Assert.True(again.Pending);
        Assert.Null(again.CheckoutUrl);
        Assert.Single(api.Invoices);

        inv.Status = "success";
        var paid = await svc.ChargeAsync(Charge(pid), default);
        Assert.True(paid.Success);
        Assert.Equal(inv.Id, paid.ProviderPaymentId);
    }

    [Theory]
    [InlineData("failure", "card_declined")]
    [InlineData("expired", "invoice_expired")]
    public async Task terminal_invoice_status_is_explicit_decline(string status, string code)
    {
        var (svc, api, refs) = NewSvc();
        var pid = refs.AddOrderPayment(100m);
        await svc.ChargeAsync(Charge(pid), default);
        api.Single().Status = status;
        var r = await svc.ChargeAsync(Charge(pid), default);
        Assert.False(r.Success);
        Assert.False(r.Pending);
        Assert.Equal(code, r.Error);
        Assert.True(ProductOrderPaymentFlow.IsExplicitDecline(r));
    }

    [Fact]
    public async Task network_failure_on_create_is_unknown_and_without_slug_no_invoice_is_created()
    {
        var (svc, api, refs) = NewSvc();
        var pid = refs.AddOrderPayment(100m);
        api.FailCreate = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => svc.ChargeAsync(Charge(pid), default));
        Assert.Null(refs.Rows[pid].ProviderPaymentId);

        api.FailCreate = false;
        var noSlug = await svc.ChargeAsync(Charge(pid, slug: null), default);
        Assert.Equal("invoice_not_created", noSlug.Error);
        Assert.Empty(api.Invoices);
    }

    [Fact]
    public async Task appointments_stay_on_mock()
    {
        var (svc, api, _) = NewSvc();
        var r = await svc.ChargeAsync(Guid.NewGuid(), 500m, default);
        Assert.True(r.Success);
        Assert.StartsWith("mock_pay_", r.ProviderPaymentId);
        Assert.Empty(api.Invoices);
    }

    [Fact]
    public async Task refund_cancels_invoice_once_with_payment_id_as_ext_ref()
    {
        var (svc, api, refs) = NewSvc();
        var pid = refs.AddOrderPayment(250m);
        await svc.ChargeAsync(Charge(pid, 250m), default);
        var inv = api.Single();

        Assert.Equal("not_refundable", (await svc.RefundAsync(pid, 250m, default)).Error); // ще не оплачено

        inv.Status = "success";
        var r = await svc.RefundAsync(pid, 250m, default);
        Assert.True(r.Success);
        Assert.Equal((pid.ToString("N"), 25000L), Assert.Single(inv.Cancels));

        var again = await svc.RefundAsync(pid, 250m, default); // повтор після збою: уже reversed, другого скасування немає
        Assert.True(again.Success);
        Assert.Single(inv.Cancels);
    }

    [Theory]
    [InlineData("success", ProviderPaymentState.Paid)]
    [InlineData("failure", ProviderPaymentState.Failed)]
    [InlineData("expired", ProviderPaymentState.Failed)]
    [InlineData("reversed", ProviderPaymentState.Failed)]
    [InlineData("created", ProviderPaymentState.Pending)]
    [InlineData("processing", ProviderPaymentState.Pending)]
    [InlineData("hold", ProviderPaymentState.Pending)]
    [InlineData(null, ProviderPaymentState.Pending)]
    public void status_mapping(string? status, ProviderPaymentState expected) => Assert.Equal(expected, MonobankPaymentService.MapState(status));

    private static IConfiguration Cfg(params (string K, string? V)[] kv) =>
        new ConfigurationBuilder().AddInMemoryCollection(kv.Select(x => new KeyValuePair<string, string?>(x.K, x.V))).Build();

    [Fact]
    public void config_requires_token_test_mode_and_https_public_base()
    {
        var ok = PaymentsSetup.ReadMonobank(Cfg(("Monobank:Token", "t"), ("Monobank:TestMode", "true"), ("Monobank:PublicBaseUrl", "https://beauty.example.test/base")));
        Assert.Equal("https://beauty.example.test/base/api/public/payments/return", ok.RedirectUrl);
        Assert.Equal(300, ok.InvoiceValiditySeconds);

        Assert.Throws<InvalidOperationException>(() => PaymentsSetup.ReadMonobank(Cfg(("Monobank:TestMode", "true"), ("Monobank:PublicBaseUrl", "https://x.test"))));
        var live = Assert.Throws<InvalidOperationException>(() => PaymentsSetup.ReadMonobank(Cfg(("Monobank:Token", "t"), ("Monobank:TestMode", "false"), ("Monobank:PublicBaseUrl", "https://x.test"))));
        Assert.Contains("TestMode", live.Message);
        Assert.DoesNotContain("t\"", live.Message);
        Assert.Throws<InvalidOperationException>(() => PaymentsSetup.ReadMonobank(Cfg(("Monobank:Token", "t"), ("Monobank:TestMode", "true"), ("Monobank:PublicBaseUrl", "http://x.test"))));
        Assert.Throws<InvalidOperationException>(() => PaymentsSetup.ReadMonobank(Cfg(("Monobank:Token", "t"), ("Monobank:TestMode", "true"), ("Monobank:PublicBaseUrl", "https://x.test"), ("Monobank:InvoiceValiditySeconds", "10"))));
        Assert.Throws<InvalidOperationException>(() => new Microsoft.Extensions.DependencyInjection.ServiceCollection().AddBeautyPayments(Cfg(("Payments:Provider", "stripe"))));
    }
}
