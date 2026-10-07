using System.Security.Claims;
using System.Threading.RateLimiting;
using BeautyCrm.Api.Infrastructure;
using BeautyCrm.Application.Features.BeautyAuth;
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
    public const string RateLimit = "auth";
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
            .AddScheme<AuthenticationSchemeOptions, PlatformKeyHandler>(PlatformKeyHandler.Scheme, _ => { });

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
        });

        var permit = config.GetValue("Auth:RateLimit:PermitLimit", 10);
        var window = TimeSpan.FromSeconds(config.GetValue("Auth:RateLimit:WindowSeconds", 60));
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = async (ctx, ct) =>
            {
                ctx.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await ctx.HttpContext.Response.WriteAsJsonAsync(new ApiError("rate_limited", "Too many requests. Try again later."), ct);
            };
            // Ключ — IP клієнта (за reverse proxy потрібен ForwardedHeaders із довіреними проксі).
            o.AddPolicy(AuthPolicies.RateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = permit, Window = window, QueueLimit = 0 }));
        });
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
