using System.Net;
using System.Text.Json;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyControlPlane;
using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BeautyCrm.Infrastructure.Data.Auth;

/// <summary>
/// Сховище control plane ShelfGuard (ADR-018). Роль застосунку без BYPASSRLS: tenant знаходиться за external_ref через вузьку
/// SELECT-політику <c>tenants_control_lookup</c> (<c>set_config('app.tenant_external_ref', ..., true)</c>), далі
/// <c>app.tenant_id</c> ставиться ТЕЖ лише транзакційно (<c>set_config(..., true)</c>) - після commit/rollback значення зникає.
/// Усі зміни й рядки platform_audit_log - в одній транзакції; рядок tenant блокується (FOR UPDATE), тому операції над одним
/// tenant серіалізуються (noop/before-after коректні при паралельних запитах).
/// </summary>
public sealed class EfControlPlaneStore(BeautyDbContext db) : IControlPlaneStore
{
    private const string ExternalSource = "shelfguard";

    public async Task<(bool Found, T? Value)> WithTenantAsync<T>(string externalRef, Func<ICpSession, Task<T>> work, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await SetLocalAsync("app.tenant_id", "", ct);
        await SetLocalAsync("app.tenant_external_ref", externalRef, ct);
        var id = await db.Tenants.AsNoTracking().Where(x => x.ExternalSource == ExternalSource && x.ExternalRef == externalRef)
            .Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
        if (id is null) return (false, default);

        await SetLocalAsync("app.tenant_id", id.Value.ToString(), ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM tenants WHERE id = {id.Value} FOR UPDATE", ct);
        var t = await db.Tenants.AsNoTracking().FirstAsync(x => x.Id == id.Value, ct);

        var value = await work(new Session(db, Map(t)));
        await tx.CommitAsync(ct);
        return (true, value);
    }

    public async Task<bool> TryCreateAsync(CpNewTenant t, CpNewOwner o, Func<ICpSession, Task> inSameTransaction, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await SetLocalAsync("app.tenant_id", t.Id.ToString(), ct); // RLS WITH CHECK (id = app.tenant_id) дозволяє вставку лише цього tenant
        db.Tenants.Add(new Tenant
        {
            Id = t.Id, Name = t.Name, Slug = t.Slug, Modules = t.Modules.ToArray(), Status = "active",
            ExternalRef = t.ExternalRef, ExternalSource = ExternalSource, CreatedAt = t.Now, UpdatedAt = t.Now,
        });
        db.Users.Add(new User
        {
            Id = Guid.NewGuid(), TenantId = t.Id, Email = o.Email, FullName = o.FullName, Role = Roles.Owner,
            PasswordHash = null, State = UserStates.Invited,
        });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when ((ex.InnerException as PostgresException)?.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            db.ChangeTracker.Clear();
            return false; // tx відкотиться при Dispose
        }

        await inSameTransaction(new Session(db, new CpTenant(t.Id, t.ExternalRef, t.Slug, t.Name, "active", t.Modules, null, t.Now)));
        await tx.CommitAsync(ct);
        return true;
    }

    private Task SetLocalAsync(string name, string value, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config({name}, {value}, true)", ct);

    private static CpTenant Map(Tenant t) =>
        new(t.Id, t.ExternalRef!, t.Slug, t.Name, t.Status, t.Modules, t.SuspendedAt, t.CreatedAt);

    private sealed class Session(BeautyDbContext db, CpTenant tenant) : ICpSession
    {
        public CpTenant Tenant { get; } = tenant;

        public async Task<CpOwner?> GetOwnerAsync(CancellationToken ct) =>
            await db.Users.AsNoTracking().Where(u => u.TenantId == Tenant.Id && u.Role == Roles.Owner)
                .OrderBy(u => u.CreatedAt).Select(u => new CpOwner(u.Id, u.Email, u.State)).FirstOrDefaultAsync(ct);

        public async Task<DateTimeOffset?> GetActiveInviteExpiryAsync(Guid userId, DateTimeOffset now, CancellationToken ct) =>
            await db.PlatformOwnerInvites.AsNoTracking()
                .Where(i => i.TenantId == Tenant.Id && i.UserId == userId && i.UsedAt == null && i.RevokedAt == null && i.ExpiresAt > now)
                .Select(i => (DateTimeOffset?)i.ExpiresAt).OrderByDescending(e => e).FirstOrDefaultAsync(ct);

        public Task<bool> SuspendAsync(string reason, DateTimeOffset now, CancellationToken ct) =>
            TenantMutations.SuspendAsync(db, Tenant.Id, reason, now, ct);

        public Task<bool> ResumeAsync(DateTimeOffset now, CancellationToken ct) =>
            TenantMutations.ResumeAsync(db, Tenant.Id, now, ct);

        public Task<bool> SetModulesAsync(IReadOnlyList<string> modules, DateTimeOffset now, CancellationToken ct) =>
            TenantMutations.SetModulesAsync(db, Tenant.Id, modules, now, ct);

        public async Task<IReadOnlyList<Guid>> RevokeActiveInvitesAsync(Guid userId, DateTimeOffset now, CancellationToken ct)
        {
            var active = db.PlatformOwnerInvites.Where(i => i.TenantId == Tenant.Id && i.UserId == userId && i.UsedAt == null && i.RevokedAt == null);
            var ids = await active.Select(i => i.Id).ToListAsync(ct);
            if (ids.Count > 0) await active.ExecuteUpdateAsync(s => s.SetProperty(i => i.RevokedAt, now), ct);
            return ids;
        }

        public async Task<Guid> AddInviteAsync(Guid userId, byte[] tokenHash, DateTimeOffset expiresAt, string createdBy, DateTimeOffset now, CancellationToken ct)
        {
            var invite = new PlatformOwnerInvite
            {
                Id = Guid.NewGuid(), TenantId = Tenant.Id, UserId = userId, TokenHash = tokenHash, ExpiresAt = expiresAt,
                CreatedAt = now, CreatedByActor = createdBy,
            };
            db.PlatformOwnerInvites.Add(invite);
            await db.SaveChangesAsync(ct);
            return invite.Id;
        }

        public async Task AddAuditAsync(CpAuditEntry e, CancellationToken ct)
        {
            db.PlatformAuditLog.Add(new PlatformAuditRow
            {
                Action = e.Action, TenantId = Tenant.Id, ActorType = e.ActorType, ActorId = e.ActorId, ActorDisplay = e.ActorDisplay,
                KeyId = e.KeyId, RequestId = e.RequestId, Ip = IPAddress.TryParse(e.Ip, out var ip) ? ip : null,
                Details = JsonSerializer.Serialize(e.Details),
            });
            await db.SaveChangesAsync(ct);
        }
    }
}
