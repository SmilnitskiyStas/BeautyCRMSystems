using System.Net;
using System.Text.Json;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyControlPlane;
using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace BeautyCrm.Infrastructure.Data.Auth;

/// <summary>
/// Публічні inspect/accept запрошення власника (ADR-018, §27.8). Роль застосунку без BYPASSRLS: <c>platform_owner_invites</c> не
/// має tenant-RLS, тому запрошення знаходиться за sha256 токена; далі <c>app.tenant_id</c> ставиться лише транзакційно
/// (<c>set_config(..., true)</c>) на tenant із запрошення - тим самим механізмом, що й <see cref="EfControlPlaneStore"/>.
/// Токен у цей клас не потрапляє (лише хеш).
/// </summary>
public sealed class EfOwnerInviteStore(BeautyDbContext db) : IOwnerInviteStore
{
    public async Task<(OwnerInviteState State, OwnerInviteRow? Row)> InspectAsync(byte[] tokenHash, DateTimeOffset now, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await SetLocalAsync("app.tenant_id", "", ct);
        var (state, row, _) = await LoadAsync(tokenHash, now, lockTenant: false, ct);
        return (state, row);
    }

    public async Task<(OwnerInviteState State, TokenResponse? Tokens)> AcceptAsync(
        byte[] tokenHash, DateTimeOffset now, Func<IOwnerAcceptSession, Task<TokenResponse?>> onRedeemed, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await SetLocalAsync("app.tenant_id", "", ct);
        var (state, row, _) = await LoadAsync(tokenHash, now, lockTenant: true, ct);
        if (state != OwnerInviteState.Valid) return (state, null);

        // Атомарне погашення: рівно один паралельний виклик отримає рядок. Токен гаситься лише разом із установленням пароля (одна tx).
        var redeemed = await RedeemAsync(tokenHash, now, ct);
        if (redeemed != row!.UserId) return (OwnerInviteState.Invalid, null);

        var tokens = await onRedeemed(new Session(db, row));
        if (tokens is null) return (OwnerInviteState.Invalid, null); // відкат: запрошення лишається непогашеним
        await tx.CommitAsync(ct);
        return (OwnerInviteState.Valid, tokens);
    }

    private Task SetLocalAsync(string name, string value, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config({name}, {value}, true)", ct);

    private async Task<(OwnerInviteState, OwnerInviteRow?, User?)> LoadAsync(byte[] hash, DateTimeOffset now, bool lockTenant, CancellationToken ct)
    {
        var invite = await db.PlatformOwnerInvites.AsNoTracking().Where(i => i.TokenHash == hash).FirstOrDefaultAsync(ct);
        if (invite is null) return (OwnerInviteState.Invalid, null, null);

        await SetLocalAsync("app.tenant_id", invite.TenantId.ToString(), ct);
        // Серіалізація з suspend/resume/reissue того самого tenant (вони теж блокують цей рядок першим).
        if (lockTenant) await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM tenants WHERE id = {invite.TenantId} FOR UPDATE", ct);
        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == invite.TenantId, ct);
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == invite.UserId, ct);

        // Непридатне запрошення -> Invalid без будь-якого розрізнення причини; статус tenant розкривається лише власнику чинного токена.
        var usable = tenant is not null && user is { State: UserStates.Invited, PasswordHash: null, IsActive: true }
            && invite.UsedAt is null && invite.RevokedAt is null && invite.ExpiresAt > now;
        if (!usable) return (OwnerInviteState.Invalid, null, null);
        if (tenant!.Status != "active") return (OwnerInviteState.TenantSuspended, null, null);
        return (OwnerInviteState.Valid, new OwnerInviteRow(invite.Id, invite.TenantId, invite.UserId, tenant.Name, user!.Email, invite.ExpiresAt), user);
    }

    /// <summary>UPDATE ... WHERE token_hash AND used_at IS NULL AND revoked_at IS NULL AND expires_at &gt; now RETURNING user_id.</summary>
    private async Task<Guid?> RedeemAsync(byte[] hash, DateTimeOffset now, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        cmd.CommandText = "UPDATE platform_owner_invites SET used_at = @now WHERE token_hash = @h AND used_at IS NULL AND revoked_at IS NULL " +
                          "AND expires_at > @now RETURNING user_id";
        cmd.Parameters.Add(new NpgsqlParameter("now", now.ToUniversalTime()));
        cmd.Parameters.Add(new NpgsqlParameter("h", hash));
        return await cmd.ExecuteScalarAsync(ct) is Guid g ? g : null;
    }

    private sealed class Session(BeautyDbContext db, OwnerInviteRow invite) : IOwnerAcceptSession
    {
        public OwnerInviteRow Invite { get; } = invite;

        public async Task<UserRecord?> ActivateOwnerAsync(string passwordHash, DateTimeOffset now, CancellationToken ct)
        {
            var n = await db.Users.Where(u => u.Id == Invite.UserId && u.State == UserStates.Invited && u.PasswordHash == null && u.IsActive)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(u => u.PasswordHash, passwordHash)
                    .SetProperty(u => u.State, UserStates.Active)
                    .SetProperty(u => u.FailedLoginCount, 0)
                    .SetProperty(u => u.LockoutUntil, (DateTimeOffset?)null)
                    .SetProperty(u => u.LastLoginAt, now)
                    .SetProperty(u => u.UpdatedAt, now), ct);
            if (n != 1) return null;
            var u = await db.Users.AsNoTracking().FirstAsync(x => x.Id == Invite.UserId, ct);
            return new UserRecord(u.Id, u.TenantId, u.Email, u.FullName, u.PasswordHash, u.Role, u.SpecialistId, u.IsActive,
                u.FailedLoginCount, u.LockoutUntil, u.LastLoginAt, u.State);
        }

        public async Task AddRefreshTokenAsync(Guid userId, string tokenHash, DateTimeOffset expiresAt, CancellationToken ct)
        {
            db.RefreshTokens.Add(new RefreshToken { TenantId = Invite.TenantId, UserId = userId, TokenHash = tokenHash, ExpiresAt = expiresAt });
            await db.SaveChangesAsync(ct);
        }

        public async Task AddAuditAsync(CpAuditEntry e, CancellationToken ct)
        {
            db.PlatformAuditLog.Add(new PlatformAuditRow
            {
                Action = e.Action, TenantId = Invite.TenantId, ActorType = e.ActorType, ActorId = e.ActorId, ActorDisplay = e.ActorDisplay,
                KeyId = e.KeyId, RequestId = e.RequestId, Ip = IPAddress.TryParse(e.Ip, out var ip) ? ip : null,
                Details = JsonSerializer.Serialize(e.Details),
            });
            await db.SaveChangesAsync(ct);
        }
    }
}
