using System.Globalization;
using System.Threading.RateLimiting;
using BeautyCrm.Api.PublicBooking;
using BeautyCrm.Application.Features.BeautyProductImages;
using BeautyCrm.Infrastructure.Media;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Net.Http.Headers;

namespace BeautyCrm.Api.Infrastructure;

/// <summary>Ліміт upload зображень на користувача (<c>Media:UploadsPerUserPerMinute</c>, 1..120): перевіряється в контролері після автентифікації.</summary>
public sealed class UploadThrottle(int permitPerMinute)
{
    private readonly PartitionedRateLimiter<string> _perUser = PartitionedRateLimiter.Create<string, string>(key => RateLimitPartition.GetFixedWindowLimiter(key,
        _ => new FixedWindowRateLimiterOptions { PermitLimit = permitPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));

    public bool TryAcquire(Guid tenantId, Guid userId)
    {
        using var lease = _perUser.AttemptAcquire($"{tenantId:N}:{userId:N}");
        return lease.IsAcquired;
    }
}

/// <summary>
/// Без цього MVC читає multipart-тіло в FormValueProviderFactory ще до дії (буферизація, ліміти форм, відповідь до перевірок прав/товару).
/// Upload читає тіло сам, потоково, після перевірок (§33.22.5).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class DisableFormValueModelBindingAttribute : Attribute, Microsoft.AspNetCore.Mvc.Filters.IResourceFilter
{
    public void OnResourceExecuting(Microsoft.AspNetCore.Mvc.Filters.ResourceExecutingContext context)
    {
        var factories = context.ValueProviderFactories;
        for (var i = factories.Count - 1; i >= 0; i--)
            if (factories[i].GetType().Name.Contains("Form", StringComparison.Ordinal)) // FormValue-, JQueryFormValue-, FormFileValueProviderFactory
                factories.RemoveAt(i);
    }

    public void OnResourceExecuted(Microsoft.AspNetCore.Mvc.Filters.ResourceExecutedContext context) { }
}

/// <summary>Зображення товарів (ADR-027, §33.22): конфіг Media:*, порти, політика rate limit public-media, публічна роздача файлів.</summary>
public static class MediaSetup
{
    public const string PublicMediaPolicy = "public-media";

    /// <summary>Невалідне значення = помилка старту. Без <c>Media:RootPath</c> функцію вимкнено.</summary>
    public static IServiceCollection AddBeautyMediaApi(this IServiceCollection services, IConfiguration config, IHostEnvironment env)
    {
        var o = new MediaOptions
        {
            MaxUploadBytes = Range(config, "MaxUploadBytes", 10 * 1024 * 1024, 1024 * 1024, 20 * 1024 * 1024),
            MaxImagesPerProduct = Range(config, "MaxImagesPerProduct", 8, 1, 20),
            MaxImagesPerTenant = Range(config, "MaxImagesPerTenant", 5000, 1, 100_000),
            MaxPixels = Range(config, "MaxPixels", 25_000_000, 1_000_000, 50_000_000),
            MaxConcurrentProcessing = Range(config, "MaxConcurrentProcessing", 1, 1, 4),
            UploadsPerUserPerMinute = Range(config, "UploadsPerUserPerMinute", 20, 1, 120),
            SweepMode = ParseSweep(config["Media:SweepMode"]),
        };
        var root = config["Media:RootPath"];
        if (!string.IsNullOrWhiteSpace(root))
        {
            o.RootPath = root.Trim();
            o.PublicBaseUrl = ValidateBaseUrl(config["Media:PublicBaseUrl"], env);
            Probe(o.RootPath);
        }

        services.Replace(ServiceDescriptor.Singleton(o));
        services.AddSingleton<FileProductImageStorage>();
        services.AddSingleton<IProductImageStorage>(sp => sp.GetRequiredService<FileProductImageStorage>());
        services.AddSingleton<IImageProcessor, ImageSharpProcessor>();
        services.AddScoped<ProductImageService>(); // порти сховища/обробки реєструються тут, тож і сервіс тут
        services.AddSingleton(new UploadThrottle(o.UploadsPerUserPerMinute));
        if (o.Enabled && o.SweepMode != MediaSweepMode.Off) services.AddHostedService<MediaOrphanSweeper>();

        var permit = config.GetValue("Media:PublicPermitPerMinute", MediaOptions.PublicPermitPerMinute);
        services.AddRateLimiter(rl => rl.AddPolicy(PublicMediaPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
            PublicRateLimit.KeyFor(http.Connection.RemoteIpAddress),
            _ => new FixedWindowRateLimiterOptions { PermitLimit = permit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })));
        return services;
    }

