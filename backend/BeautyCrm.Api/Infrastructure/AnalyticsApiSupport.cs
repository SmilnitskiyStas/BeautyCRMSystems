using System.Threading.RateLimiting;

namespace BeautyCrm.Api.Infrastructure;

/// <summary>
/// Ліміт <c>GET /analytics/network/details</c> на пару (tenant, користувач) (§24.2 <c>Analytics:DetailsPerUserPerMinute</c>, 10..600).
/// Вбудований rate limiter стоїть до автентифікації й не знає користувача, тож перевірка йде в контролері (патерн <see cref="AbsenceThrottle"/>).
/// </summary>
public sealed class AnalyticsDetailsThrottle
{
    private readonly PartitionedRateLimiter<string> _limiter;
    private readonly TimeSpan _window = TimeSpan.FromMinutes(1);

    public AnalyticsDetailsThrottle(int permitPerMinute) =>
        _limiter = PartitionedRateLimiter.Create<string, string>(key => RateLimitPartition.GetFixedWindowLimiter(key,
            _ => new FixedWindowRateLimiterOptions { PermitLimit = Math.Clamp(permitPerMinute, 10, 600), Window = _window, QueueLimit = 0 }));

    /// <summary>false = перевищено; <paramref name="retryAfter"/> - скільки секунд чекати (>= 1).</summary>
    public bool TryAcquire(Guid tenantId, Guid userId, out int retryAfter)
    {
        using var lease = _limiter.AttemptAcquire($"{tenantId:N}:{userId:N}");
        retryAfter = 0;
        if (lease.IsAcquired) return true;
        retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var ra) ? Math.Max(1, (int)Math.Ceiling(ra.TotalSeconds)) : (int)_window.TotalSeconds;
        return false;
    }
}

public static class AnalyticsApiSupport
{
    public static IServiceCollection AddBeautyAnalyticsApi(this IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton(new AnalyticsDetailsThrottle(config.GetValue("Analytics:DetailsPerUserPerMinute", 60)));
        return services;
    }

    /// <summary>Cache-Control: no-store на всіх /api/beauty/analytics/* - також для 401/403/429/4xx (§24, зміна §19.6).</summary>
    public static IApplicationBuilder UseBeautyAnalyticsNoStore(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        if (ctx.Request.Path.StartsWithSegments("/api/beauty/analytics"))
            ctx.Response.OnStarting(() =>
            {
                ctx.Response.Headers.CacheControl = "no-store";
                return Task.CompletedTask;
            });
        await next();
    });
}
