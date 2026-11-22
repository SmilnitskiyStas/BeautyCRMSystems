namespace BeautyCrm.Api.Payments.Monobank;

/// <summary>
/// Конфігурація <c>Monobank:*</c> (ADR-028). Токен - лише зі змінної середовища/секрету (<c>Monobank__Token</c>), ніколи з git і не в логах.
/// Перший реліз - лише тестове середовище monobank (<c>Monobank:TestMode=true</c>, тестовий токен з api.monobank.ua).
/// </summary>
public sealed class MonobankOptions
{
    public const string ProviderName = "monobank";

    public string Token { get; init; } = "";
    public bool TestMode { get; init; }
    /// <summary>API monobank (за замовчуванням https://api.monobank.ua; тест і прод - той самий хост, режим задає токен).</summary>
    public Uri ApiBaseUrl { get; init; } = new("https://api.monobank.ua/");
    /// <summary>Публічна https-адреса цього API: з неї будуються webHookUrl і redirectUrl рахунку.</summary>
    public Uri PublicBaseUrl { get; init; } = null!;
    /// <summary>Строк дії рахунку, с (60..3600, за замовчуванням 300): після нього рахунок expired і спробу можна повторити.</summary>
    public int InvoiceValiditySeconds { get; init; } = 300;
    /// <summary>Глибоке посилання застосунку для сторінки повернення після оплати.</summary>
    public string AppReturnUrl { get; init; } = "beautycrm://order";

    public string WebhookUrl(string tenantSlug) =>
        new Uri(PublicBaseUrl, $"api/public/{Uri.EscapeDataString(tenantSlug)}/payments/monobank/webhook").ToString();

    public string RedirectUrl => new Uri(PublicBaseUrl, "api/public/payments/return").ToString();
}
