using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace BeautyCrm.Infrastructure.Data.Auth;

/// <summary>
/// ADR-023 §2.5, §30.5 п.4: доступ користувача одним запитом під RLS (роль, активність, стан, прив'язка, <c>access_version</c>,
/// <c>specialists.is_active</c> через LEFT JOIN, рядки <c>user_permissions</c>) + модулі tenant. Невалідні рядки прав (формат, ключ,
/// рівень) відсіюються під час побудови <see cref="AccessSet"/>, а не тут. Призупинений tenant = жодного модуля.
/// </summary>
public sealed class EfAccessStore(BeautyDbContext db, TenantContext tenant) : IAccessStore
{
    public async Task<AccessSnapshot?> LoadAsync(Guid userId, CancellationToken ct)
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Tenant is not resolved.");

        var user = await db.Users.AsNoTracking()
            .Where(u => u.TenantId == tenantId && u.Id == userId)
            .Select(u => new
            {
                u.Role,
                u.IsActive,
                u.State,
                u.SpecialistId,
                u.AccessVersion,
                SpecialistActive = u.Specialist == null ? (bool?)null : u.Specialist.IsActive,
                Permissions = db.UserPermissions
                    .Where(p => p.TenantId == tenantId && p.UserId == u.Id)
                    .Select(p => new { p.Permission, p.Level })
                    .ToList(),
            })
            .FirstOrDefaultAsync(ct);
        if (user is null) return null;

        var t = await db.Tenants.AsNoTracking()
            .Where(x => x.Id == tenantId)
            .Select(x => new { x.Modules, x.Status })
            .FirstOrDefaultAsync(ct);
        var modules = t is { Status: "active" } ? t.Modules : [];

        var permissions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in user.Permissions) permissions[p.Permission] = p.Level;

        // SpecialistId задано, а профіль не видно (SpecialistActive = null попри FK) = неактивний (fail-closed).
        var specialistActive = user.SpecialistId is null ? (bool?)null : user.SpecialistActive ?? false;
        return new AccessSnapshot(user.Role, user.IsActive, user.State, user.SpecialistId, user.AccessVersion,
            specialistActive, permissions, modules);
    }
}
