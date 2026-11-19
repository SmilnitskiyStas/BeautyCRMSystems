using BeautyCrm.Application.Features.BeautyAuth;
using Microsoft.EntityFrameworkCore;

namespace BeautyCrm.Infrastructure.Data.Beauty;

/// <summary>
/// Security-review L2 (§32.18 п.2): модуль <c>beauty_inventory</c> поточного tenant для кроку автосписання (поза HTTP-фільтром).
/// Явний фільтр за tenant з <c>app.tenant_id</c> (а не лише RLS), активний tenant, порівняння модуля без урахування регістру
/// (як <c>[RequireModule]</c> API й <c>lower(m)</c> воркера). Tenant не встановлено -> вимкнено (fail-closed).
/// </summary>
internal static class InventoryModuleGate
{
    public static async Task<bool> IsEnabledAsync(BeautyDbContext db, CancellationToken ct)
    {
        var tenantId = await db.Database.SqlQuery<Guid?>($"SELECT NULLIF(current_setting('app.tenant_id', true), '')::uuid AS \"Value\"").SingleAsync(ct);
        if (tenantId is not { } id) return false;
        var modules = await db.Tenants.AsNoTracking().Where(t => t.Id == id && t.Status == "active").Select(t => t.Modules).SingleOrDefaultAsync(ct);
        return modules is not null && modules.Contains(BeautyModules.Inventory, StringComparer.OrdinalIgnoreCase);
    }
}
