using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using BeautyCrm.Api.Auth;
using BeautyCrm.Api.Infrastructure;
using BeautyCrm.Application.Features.BeautyChatBooking;
using BeautyCrm.Application.Features.BeautyPublicBooking;
using BeautyCrm.Infrastructure.Data.Beauty;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BeautyCrm.Api.PublicBooking;

public static class PublicRateLimit
{
    /// <summary>Каталог і слоти (читання).</summary>
    public const string Read = "public-read";
    /// <summary>Перегляд запису за токеном (суворіше: захист від підбору).</summary>
    public const string Token = "public-token";
    /// <summary>POST створення/скасування (найсуворіше).</summary>
    public const string Write = "public-write";
    /// <summary>POST /locations/nearby (ADR-022): координати, захист від сканування сіткою.</summary>
    public const string Nearby = "public-nearby";
    /// <summary>POST /locations/search, тіло {city} (ADR-022).</summary>
    public const string Search = "public-search";

    /// <summary>
    /// Ключ ліміту: IPv4 як є; IPv6 — префікс /64 (один абонент отримує цілу /64, тож ротація адрес всередині неї не обходить ліміт);
    /// IPv4-mapped IPv6 зводиться до IPv4. Без довірених проксі (Beauty:TrustedProxies) це адреса проксі.
    /// </summary>
    public static string KeyFor(System.Net.IPAddress? ip)
    {
        if (ip is null) return "unknown";
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return ip.ToString();
        var bytes = ip.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new System.Net.IPAddress(bytes) + "/64";
    }
}

/// <summary>
/// ADR-022 §6: самодекларовані мітки клієнта. НЕ автентифікація (будь-що з бінарника витягується): використовуються для метрик,
/// вибору політики захисту й kill switch застарілих версій. Заголовки: X-Booking-Client: web|ios|android; X-App-Version: 1.2.3.
/// </summary>
public static class PublicClientInfo
{
    public const string ClientHeader = "X-Booking-Client";
    public const string VersionHeader = "X-App-Version";
    public const string IntegrityHeader = "X-Client-Integrity";
    private const string ItemKey = "beauty.public-client";

    /// <summary>Відсутня/невідома мітка = web (найсуворіша політика з доступних для неоголошеного клієнта).</summary>
    public static ClientKind KindOf(HttpContext http) =>
        http.Items[ItemKey] is ClientKind k ? k : Parse(http.Request.Headers[ClientHeader].FirstOrDefault());

    public static ClientKind Parse(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "ios" => ClientKind.Ios,
        "android" => ClientKind.Android,
        _ => ClientKind.Web,
    };

    /// <summary>Лише ведучі цифри й крапки (до 4 компонентів); суфікси (-beta, +build) відкидаються. Невалідне = null.</summary>
    public static Version? ParseVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 32) return null;
        var core = raw.Trim().Split('-', '+')[0];
        var parts = core.Split('.');
        if (parts.Length is < 1 or > 4 || parts.Any(p => p.Length is 0 or > 5 || !p.All(char.IsAsciiDigit))) return null;
        while (parts.Length < 2) parts = [.. parts, "0"];
        return Version.TryParse(string.Join('.', parts), out var v) ? v : null;
    }

    internal static void Remember(HttpContext http, ClientKind kind) => http.Items[ItemKey] = kind;
}

