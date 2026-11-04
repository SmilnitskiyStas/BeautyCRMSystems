using System.Security.Claims;
using System.Threading.RateLimiting;
using BeautyCrm.Api.Auth.ControlPlane;
using BeautyCrm.Api.Infrastructure;
using Microsoft.Extensions.DependencyInjection.Extensions;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyStaff;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;

namespace BeautyCrm.Api.Auth;

public static class AuthPolicies
{
    /// <summary>owner | admin | specialist.</summary>
    public const string Staff = "Staff";
    /// <summary>owner | admin.</summary>
    public const string Management = "Management";
    /// <summary>лише owner.</summary>
    public const string Owner = "Owner";
    public const string PlatformOperator = "PlatformOperator";
    /// <summary>ControlPlaneHmac (ADR-018): лише для /api/control/*.</summary>
    public const string ControlPlane = "ControlPlane";
    public const string RateLimit = "auth";
    /// <summary>Ширший ліміт на IP для refresh (клієнти за одним NAT оновлюють токени регулярно); доповнюється RefreshThrottle.</summary>
    public const string RefreshRateLimit = "auth-refresh";
    /// <summary>Публічні inspect/accept запрошення власника (§27.8): 10/хв на IP, окремий лічильник від логіну.</summary>
    public const string InviteRateLimit = "auth-invite";
}

public static class AuthSetup
{
    public static AuthOptions BindAuthOptions(IConfiguration config)
    {
        var o = new AuthOptions();
        config.GetSection("Auth").Bind(o);
        return o;
    }

    /// <summary>JwtBearer + політики за ролями + fallback "автентифікація за замовчуванням" + rate limit на auth-ендпоінти.</summary>
    public static IServiceCollection AddBeautyAuthApi(this IServiceCollection services, IConfiguration config)
    {
        var jwt = JwtSettings.From(config);
        services.AddSingleton(jwt);
        services.AddBeautyAuthApplication(BindAuthOptions(config));
        services.AddSingleton<ITokenIssuer, JwtTokenIssuer>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(o =>
            {
                o.MapInboundClaims = false; // лишаємо sub / role / tenant_id як є
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwt.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(jwt.SigningKey),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = "sub",
                    RoleClaimType = JwtTokenIssuer.RoleClaim,
                };
            })
            .AddScheme<AuthenticationSchemeOptions, PlatformKeyHandler>(PlatformKeyHandler.Scheme, _ => { })
            .AddScheme<AuthenticationSchemeOptions, ControlPlaneHmacHandler>(ControlPlaneHmacHandler.Scheme, _ => { }); // ADR-018, /api/control
        services.TryAddSingleton(TimeProvider.System);
        services.AddControlPlaneHmac();

        services.AddAuthorization(o =>
        {
            // Публічний ендпоінт — явний [AllowAnonymous]; усе інше вимагає токен.
            o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
            o.AddPolicy(AuthPolicies.Staff, p => p.RequireRole(Roles.Owner, Roles.Admin, Roles.Specialist));
            o.AddPolicy(AuthPolicies.Owner, p => p.RequireRole(Roles.Owner));
            o.AddPolicy(AuthPolicies.Management, p => p.RequireRole(Roles.Owner, Roles.Admin));
            o.AddPolicy(AuthPolicies.PlatformOperator, p => p
                .AddAuthenticationSchemes(PlatformKeyHandler.Scheme)
                .RequireRole(Roles.PlatformOperator));
            // /api/control/*: лише підпис ControlPlaneHmac; X-Platform-Key і JWT цю політику не задовольняють.
            o.AddPolicy(AuthPolicies.ControlPlane, p => p
                .AddAuthenticationSchemes(ControlPlaneHmacHandler.Scheme)
                .RequireClaim(ControlPlaneHmacHandler.KidClaim));
        });

        var permit = config.GetValue("Auth:RateLimit:PermitLimit", 10);
        var refreshPermit = config.GetValue("Auth:RateLimit:RefreshPermitLimit", 120);
        var invitePermit = config.GetValue("Auth:RateLimit:InvitePermitLimit", 10);
        var window = TimeSpan.FromSeconds(config.GetValue("Auth:RateLimit:WindowSeconds", 60));
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = async (ctx, ct) =>
            {
                ctx.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await ctx.HttpContext.Response.WriteAsJsonAsync(new ApiError("rate_limited", "Too many requests. Try again later."), ct);
            };
            // Ключ — IP клієнта: Connection.RemoteIpAddress уже перезаписано UseForwardedHeaders лише для довірених проксі
            // (Beauty:TrustedProxies); спуфлений X-Forwarded-For від недовіреного клієнта ігнорується.
            o.AddPolicy(AuthPolicies.RateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
                "login:" + (http.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = permit, Window = window, QueueLimit = 0 }));
            o.AddPolicy(AuthPolicies.RefreshRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
                "refresh:" + (http.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = refreshPermit, Window = window, QueueLimit = 0 }));
            o.AddPolicy(AuthPolicies.InviteRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
                "invite:" + (http.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = invitePermit, Window = window, QueueLimit = 0 }));
        });
        services.AddSingleton(new OwnerInviteThrottle(config.GetValue("Auth:RateLimit:InvitePerTokenPermit", 5), window));
        services.AddSingleton(new RefreshThrottle(
            config.GetValue("Auth:RateLimit:RefreshPerTokenPermit", 5),
            config.GetValue("Auth:RateLimit:RefreshPerTenantPermit", 600), window));
        services.AddSingleton(new AbsenceThrottle(
            config.GetValue("Staff:AbsenceRateLimit:PermitLimit", 20),
            TimeSpan.FromSeconds(config.GetValue("Staff:AbsenceRateLimit:WindowSeconds", 60))));
        services.AddSingleton(new ServiceCostThrottle(config.GetValue("ServiceCosts:WritePerUserPerMinute", ServiceCostThrottle.Default)));
        services.AddSingleton(config.GetSection("Staff").Get<StaffOptions>() ?? new StaffOptions());
        services.AddScoped<ActiveSpecialistFilter>();
        return services;
    }
}

public static class ActorExtensions
{
    /// <summary>Користувач з claims JWT; null, якщо claims неповні (трактувати як 401).</summary>
    public static Actor? ToActor(this ClaimsPrincipal user)
    {
        if (!Guid.TryParse(user.FindFirst("sub")?.Value, out var userId)) return null;
        if (!Guid.TryParse(user.FindFirst(JwtTokenIssuer.TenantClaim)?.Value, out var tenantId)) return null;
        var role = user.FindFirst(JwtTokenIssuer.RoleClaim)?.Value;
        if (!Roles.IsTenantRole(role)) return null;
        Guid? specialistId = Guid.TryParse(user.FindFirst(JwtTokenIssuer.SpecialistClaim)?.Value, out var s) ? s : null;
        return new Actor(userId, tenantId, role!, specialistId);
    }
}
