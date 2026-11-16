using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace BeautyCrm.Infrastructure.Data.Auth;

/// <summary>Спільні записи аудиту доступу (append-only таблиця user_access_audit; TenantId проставляє DbContext).</summary>
internal static class AccessAuditWriter
{
    public static void Add(BeautyDbContext db, string action, Guid? actorUserId, Guid? targetUserId, Guid? inviteId, string changesJson) =>
        db.UserAccessAudit.Add(new UserAccessAuditRow
        {
            Id = Guid.NewGuid(), ActorUserId = actorUserId, TargetUserId = targetUserId, InviteId = inviteId, Action = action, Changes = changesJson,
        });

    /// <summary>
    /// Відкликає pending-запрошення, де <paramref name="inviterId"/> є запрошувачем (M5), і пише <c>invite_revoked</c> на кожне.
    /// Виконується у вже відкритій транзакції викликача; повертає id відкликаних.
    /// </summary>
    public static async Task<List<Guid>> RevokeInvitesOfInviterAsync(
        BeautyDbContext db, Guid inviterId, Guid? actorUserId, string cause, DateTimeOffset now, CancellationToken ct)
    {
        var ids = await db.Invites.Where(i => i.InvitedByUserId == inviterId && i.AcceptedAt == null && i.RevokedAt == null)
            .Select(i => i.Id).ToListAsync(ct);
        if (ids.Count == 0) return ids;
        await db.Invites.Where(i => ids.Contains(i.Id) && i.AcceptedAt == null && i.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.RevokedAt, now.ToUniversalTime()), ct);
        foreach (var id in ids)
            Add(db, "invite_revoked", actorUserId, null, id, System.Text.Json.JsonSerializer.Serialize(new { cause }));
        return ids;
    }
}

/// <summary>
/// <see cref="IAccessAdminStore"/> над EF (ADR-023 §2.6, §30.10). Сесія = одна транзакція: FOR SHARE актора, FOR UPDATE цілі
/// (блокування беруться окремим SELECT ... FOR ..., далі стан читається звичайним запитом: READ COMMITTED бачить найсвіжіші дані
/// після блокування). Усе під RLS поточного tenant.
/// </summary>
public sealed class EfAccessAdminStore(BeautyDbContext db, TenantContext tenant) : IAccessAdminStore
{
    public async Task<IAccessAdminSession> BeginAsync(CancellationToken ct)
    {
        if (tenant.TenantId is null) throw new InvalidOperationException("Tenant is not resolved.");
        return new Session(db, tenant.TenantId.Value, await db.Database.BeginTransactionAsync(ct));
    }

    public async Task<IReadOnlyList<AuditRow>> ListAuditAsync(AuditQuery q, CancellationToken ct)
    {
        // keyset (created_at desc, id desc); tuple-порівняння та імена співробітників через LEFT JOIN під RLS
        var rows = await db.Database.SqlQuery<AuditRaw>($"""
            SELECT a.id AS id, a.created_at AS created_at, a.action AS action,
                   a.actor_user_id AS actor_id, au.full_name AS actor_name,
                   a.target_user_id AS target_id, tu.full_name AS target_name,
                   a.invite_id AS invite_id, a.changes::text AS changes_json
            FROM user_access_audit a
            LEFT JOIN users au ON au.tenant_id = a.tenant_id AND au.id = a.actor_user_id
            LEFT JOIN users tu ON tu.tenant_id = a.tenant_id AND tu.id = a.target_user_id
            WHERE ({q.UserId}::uuid IS NULL OR a.target_user_id = {q.UserId} OR a.actor_user_id = {q.UserId})
              AND ({q.InviteId}::uuid IS NULL OR a.invite_id = {q.InviteId})
              AND ({q.CursorAt}::timestamptz IS NULL OR (a.created_at, a.id) < ({q.CursorAt}::timestamptz, {q.CursorId}::uuid))
            ORDER BY a.created_at DESC, a.id DESC
            LIMIT {q.Take}
            """).ToListAsync(ct);
        return [.. rows.Select(r => new AuditRow(r.Id, r.CreatedAt, r.Action, r.ActorId, r.ActorName, r.TargetId, r.TargetName, r.InviteId, r.ChangesJson))];
    }

    private sealed class AuditRaw
    {
        public Guid Id { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public string Action { get; set; } = null!;
        public Guid? ActorId { get; set; }
        public string? ActorName { get; set; }
        public Guid? TargetId { get; set; }
        public string? TargetName { get; set; }
        public Guid? InviteId { get; set; }
        public string ChangesJson { get; set; } = null!;
    }

    private sealed class Session(BeautyDbContext db, Guid tenantId, IDbContextTransaction tx) : IAccessAdminSession
    {
        private bool _committed;

