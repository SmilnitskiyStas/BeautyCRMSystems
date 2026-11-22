using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BeautyCrm.Api.Payments.Monobank;

/// <summary>Відповідь «Статус рахунку» і тіло вебхука (однакова структура, api.monobank.ua/docs/acquiring.html). Зайві поля ігноруються.</summary>
public sealed record MonobankInvoiceStatus(
    string? InvoiceId, string? Status, string? FailureReason, string? ErrCode, long? Amount, int? Ccy, long? FinalAmount,
    DateTimeOffset? CreatedDate, DateTimeOffset? ModifiedDate, string? Reference);

public sealed record MonobankCreatedInvoice(string InvoiceId, string PageUrl);

public sealed record MonobankCancelResult(string? Status);

/// <summary>Відмова monobank з кодом 4xx (запит невалідний/токен невалідний/не знайдено): результат відомий, повтор не допоможе.</summary>
public sealed class MonobankRejectedException(HttpStatusCode status, string? errCode) : Exception($"monobank rejected: {(int)status} {errCode}")
{
    public HttpStatusCode Status { get; } = status;
    public string? ErrCode { get; } = errCode;
}

/// <summary>
/// Тонкий HTTP-клієнт acquiring API (X-Token у заголовку). 5xx / 429 / мережа -> виняток HttpRequestException/TaskCanceled
/// (результат НЕВІДОМИЙ для викликача); 4xx -> <see cref="MonobankRejectedException"/>. Тіла відповідей і токен у логи не пишуться.
/// </summary>
public class MonobankClient(HttpClient http, MonobankOptions options)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public virtual async Task<MonobankCreatedInvoice> CreateInvoiceAsync(
        long amountMinor, string reference, string destination, string redirectUrl, string webHookUrl, int validitySeconds, CancellationToken ct)
    {
        var body = new
        {
            amount = amountMinor,
            ccy = 980,
            merchantPaymInfo = new { reference, destination },
            redirectUrl,
            webHookUrl,
            validity = validitySeconds,
            paymentType = "debit",
        };
        var r = await SendAsync<MonobankCreatedInvoice>(HttpMethod.Post, "api/merchant/invoice/create", body, ct);
        if (string.IsNullOrWhiteSpace(r?.InvoiceId) || string.IsNullOrWhiteSpace(r.PageUrl)) throw new HttpRequestException("monobank: empty invoice response");
        return r;
    }

    public virtual async Task<MonobankInvoiceStatus> GetStatusAsync(string invoiceId, CancellationToken ct) =>
        await SendAsync<MonobankInvoiceStatus>(HttpMethod.Get, $"api/merchant/invoice/status?invoiceId={Uri.EscapeDataString(invoiceId)}", null, ct)
        ?? throw new HttpRequestException("monobank: empty status response");

    public virtual async Task<MonobankCancelResult> CancelAsync(string invoiceId, string extRef, long amountMinor, CancellationToken ct) =>
        await SendAsync<MonobankCancelResult>(HttpMethod.Post, "api/merchant/invoice/cancel", new { invoiceId, extRef, amount = amountMinor }, ct)
        ?? throw new HttpRequestException("monobank: empty cancel response");

    /// <summary>Base64 від PEM відкритого ECDSA-ключа для перевірки X-Sign.</summary>
    public virtual async Task<string> GetPublicKeyAsync(CancellationToken ct)
    {
        var r = await SendAsync<PubKey>(HttpMethod.Get, "api/merchant/pubkey", null, ct);
        return string.IsNullOrWhiteSpace(r?.Key) ? throw new HttpRequestException("monobank: empty pubkey") : r.Key;
    }

    private sealed record PubKey(string? Key);
    private sealed record ErrorBody(string? ErrCode, string? ErrText);

    private async Task<T?> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, new Uri(options.ApiBaseUrl, path));
        req.Headers.TryAddWithoutValidation("X-Token", options.Token);
        if (body is not null) req.Content = JsonContent.Create(body, options: Json);
        using var res = await http.SendAsync(req, ct);
        if ((int)res.StatusCode is >= 400 and < 500 && res.StatusCode != HttpStatusCode.TooManyRequests)
        {
            ErrorBody? err = null;
            try { err = await res.Content.ReadFromJsonAsync<ErrorBody>(Json, ct); } catch (JsonException) { }
            throw new MonobankRejectedException(res.StatusCode, err?.ErrCode);
        }
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"monobank: http {(int)res.StatusCode}", null, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<T>(Json, ct);
    }
}
