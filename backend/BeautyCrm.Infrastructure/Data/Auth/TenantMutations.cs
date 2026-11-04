using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace BeautyCrm.Infrastructure.Data.Auth;

/// <summary>
/// Зміни стану tenant, спільні для оператора платформи (PATCH /api/platform/tenants/{id}) і control plane ShelfGuard
/// (ADR-018: одна доменна операція, без копії логіки). Викликати в межах транзакції з уже встановленим app.tenant_id.
/// </summary>
internal static class TenantMutations
{
    /// <summary>active -> suspended: suspended_at/reason, усі refresh-токени tenant відкликано. false = уже призупинено (нічого не змінено).</summary>
    public static async Task<bool> SuspendAsync(BeautyDbContext db, Guid tenantId, string? reason, DateTimeOffset now, CancellationToken ct)
    {
        var n = await db.Tenants.Where(x => x.Id == tenantId && x.Status != "suspended").ExecuteUpdateAsync(s => s
            .SetProperty(x => x.Status, "suspended").SetProperty(x => x.SuspendedAt, now)
            .SetProperty(x => x.SuspendedReason, reason).SetProperty(x => x.UpdatedAt, now), ct);
        if (n != 1) return false;
        await db.RefreshTokens.Where(t => t.TenantId == tenantId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
        return true;
    }

    /// <summary>suspended -> active: suspended_at і suspended_reason очищаються (CHECK ck_tenants_suspended). Сесії не відновлюються.</summary>
    public static async Task<bool> ResumeAsync(BeautyDbContext db, Guid tenantId, DateTimeOffset now, CancellationToken ct) =>
        await db.Tenants.Where(x => x.Id == tenantId && x.Status == "suspended").ExecuteUpdateAsync(s => s
            .SetProperty(x => x.Status, "active").SetProperty(x => x.SuspendedAt, (DateTimeOffset?)null)
            .SetProperty(x => x.SuspendedReason, (string?)null).SetProperty(x => x.UpdatedAt, now), ct) == 1;

    /// <summary>Повний набір модулів. false = набір (як множина) не змінився, запис не виконується.</summary>
    public static async Task<bool> SetModulesAsync(BeautyDbContext db, Guid tenantId, IReadOnlyList<string> modules, DateTimeOffset now, CancellationToken ct)
    {
        var current = await db.Tenants.AsNoTracking().Where(x => x.Id == tenantId).Select(x => x.Modules).FirstOrDefaultAsync(ct);
        if (current is null || current.ToHashSet(StringComparer.Ordinal).SetEquals(modules)) return false;
        var array = modules.ToArray();
        await db.Tenants.Where(x => x.Id == tenantId).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.Modules, array).SetProperty(x => x.UpdatedAt, now), ct);
        return true;
    }
}