        public async Task<IReadOnlyList<string>> GetModulesAsync(CancellationToken ct)
        {
            var t = await db.Tenants.AsNoTracking().Where(x => x.Id == tenantId).Select(x => new { x.Modules, x.Status }).FirstOrDefaultAsync(ct);
            return t is { Status: "active" } ? t.Modules : [];
        }

        public async Task<AccessUserRow?> LockActorAsync(Guid id, CancellationToken ct)
        {
            var locked = await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM users WHERE id = {id} FOR SHARE").ToListAsync(ct);
            return locked.Count == 0 ? null : await GetUserAsync(id, ct);
        }

        public async Task<AccessUserRow?> LockTargetAsync(Guid id, CancellationToken ct)
        {
            var locked = await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM users WHERE id = {id} FOR UPDATE").ToListAsync(ct);
            return locked.Count == 0 ? null : await GetUserAsync(id, ct);
        }

        public async Task<AccessUserRow?> GetUserAsync(Guid id, CancellationToken ct)
        {
            var u = await db.Users.AsNoTracking().Where(x => x.Id == id).Select(x => new
            {
                x.Id, x.Email, x.FullName, x.Role, x.IsActive, x.State, x.SpecialistId, x.AccessVersion, x.LastLoginAt,
            }).FirstOrDefaultAsync(ct);
            if (u is null) return null;
            var rows = await db.UserPermissions.AsNoTracking().Where(p => p.UserId == id).Select(p => new { p.Permission, p.Level }).ToListAsync(ct);
            return new AccessUserRow(u.Id, u.Email, u.FullName, u.Role, u.IsActive, u.State, u.SpecialistId, u.AccessVersion, u.LastLoginAt,
                rows.ToDictionary(r => r.Permission, r => r.Level, StringComparer.Ordinal));
        }

        public async Task<int> WriteAccessAsync(Guid targetId, string? newRole, IReadOnlyCollection<KeyValuePair<string, string>> upserts,
            Guid actorId, DateTimeOffset now, CancellationToken ct)
        {
            var at = now.ToUniversalTime();
            foreach (var (key, level) in upserts)
            {
                // upsert без трекінгу: PK (tenant_id, user_id, permission); tenant_id береться з поточного RLS-контексту
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO user_permissions (tenant_id, user_id, permission, level, updated_at, updated_by_user_id)
                    VALUES ({tenantId}, {targetId}, {key}, {level}, {at}, {actorId})
                    ON CONFLICT (tenant_id, user_id, permission)
                    DO UPDATE SET level = EXCLUDED.level, updated_at = EXCLUDED.updated_at, updated_by_user_id = EXCLUDED.updated_by_user_id
                    """, ct);
            }
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE users SET access_version = access_version + 1, updated_at = {at}, role = COALESCE({newRole}, role)
                WHERE id = {targetId}
                """, ct);
            return await db.Users.AsNoTracking().Where(u => u.Id == targetId).Select(u => u.AccessVersion).SingleAsync(ct);
        }

        public async Task<int> SetActiveAsync(Guid targetId, bool active, DateTimeOffset now, CancellationToken ct)
        {
            var at = now.ToUniversalTime();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE users SET is_active = {active}, access_version = access_version + 1, updated_at = {at}
                WHERE id = {targetId}
                """, ct);
            var version = await db.Users.AsNoTracking().Where(u => u.Id == targetId).Select(u => u.AccessVersion).SingleAsync(ct);
            if (!active)
                await db.RefreshTokens.Where(t => t.UserId == targetId && t.RevokedAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, at), ct);
            return version;
        }

        public async Task<IReadOnlyList<Guid>> RevokePendingInvitesByInviterAsync(Guid inviterId, DateTimeOffset now, CancellationToken ct)
        {
            // аудит invite_revoked пише сервіс (actor відомий йому): тут лише відкликання
            var ids = await db.Invites.Where(i => i.InvitedByUserId == inviterId && i.AcceptedAt == null && i.RevokedAt == null)
                .Select(i => i.Id).ToListAsync(ct);
            if (ids.Count > 0)
                await db.Invites.Where(i => ids.Contains(i.Id) && i.AcceptedAt == null && i.RevokedAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(i => i.RevokedAt, now.ToUniversalTime()), ct);
            return ids;
        }

        public async Task AddAuditAsync(AuditEntry e, CancellationToken ct)
        {
            AccessAuditWriter.Add(db, e.Action, e.ActorUserId, e.TargetUserId, e.InviteId, e.ChangesJson);
            await db.SaveChangesAsync(ct);
        }

        public async Task CommitAsync(CancellationToken ct)
        {
            await tx.CommitAsync(ct);
            _committed = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_committed) await tx.RollbackAsync();
            await tx.DisposeAsync();
            db.ChangeTracker.Clear();
        }
    }
}
