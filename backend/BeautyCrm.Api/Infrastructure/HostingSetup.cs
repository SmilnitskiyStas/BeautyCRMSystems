using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;

namespace BeautyCrm.Api.Infrastructure;

/// <summary>
/// Хостинг-захист API (TASK-689): довірені проксі (ForwardedHeaders), CORS для адмінки, security headers.
/// Усе керується конфігом; значення за замовчуванням — закрито (проксі не довіряємо, CORS вимкнено).
/// </summary>
public static class HostingSetup
{
    public const string CorsPolicy = "beauty-admin";

    /// <summary>Список з конфігу: масив (<c>Key:0</c>, <c>Key:1</c>) або рядок через кому / крапку з комою.</summary>
    public static string[] GetList(this IConfiguration config, string key)
    {
        var section = config.GetSection(key);
        var items = section.GetChildren().Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        if (section.Value is { Length: > 0 } flat) items.AddRange(flat.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return items.Select(v => v!.Trim()).Distinct(StringComparer.Ordinal).ToArray();
    }

    // ---------- довірені проксі (H3) ----------

    /// <summary>
    /// Beauty:TrustedProxies — IP або CIDR (10.0.0.0/8) reverse proxy. Лише для них враховується X-Forwarded-For/Proto;
    /// ланцюг розбирається справа наліво до першого НЕдовіреного адреса (ForwardLimit = null). Порожній список =
    /// X-Forwarded-* ігноруються повністю (ключ rate limit = IP з'єднання).
    /// </summary>
    public static bool UseBeautyForwardedHeaders(this WebApplication app)
    {
        var trusted = app.Configuration.GetList("Beauty:TrustedProxies");
        if (trusted.Length == 0) return false;

        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = null,
            RequireHeaderSymmetry = false,
        };
        options.KnownProxies.Clear();
        options.KnownNetworks.Clear();
        foreach (var entry in trusted)
        {
            var slash = entry.IndexOf('/');
            if (slash < 0)
            {
                if (!IPAddress.TryParse(entry, out var ip))
                    throw new InvalidOperationException($"Beauty:TrustedProxies: '{entry}' is not an IP address or CIDR.");
                options.KnownProxies.Add(ip);
            }
            else if (IPAddress.TryParse(entry[..slash], out var prefix) && int.TryParse(entry[(slash + 1)..], out var bits)
                     && bits >= 0 && bits <= (prefix.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128))
            {
                options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(prefix, bits));
            }
            else
            {
                throw new InvalidOperationException($"Beauty:TrustedProxies: '{entry}' is not an IP address or CIDR.");
            }
        }
        app.UseForwardedHeaders(options);
        return true;
    }

    // ---------- CORS для адмінки ----------

    /// <summary>
    /// Beauty:AllowedOrigins — точні origin (https://admin.example.com). Credentials (cookies) не потрібні: авторизація
    /// заголовком Authorization. Порожній список = CORS вимкнено (браузер блокує крос-origin). "*" відхиляється.
    /// </summary>
    public static IServiceCollection AddBeautyCors(this IServiceCollection services, IConfiguration config)
    {
        var origins = config.GetList("Beauty:AllowedOrigins").Select(NormalizeOrigin).ToArray();
        services.AddCors(o => o.AddPolicy(CorsPolicy, p =>
        {
            if (origins.Length == 0) return;
            p.WithOrigins(origins)
                .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS")
                .WithHeaders("Authorization", "Content-Type", "Idempotency-Key", "X-Captcha-Token")
                .WithExposedHeaders("Idempotent-Replayed")
                .SetPreflightMaxAge(TimeSpan.FromMinutes(10));
            // AllowCredentials навмисно не викликається.
        }));
        return services;
    }

    private static string NormalizeOrigin(string raw)
    {
        if (raw == "*" || !Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || uri.PathAndQuery != "/" || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException($"Beauty:AllowedOrigins: '{raw}' is not a valid origin (scheme://host[:port], no wildcard).");
        return uri.GetLeftPart(UriPartial.Authority);
    }

    public static bool UseBeautyCors(this WebApplication app)
    {
        if (app.Configuration.GetList("Beauty:AllowedOrigins").Length == 0) return false;
        app.UseCors(CorsPolicy);
        return true;
    }

    // ---------- security headers ----------

    /// <summary>
    /// X-Content-Type-Options: nosniff, Referrer-Policy, X-Frame-Options (+ CSP "default-src 'none'" поза Development, бо Swagger UI
    /// потребує скриптів). HSTS — поза Development і лише на HTTPS-запитах (по довіреному X-Forwarded-Proto).
    /// </summary>
    public static IApplicationBuilder UseBeautySecurityHeaders(this IApplicationBuilder app, IHostEnvironment env)
    {
        var development = env.IsDevelopment();
        return app.Use(async (http, next) =>
        {
            http.Response.OnStarting(() =>
            {
                var h = http.Response.Headers;
                h["X-Content-Type-Options"] = "nosniff";
                h["Referrer-Policy"] = "no-referrer";
                h["X-Frame-Options"] = "DENY";
                if (!development)
                {
                    h["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
                    if (http.Request.IsHttps) h["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
                }
                return Task.CompletedTask;
            });
            await next();
        });
    }
}

/// <summary>
/// Додатковий ліміт refresh за токеном і tenant (H3): сам refresh-токен одноразовий (ротація), тож повторні спроби
/// з тим самим токеном — це replay/перебір. Широкий ліміт на IP лишається окремою політикою "auth-refresh".
/// </summary>
/// <summary>
/// Ліміт POST /specialists/{id}/absences на КОРИСТУВАЧА (TASK-696). Вбудований rate limiter стоїть до автентифікації й не знає
/// користувача (а ключ зі спуфленого токена дозволив би виснажувати чужий ліміт), тож ліміт перевіряється в контролері після
/// автентифікації за claim sub.
/// </summary>
public sealed class AbsenceThrottle
{
    private readonly PartitionedRateLimiter<string> _perUser;

    public AbsenceThrottle(int permit, TimeSpan window) =>
        _perUser = PartitionedRateLimiter.Create<string, string>(key => RateLimitPartition.GetFixedWindowLimiter(key,
            _ => new FixedWindowRateLimiterOptions { PermitLimit = permit, Window = window, QueueLimit = 0 }));

    /// <summary>false = перевищено.</summary>
    public bool TryAcquire(Guid tenantId, Guid userId)
    {
        using var lease = _perUser.AttemptAcquire($"{tenantId:N}:{userId:N}");
        return lease.IsAcquired;
    }
}

public sealed class RefreshThrottle
{
    private readonly PartitionedRateLimiter<string> _perToken;
    private readonly PartitionedRateLimiter<string> _perTenant;

    public RefreshThrottle(int perTokenPermit, int perTenantPermit, TimeSpan window)
    {
        _perToken = Create(perTokenPermit, window);
        _perTenant = Create(perTenantPermit, window);
    }

    private static PartitionedRateLimiter<string> Create(int permit, TimeSpan window) =>
        PartitionedRateLimiter.Create<string, string>(key => RateLimitPartition.GetFixedWindowLimiter(key,
            _ => new FixedWindowRateLimiterOptions { PermitLimit = permit, Window = window, QueueLimit = 0 }));

    /// <summary>false = перевищено. tokenHash — SHA-256 токена (не сам токен).</summary>
    public bool TryAcquire(Guid tenantId, string tokenHash)
    {
        using var tenantLease = _perTenant.AttemptAcquire(tenantId.ToString("N"));
        if (!tenantLease.IsAcquired) return false;
        using var tokenLease = _perToken.AttemptAcquire(tokenHash);
        return tokenLease.IsAcquired;
    }
}
