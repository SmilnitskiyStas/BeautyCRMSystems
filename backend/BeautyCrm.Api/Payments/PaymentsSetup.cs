using BeautyCrm.Api.Payments.Monobank;
using BeautyCrm.Application.Features.BeautyBooking;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BeautyCrm.Api.Payments;

/// <summary>
/// Вибір провайдера оплати (ADR-028). <c>Payments:Provider</c> = <c>mock</c> (за замовчуванням) | <c>monobank</c>.
/// monobank: <c>Monobank:Token</c> (лише секрет/змінна середовища <c>Monobank__Token</c>), <c>Monobank:TestMode</c> = true (живий режим
/// у першому релізі заборонено - помилка старту), <c>Monobank:PublicBaseUrl</c> (https, публічна адреса API для вебхука й повернення),
/// <c>Monobank:InvoiceValiditySeconds</c> (60..3600, 300), <c>Monobank:ApiBaseUrl</c>, <c>Monobank:AppReturnUrl</c>. Невалідне значення = помилка старту.
/// Викликати ПІСЛЯ AddBeautyApplication (підміняє mock-реєстрацію).
/// </summary>
public static class PaymentsSetup
{
    public static IServiceCollection AddBeautyPayments(this IServiceCollection services, IConfiguration config)
    {
        var provider = (config["Payments:Provider"] ?? "mock").Trim().ToLowerInvariant();
        switch (provider)
        {
            case "" or "mock":
                return services;
            case MonobankOptions.ProviderName:
                break;
            default:
                throw new InvalidOperationException("Payments:Provider must be 'mock' or 'monobank'.");
        }

        var options = ReadMonobank(config);
        services.AddSingleton(options);
        services.TryAddSingleton<MockPaymentService>();
        services.AddHttpClient<MonobankClient>(c => c.Timeout = TimeSpan.FromSeconds(20));
        // singleton (кеш ключа на процес) з власним клієнтом поверх IHttpClientFactory
        services.AddSingleton(sp => new MonobankSignature(
            new MonobankClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(MonobankClient)), options),
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<MonobankSignature>>()));
        // scoped: читає/пише beauty_payments під RLS tenant поточного запиту
        services.Replace(ServiceDescriptor.Scoped<IPaymentService, MonobankPaymentService>());
        return services;
    }

    public static MonobankOptions ReadMonobank(IConfiguration config)
    {
        var token = config["Monobank:Token"]?.Trim();
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException("Payments:Provider=monobank requires Monobank:Token (set it via the Monobank__Token secret, never in git).");
        if (!string.Equals(config["Monobank:TestMode"]?.Trim(), "true", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Monobank:TestMode must be true: only the monobank test environment is enabled (ADR-028).");
        var publicBase = HttpsUri(config["Monobank:PublicBaseUrl"], "Monobank:PublicBaseUrl", required: true)!;
        var apiBase = HttpsUri(config["Monobank:ApiBaseUrl"], "Monobank:ApiBaseUrl", required: false) ?? new Uri("https://api.monobank.ua/");
        var validity = 300;
        if (config["Monobank:InvoiceValiditySeconds"] is { Length: > 0 } raw
            && (!int.TryParse(raw.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out validity) || validity is < 60 or > 3600))
            throw new InvalidOperationException("Monobank:InvoiceValiditySeconds must be an integer in 60..3600.");
        var appReturn = config["Monobank:AppReturnUrl"]?.Trim();
        if (!string.IsNullOrEmpty(appReturn) && !Uri.TryCreate(appReturn, UriKind.Absolute, out _))
            throw new InvalidOperationException("Monobank:AppReturnUrl must be an absolute URL (e.g. beautycrm://order).");
        return new MonobankOptions
        {
            Token = token, TestMode = true, PublicBaseUrl = publicBase, ApiBaseUrl = apiBase, InvoiceValiditySeconds = validity,
            AppReturnUrl = string.IsNullOrEmpty(appReturn) ? "beautycrm://order" : appReturn,
        };
    }

    private static Uri? HttpsUri(string? raw, string key, bool required)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return required ? throw new InvalidOperationException($"{key} is required for Payments:Provider=monobank.") : null;
        var v = raw.Trim();
        if (!v.EndsWith('/')) v += "/"; // база для відносних шляхів
        return Uri.TryCreate(v, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(u.Query)
            ? u
            : throw new InvalidOperationException($"{key} must be an absolute https URL without a query.");
    }
}
