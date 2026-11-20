using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyProductOrders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BeautyCrm.Api.PublicBooking;

public static class ProductOrdersSetup
{
    /// <summary>
    /// Замовлення товарів (ADR-026, §33.3). <c>ProductOrders:Enabled</c>: лише литерал <c>true</c> (без регістру) вмикає. Невалідне значення
    /// діапазонного ключа = помилка старту. Prod-запобіжник (Production/невизначене середовище + Enabled + MockPaymentService) спрацьовує на старті хоста.
    /// </summary>
    public static IServiceCollection AddBeautyProductOrdersApi(this IServiceCollection services, IConfiguration config, IHostEnvironment? env = null)
    {
        var options = new ProductOrdersOptions
        {
            Enabled = ProductOrdersOptions.IsOn(config["ProductOrders:Enabled"]),
            ReservationMinutes = Range(config, "ReservationMinutes", 15, 5, 60),
            PaymentStaleSeconds = Range(config, "PaymentStaleSeconds", 120, 30, 900),
            MaxPaymentAttempts = Range(config, "MaxPaymentAttempts", 5, 1, 10),
            MaxPendingPerPhone = Range(config, "MaxPendingPerPhone", 2, 1, 10),
            MaxCreatesPerPhonePerHour = Range(config, "MaxCreatesPerPhonePerHour", 5, 1, 50),
            MaxCreatesPerLocationPerHour = Range(config, "MaxCreatesPerLocationPerHour", 60, 0, 1000),
            MaxOrderTotal = Range(config, "MaxOrderTotal", 50_000, 1, 1_000_000),
        };
        services.AddBeautyProductOrdersApplication(options);
        var productionOrUnknown = env is null || env.IsProduction();
        services.AddSingleton<IHostedService>(sp => new ProductOrdersStartupGuard(options, productionOrUnknown, sp.GetRequiredService<IPaymentService>(), sp.GetService<ILogger<ProductOrdersStartupGuard>>()));
        return services;
    }

    private static int Range(IConfiguration config, string key, int def, int min, int max)
    {
        var raw = config[$"ProductOrders:{key}"];
        if (string.IsNullOrWhiteSpace(raw)) return def;
        return int.TryParse(raw.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= min && v <= max
            ? v
            : throw new InvalidOperationException($"ProductOrders:{key} must be an integer in {min}..{max}.");
    }
}

/// <summary>Падає на старті, якщо замовлення товарів увімкнено у Production з mock-оплатою.</summary>
public sealed class ProductOrdersStartupGuard(ProductOrdersOptions options, bool productionOrUnknown, IPaymentService payments, ILogger<ProductOrdersStartupGuard>? log = null) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var mock = payments is MockPaymentService;
        ProductOrdersOptions.ValidateForHost(options.Enabled, productionOrUnknown, mock);
        // Не-Production: mock дозволено (dev/staging/тести), але гучно попереджаємо - картки не списуються по-справжньому.
        if (options.Enabled && mock)
            log?.LogWarning("ProductOrders:Enabled=true with MockPaymentService (non-Production environment): no real card charges are made.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