    private static int Range(IConfiguration config, string key, int def, int min, int max)
    {
        var raw = config[$"Media:{key}"];
        if (string.IsNullOrWhiteSpace(raw)) return def;
        return int.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v >= min && v <= max
            ? v
            : throw new InvalidOperationException($"Media:{key} must be an integer in {min}..{max}.");
    }

    private static MediaSweepMode ParseSweep(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        null or "" or "off" => MediaSweepMode.Off,
        "dry-run" => MediaSweepMode.DryRun,
        "delete" => MediaSweepMode.Delete,
        _ => throw new InvalidOperationException("Media:SweepMode must be one of: off, dry-run, delete."),
    };

    /// <summary>Origin без шляху й завершального «/»; поза Development лише https.</summary>
    private static string ValidateBaseUrl(string? raw, IHostEnvironment env)
    {
        const string rule = "Media:PublicBaseUrl must be an origin (scheme://host[:port]) without path, query or trailing slash; https is required outside Development.";
        var s = raw?.Trim();
        if (string.IsNullOrEmpty(s) || s.EndsWith('/') || !Uri.TryCreate(s, UriKind.Absolute, out var u)
            || u.AbsolutePath != "/" || u.Query.Length > 0 || u.Fragment.Length > 0 || u.UserInfo.Length > 0
            || !(u.Scheme == Uri.UriSchemeHttps || (u.Scheme == Uri.UriSchemeHttp && env.IsDevelopment())))
            throw new InvalidOperationException(rule);
        return s;
    }

    /// <summary>Тека має існувати й бути доступною на запис (том не змонтовано / без прав = помилка старту, а не 500 на upload).</summary>
    private static void Probe(string root)
    {
        if (!Path.IsPathRooted(root)) throw new InvalidOperationException("Media:RootPath must be an absolute path.");
        if (!Directory.Exists(root)) throw new InvalidOperationException("Media:RootPath does not exist.");
        var probe = Path.Combine(root, $".probe-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(probe, [0]);
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("Media:RootPath is not writable.", ex);
        }
    }

    /// <summary>GET|HEAD /api/public/media/p/{file}: без БД, автентифікації, slug і 426; усе, чого немає або що невалідне, - однакова 404 no-store.</summary>
    public static IEndpointRouteBuilder MapBeautyPublicMedia(this IEndpointRouteBuilder app)
    {
        app.MapMethods("/api/public/media/p/{**file}", ["GET", "HEAD"], (HttpContext http, string? file, FileProductImageStorage storage) =>
        {
            var h = http.Response.Headers;
            if (storage.ResolveVariant(file, out var etag) is not { } path)
            {
                h.CacheControl = "no-store";
                return Results.StatusCode(StatusCodes.Status404NotFound);
            }
            h.CacheControl = "public, max-age=31536000, immutable";
            h.XContentTypeOptions = "nosniff";
            h.ContentSecurityPolicy = "default-src 'none'; sandbox";
            h["Cross-Origin-Resource-Policy"] = "cross-origin";
            h.ContentDisposition = "inline";
            return Results.File(path, "image/webp", enableRangeProcessing: true, entityTag: EntityTagHeaderValue.Parse(etag!));
        }).AllowAnonymous().RequireRateLimiting(PublicMediaPolicy).ExcludeFromDescription();
        return app;
    }
}