/// <summary>
/// Мітка клієнта + мінімальна версія застосунку: ios/android із відомою версією, меншою за PublicBooking:MinAppVersion:{Ios|Android},
/// отримують 426 `upgrade_required` (на будь-якому публічному маршруті). Відсутня версія не блокується (це мітка, а не автентифікація).
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class PublicClientAttribute : Attribute, IAsyncActionFilter, IOrderedFilter
{
    public int Order => -100; // до PublicTenant: застарілий клієнт не змушує ходити в БД

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var http = context.HttpContext;
        var kind = PublicClientInfo.Parse(http.Request.Headers[PublicClientInfo.ClientHeader].FirstOrDefault());
        PublicClientInfo.Remember(http, kind);
        var min = http.RequestServices.GetRequiredService<PublicBookingOptions>().MinVersionFor(kind);
        if (min is not null
            && PublicClientInfo.ParseVersion(http.Request.Headers[PublicClientInfo.VersionHeader].FirstOrDefault()) is { } v && v < min)
        {
            context.Result = new ObjectResult(new ApiError("upgrade_required", "This app version is no longer supported. Please update the app."))
            { StatusCode = StatusCodes.Status426UpgradeRequired };
            return;
        }
        await next();
    }
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
            // ADR-018 D2 (§27.5): призупинений tenant відрізняється від невідомого (403 tenant_suspended); модуль вимкнений = 404.
            context.Result = resolver.IsSuspended
                ? new ObjectResult(new ApiError("tenant_suspended", "Booking is temporarily unavailable.")) { StatusCode = StatusCodes.Status403Forbidden }
                : new NotFoundObjectResult(new ApiError("not_found", "Not found."));
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
    /// PublicBooking:CaptchaRequired, PublicBooking:MaxActiveBookingsPerPhone, PublicBooking:MaxDaysAhead,
    /// PublicBooking:AllowWeakerClientPaths (дозвіл на різні режими Web/Ios/Android; інакше Production падає, Development попереджає).
    /// ADR-022: RateLimit:{NearbyPermit=10,SearchPermit=20}, MaxPublicCreatesPerLocationPerHour=60 (0 = вимкнено),
    /// Protection:{Web|Ios|Android}=none|monitor|enforce, MinAppVersion:{Ios|Android}=1.2.3.
    /// </summary>
    public static IServiceCollection AddBeautyPublicBookingApi(this IServiceCollection services, IConfiguration config, IHostEnvironment? env = null)
    {
        var options = new PublicBookingOptions
        {
            CaptchaRequired = config.GetValue("PublicBooking:CaptchaRequired", false),
            MaxActiveBookingsPerPhone = config.GetValue("PublicBooking:MaxActiveBookingsPerPhone", 3),
            MaxCreatesPerPhonePerHour = config.GetValue("PublicBooking:MaxCreatesPerPhonePerHour", 5),
            MaxDaysAhead = config.GetValue("PublicBooking:MaxDaysAhead", 180),
            MaxPublicCreatesPerLocationPerHour = config.GetValue("PublicBooking:MaxPublicCreatesPerLocationPerHour", 60),
            ProtectionWeb = ParseMode(config, "Web"),
            ProtectionIos = ParseMode(config, "Ios"),
            ProtectionAndroid = ParseMode(config, "Android"),
            MinAppVersionIos = ParseMinVersion(config, "Ios"),
            MinAppVersionAndroid = ParseMinVersion(config, "Android"),
            TokenKey = ResolveTokenKey(config),
        };
        services.AddBeautyPublicBookingApplication(options).AddBeautyPublicBookingData();
        // Запис з чату (§21.6): спільне ядро OnlineBookingCore; споживач - конвеєр розмов (TASK-728).
        services.AddBeautyChatBookingApplication(new ChatBookingOptions { ProposalTtlMinutes = config.GetValue("ChatAssistant:ProposalTtlMinutes", 15) })
            .AddBeautyChatBookingData();

        // M3: найслабший прийнятий шлях визначає захист усього API (ADR-022 §6) — на старті це або явно дозволено, або помилка/попередження.
        var allowWeaker = config.GetValue("PublicBooking:AllowWeakerClientPaths", false);
        var weakPaths = DescribeWeakerClientPaths(options);
        if (weakPaths is not null && !allowWeaker)
        {
            if (env is null || env.IsProduction())
                throw new InvalidOperationException(weakPaths + " Align PublicBooking:Protection:* (and CaptchaRequired) or set PublicBooking:AllowWeakerClientPaths=true explicitly.");
            services.AddSingleton<IHostedService>(sp => new WeakClientPathsWarning(sp.GetRequiredService<ILogger<WeakClientPathsWarning>>(), weakPaths));
        }

        var window = TimeSpan.FromSeconds(config.GetValue("PublicBooking:RateLimit:WindowSeconds", 60));
        services.AddRateLimiter(o =>
        {
            void Add(string name, int permit) => o.AddPolicy(name, http => RateLimitPartition.GetFixedWindowLimiter(
                // Ключ — IP клієнта (за reverse proxy потрібен ForwardedHeaders із довіреними проксі).
                PublicRateLimit.KeyFor(http.Connection.RemoteIpAddress),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = permit, Window = window, QueueLimit = 0 }));
            Add(PublicRateLimit.Read, config.GetValue("PublicBooking:RateLimit:ReadPermit", 120));
            Add(PublicRateLimit.Token, config.GetValue("PublicBooking:RateLimit:TokenPermit", 30));
            Add(PublicRateLimit.Write, config.GetValue("PublicBooking:RateLimit:WritePermit", 10));
            Add(PublicRateLimit.Nearby, config.GetValue("PublicBooking:RateLimit:NearbyPermit", 10));
            Add(PublicRateLimit.Search, config.GetValue("PublicBooking:RateLimit:SearchPermit", 20));
        });
        return services;
    }

    /// <summary>
    /// null, якщо ефективні режими Web/Ios/Android однакові. Інакше опис розбіжності (зокрема CaptchaRequired=true без enforce для мобільних:
    /// клієнт з міткою ios/android обходить CAPTCHA).
    /// </summary>
    public static string? DescribeWeakerClientPaths(PublicBookingOptions o)
    {
        var web = o.ProtectionFor(ClientKind.Web);
        if (web == o.ProtectionIos && web == o.ProtectionAndroid) return null;
        var captcha = o.CaptchaRequired && (o.ProtectionIos != ProtectionMode.Enforce || o.ProtectionAndroid != ProtectionMode.Enforce)
            ? " CaptchaRequired=true is not mirrored by enforce for mobile clients (X-Booking-Client is a self-declared label)." : "";
        return $"Public booking protection differs per client path (effective Web={web}, Ios={o.ProtectionIos}, Android={o.ProtectionAndroid}); " +
               $"the API is only as strong as its weakest accepted path.{captcha}";
    }

    private sealed class WeakClientPathsWarning(ILogger<WeakClientPathsWarning> log, string message) : IHostedService
    {
        public Task StartAsync(CancellationToken ct) { log.LogWarning("{Message}", message); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private static ProtectionMode ParseMode(IConfiguration config, string client)
    {
        var raw = config[$"PublicBooking:Protection:{client}"];
        if (string.IsNullOrWhiteSpace(raw)) return ProtectionMode.None;
        return Enum.TryParse<ProtectionMode>(raw.Trim(), ignoreCase: true, out var m) && Enum.IsDefined(m)
            ? m
            : throw new InvalidOperationException($"PublicBooking:Protection:{client} must be none, monitor or enforce.");
    }

    private static Version? ParseMinVersion(IConfiguration config, string client)
    {
        var raw = config[$"PublicBooking:MinAppVersion:{client}"];
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return PublicClientInfo.ParseVersion(raw)
            ?? throw new InvalidOperationException($"PublicBooking:MinAppVersion:{client} must look like 1.2.3.");
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
