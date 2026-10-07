using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using BeautyCrm.Api.Auth;
using BeautyCrm.Api.Infrastructure;
using BeautyCrm.Application.Features.BeautyPublicBooking;
using BeautyCrm.Infrastructure.Data.Beauty;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BeautyCrm.Api.PublicBooking;

public static class PublicRateLimit
{
    /// <summary>Каталог і слоти (читання).</summary>
    public const string Read = "public-read";
    /// <summary>Перегляд запису за токеном (суворіше: захист від підбору).</summary>
    public const string Token = "public-token";
    /// <summary>POST створення/скасування (найсуворіше).</summary>
    public const string Write = "public-write";
}

/// <summary>Визначає tenant за {tenantSlug} до дії контролера; будь-яка невдача = однакова 404 `not_found`.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class PublicTenantAttribute : Attribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var resolver = context.HttpContext.RequestServices.GetRequiredService<PublicTenantResolver>();
        var slug = context.RouteData.Values["tenantSlug"] as string;
        if (!await resolver.ResolveAsync(slug, context.HttpContext.RequestAborted))
        {
            context.Result = new NotFoundObjectResult(new ApiError("not_found", "Not found."));
            return;
        }
        await next();
    }
}

public static class PublicBookingSetup
{
    /// <summary>
    /// Публічний запис: сервіси, порт даних, rate limit на IP. Ключ токенів: PublicBooking:TokenKey (base64, >= 32 байти)
    /// або, якщо не задано, похідний від Auth:JwtSigningKey (HMAC з розділювачем-контекстом).
    /// Конфіг: PublicBooking:RateLimit:{ReadPermit=120,TokenPermit=30,WritePermit=10,WindowSeconds=60},
    /// PublicBooking:CaptchaRequired, PublicBooking:MaxActiveBookingsPerPhone, PublicBooking:MaxDaysAhead.
    /// </summary>
    public static IServiceCollection AddBeautyPublicBookingApi(this IServiceCollection services, IConfiguration config)
    {
        var options = new PublicBookingOptions
        {
            CaptchaRequired = config.GetValue("PublicBooking:CaptchaRequired", false),
            MaxActiveBookingsPerPhone = config.GetValue("PublicBooking:MaxActiveBookingsPerPhone", 3),
            MaxCreatesPerPhonePerHour = config.GetValue("PublicBooking:MaxCreatesPerPhonePerHour", 5),
            MaxDaysAhead = config.GetValue("PublicBooking:MaxDaysAhead", 180),
            TokenKey = ResolveTokenKey(config),
        };
        services.AddBeautyPublicBookingApplication(options).AddBeautyPublicBookingData();

        var window = TimeSpan.FromSeconds(config.GetValue("PublicBooking:RateLimit:WindowSeconds", 60));
        services.AddRateLimiter(o =>
        {
            void Add(string name, int permit) => o.AddPolicy(name, http => RateLimitPartition.GetFixedWindowLimiter(
                // Ключ — IP клієнта (за reverse proxy потрібен ForwardedHeaders із довіреними проксі).
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = permit, Window = window, QueueLimit = 0 }));
            Add(PublicRateLimit.Read, config.GetValue("PublicBooking:RateLimit:ReadPermit", 120));
            Add(PublicRateLimit.Token, config.GetValue("PublicBooking:RateLimit:TokenPermit", 30));
            Add(PublicRateLimit.Write, config.GetValue("PublicBooking:RateLimit:WritePermit", 10));
        });
        return services;
    }

    private static byte[] ResolveTokenKey(IConfiguration config)
    {
        var raw = config["PublicBooking:TokenKey"];
        if (!string.IsNullOrWhiteSpace(raw))
        {
            try
            {
                var key = Convert.FromBase64String(raw);
                if (key.Length >= 32) return key;
            }
            catch (FormatException)
            {
            }
            throw new InvalidOperationException("PublicBooking:TokenKey must be base64 of at least 32 bytes.");
        }
        return HMACSHA256.HashData(JwtSettings.From(config).SigningKey, Encoding.ASCII.GetBytes("beauty-public-booking-token-v1"));
    }
}
