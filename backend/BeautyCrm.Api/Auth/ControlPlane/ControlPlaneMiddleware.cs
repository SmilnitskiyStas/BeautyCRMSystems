using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;

namespace BeautyCrm.Api.Auth.ControlPlane;

/// <summary>
/// Шлюз групи <c>/api/control/*</c> (ADR-018). Для кожного запиту групи (зокрема на неіснуючий маршрут, щоб не розкривати
/// карту ендпоінтів): заголовки <c>X-BCRM-Server-Time</c> і <c>Cache-Control: no-store</c>; непорожній query -> 400
/// <c>query_not_allowed</c> ДО перевірки підпису; далі лише схема ControlPlaneHmac (JWT і X-Platform-Key тут не діють) —
/// невдача = 401 <c>control_auth_failed</c> (або 429 після валідного підпису). Ендпоінти керування — TASK-818.
/// </summary>
public sealed class ControlPlaneMiddleware(RequestDelegate next, TimeProvider time)
{
    public static readonly PathString Prefix = new("/api/control");

    public async Task InvokeAsync(HttpContext http)
    {
        if (!http.Request.Path.StartsWithSegments(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            await next(http);
            return;
        }

        http.Response.Headers[ControlPlaneSignature.ServerTimeHeader] =
            time.GetUtcNow().ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        http.Response.Headers.CacheControl = "no-store";

        // Лише '?' без параметрів = порожній query (canonical "").
        var query = http.Request.QueryString.Value;
        if (!string.IsNullOrEmpty(query) && query != "?")
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            http.Response.ContentType = "application/json; charset=utf-8";
            await http.Response.WriteAsync("{\"code\":\"query_not_allowed\"}");
            return;
        }

        var auth = await http.AuthenticateAsync(ControlPlaneHmacHandler.Scheme);
        if (!auth.Succeeded || auth.Principal is null)
        {
            await http.ChallengeAsync(ControlPlaneHmacHandler.Scheme);
            return;
        }

        http.User = new ClaimsPrincipal(auth.Principal); // JWT/інші схеми не змішуються з control plane
        await next(http);
    }
}

public static class ControlPlaneExtensions
{
    /// <summary>Реєструє схему ControlPlaneHmac, політику <see cref="AuthPolicies.ControlPlane"/> та залежності (викликається з AddBeautyAuthApi).</summary>
    public static IServiceCollection AddControlPlaneHmac(this IServiceCollection services)
    {
        services.AddSingleton(sp => ControlPlaneInboundOptions.From(
            sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<ILoggerFactory>().CreateLogger("BeautyCrm.ControlPlane")));
        services.AddSingleton(sp => new ControlPlaneRateLimiter(sp.GetRequiredService<TimeProvider>()));
        services.AddScoped<IControlPlaneNonceStore, EfControlPlaneNonceStore>();
        services.AddSingleton(sp =>
        {
            var o = new BeautyCrm.Application.Features.BeautyControlPlane.ControlPlaneOptions();
            sp.GetRequiredService<IConfiguration>().GetSection("ControlPlane").Bind(o); // PublicAppUrl, InviteTtlHours
            return o;
        });
        return services;
    }

    /// <summary>Після UseAuthentication, до TenantMiddleware й авторизації.</summary>
    public static IApplicationBuilder UseControlPlane(this IApplicationBuilder app) => app.UseMiddleware<ControlPlaneMiddleware>();
}
