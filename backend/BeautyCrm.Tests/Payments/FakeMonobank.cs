using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using BeautyCrm.Api.Payments.Monobank;

namespace BeautyCrm.Tests.Payments;

/// <summary>Фейковий acquiring API monobank: рахунки в пам'яті, власний ECDSA-ключ для підпису вебхуків (як у банку: base64(PEM)).</summary>
internal sealed class FakeMonobankClient : MonobankClient
{
    public sealed record Invoice(string Id, long Amount, string Reference, string WebHookUrl, string RedirectUrl, int Validity)
    {
        public string Status { get; set; } = "created";
        public List<(string ExtRef, long Amount)> Cancels { get; } = [];
    }

    public readonly ConcurrentDictionary<string, Invoice> Invoices = new();
    public ECDsa Key { get; private set; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public int PubKeyCalls;
    public bool FailCreate;
    public string CancelStatus = "processing";

    public FakeMonobankClient(MonobankOptions o) : base(new HttpClient(), o) { }

    public void RotateKey() => Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public static string PubKeyOf(ECDsa k) => Convert.ToBase64String(Encoding.UTF8.GetBytes(k.ExportSubjectPublicKeyInfoPem()));

    public string Sign(byte[] body) => Convert.ToBase64String(Key.SignData(body, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

    public Invoice Single() => Assert.Single(Invoices.Values);

    public override Task<MonobankCreatedInvoice> CreateInvoiceAsync(long amountMinor, string reference, string destination, string redirectUrl, string webHookUrl,
        int validitySeconds, CancellationToken ct)
    {
        if (FailCreate) throw new HttpRequestException("down");
        var id = "p2_" + Guid.NewGuid().ToString("N")[..10];
        Invoices[id] = new Invoice(id, amountMinor, reference, webHookUrl, redirectUrl, validitySeconds);
        return Task.FromResult(new MonobankCreatedInvoice(id, $"https://pay.mbnk.biz/{id}"));
    }

    public override Task<MonobankInvoiceStatus> GetStatusAsync(string invoiceId, CancellationToken ct) =>
        Invoices.TryGetValue(invoiceId, out var i)
            ? Task.FromResult(new MonobankInvoiceStatus(i.Id, i.Status, null, null, i.Amount, 980, i.Amount, null, DateTimeOffset.UtcNow, i.Reference))
            : throw new MonobankRejectedException(System.Net.HttpStatusCode.NotFound, "404");

    public override Task<MonobankCancelResult> CancelAsync(string invoiceId, string extRef, long amountMinor, CancellationToken ct)
    {
        var i = Invoices[invoiceId];
        i.Cancels.Add((extRef, amountMinor));
        if (CancelStatus is "success" or "processing") i.Status = "reversed";
        return Task.FromResult(new MonobankCancelResult(CancelStatus));
    }

    public override Task<string> GetPublicKeyAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref PubKeyCalls);
        return Task.FromResult(PubKeyOf(Key));
    }
}
