using System.Threading.RateLimiting;

namespace BeautyCrm.Api.Infrastructure;

/// <summary>
/// Ліміт PUT/DELETE собівартості на КОРИСТУВАЧА (§24.2 <c>ServiceCosts:WritePerUserPerMinute</c>, 10..600, за замовч. 60).
/// Перевіряється в контролері після автентифікації (патерн <see cref="AbsenceThrottle"/>).
/// </summary>
public sealed class ServiceCostThrottle
{
    public const int Min = 10, Max = 600, Default = 60;
    private readonly PartitionedRateLimiter<string> _perUser;

    public ServiceCostThrottle(int permitPerMinute)
    {
        var permit = Math.Clamp(permitPerMinute, Min, Max);
        _perUser = PartitionedRateLimiter.Create<string, string>(key => RateLimitPartition.GetFixedWindowLimiter(key,
            _ => new FixedWindowRateLimiterOptions { PermitLimit = permit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    }

    /// <summary>false = перевищено.</summary>
    public bool TryAcquire(Guid tenantId, Guid userId)
    {
        using var lease = _perUser.AttemptAcquire($"{tenantId:N}:{userId:N}");
        return lease.IsAcquired;
    }
}
