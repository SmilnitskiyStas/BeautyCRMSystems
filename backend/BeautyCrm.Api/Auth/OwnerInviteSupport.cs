using System.Threading.RateLimiting;

namespace BeautyCrm.Api.Auth;

/// <summary>Ліміт спроб на один токен запрошення (за sha256-хешем, не за токеном): брутфорс/перебір одного посилання.</summary>
public sealed class OwnerInviteThrottle
{
    private readonly PartitionedRateLimiter<string> _perToken;

    public OwnerInviteThrottle(int perTokenPermit, TimeSpan window) =>
        _perToken = PartitionedRateLimiter.Create<string, string>(key => RateLimitPartition.GetFixedWindowLimiter(key,
            _ => new FixedWindowRateLimiterOptions { PermitLimit = perTokenPermit, Window = window, QueueLimit = 0 }));

    /// <summary>false = перевищено.</summary>
    public bool TryAcquire(byte[] tokenHash)
    {
        using var lease = _perToken.AttemptAcquire(Convert.ToHexString(tokenHash));
        return lease.IsAcquired;
    }
}

public static class OwnerInviteSupport
{
    /// <summary>Cache-Control: no-store на /api/auth/invite/* - також для 4xx/429, що формує rate limiter.</summary>
    public static IApplicationBuilder UseOwnerInviteNoStore(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        if (ctx.Request.Path.StartsWithSegments("/api/auth/invite"))
            ctx.Response.Headers.CacheControl = "no-store";
        await next();
    });
}
