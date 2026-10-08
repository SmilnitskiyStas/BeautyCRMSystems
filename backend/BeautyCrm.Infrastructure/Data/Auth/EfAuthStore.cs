using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace BeautyCrm.Infrastructure.Data.Auth;

/// <summary>
/// Реалізація <see cref="IAuthStore"/> над EF. Усі запити, крім пошуку tenant за slug, виконуються під RLS поточного
/// tenant (<see cref="TenantContext"/> -> app.tenant_id). Пошук за slug іде окремим з'єднанням із транзакційним
/// app.tenant_slug, який відкриває лише SELECT-політику tenants_login_lookup (один рядок, без модулів інших tenant).
/// </summary>
public sealed class EfAuthStore(BeautyDbContext db, TenantContext tenant, IConfiguration config) : IAuthStore
{
    private bool _dbTouched;

    private BeautyDbContext Db
    {
        get
        {
            _dbTouched = true;
            return db;
        }
    }

    public void UseTenant(Guid tenantId)
    {
        // app.tenant_id фіксується при відкритті з'єднання: після першого запиту змінювати tenant не можна.
        if (_dbTouched && tenant.TenantId != tenantId)
            throw new InvalidOperationException("Tenant cannot change after the database was used in this scope.");
        tenant.SetTenant(tenantId);
    }

    public async Task<TenantInfo?> FindTenantBySlugAsync(string slug, CancellationToken ct)
    {
        var cs = config.GetConnectionString(DataServiceExtensions.ConnectionStringName)
            ?? throw new InvalidOperationException("Connection string 'Default' is not configured.");
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_slug', @slug, true)", conn, tx))
        {
            set.Parameters.AddWithValue("slug", slug);
            await set.ExecuteNonQueryAsync(ct);
        }
        await using var cmd = new NpgsqlCommand("SELECT id, name, slug, modules, status FROM tenants WHERE slug = @slug", conn, tx);
        cmd.Parameters.AddWithValue("slug", slug);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new TenantInfo(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetFieldValue<string[]>(3), r.GetString(4))
            : null;
    }

    public async Task<TenantInfo?> GetTenantAsync(CancellationToken ct)
    {
        var id = tenant.TenantId;
        if (id is null) return null;
        var t = await Db.Tenants.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return t is null ? null : new TenantInfo(t.Id, t.Name, t.Slug, t.Modules, t.Status);
    }

    // ---------- users ----------

    public async Task<UserRecord?> FindUserByEmailAsync(string normalizedEmail, CancellationToken ct) =>
        await Db.Users.AsNoTracking().Where(u => u.Email == normalizedEmail).Select(Project).FirstOrDefaultAsync(ct);

    public async Task<UserRecord?> GetUserAsync(Guid id, CancellationToken ct) =>
        await Db.Users.AsNoTracking().Where(u => u.Id == id).Select(Project).FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<UserRecord>> ListUsersAsync(CancellationToken ct) =>
        await Db.Users.AsNoTracking().OrderBy(u => u.CreatedAt).Select(Project).ToListAsync(ct);

    public Task RegisterFailedLoginAsync(Guid userId, int maxAttempts, DateTimeOffset lockUntil, CancellationToken ct) =>
        Db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s
            .SetProperty(u => u.LockoutUntil, u => u.FailedLoginCount + 1 >= maxAttempts ? lockUntil : u.LockoutUntil)
            .SetProperty(u => u.FailedLoginCount, u => u.FailedLoginCount + 1), ct);

    public Task RegisterSuccessfulLoginAsync(Guid userId, DateTimeOffset now, CancellationToken ct) =>
        Db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s
            .SetProperty(u => u.FailedLoginCount, 0)
            .SetProperty(u => u.LockoutUntil, (DateTimeOffset?)null)
            .SetProperty(u => u.LastLoginAt, now), ct);

    public Task ResetLockoutAsync(Guid userId, CancellationToken ct) =>
        Db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s
            .SetProperty(u => u.FailedLoginCount, 0)
            .SetProperty(u => u.LockoutUntil, (DateTimeOffset?)null), ct);

    public async Task<bool> SetUserActiveAsync(Guid userId, bool active, DateTimeOffset now, CancellationToken ct)
    {
        var n = await Db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s
            .SetProperty(u => u.IsActive, active).SetProperty(u => u.UpdatedAt, now), ct);
        if (n > 0 && !active) await RevokeAllRefreshTokensAsync(userId, now, ct);
        return n > 0;
    }

    // ---------- refresh tokens ----------

    public async Task AddRefreshTokenAsync(Guid userId, string tokenHash, DateTimeOffset expiresAt, CancellationToken ct)
    {
        Db.RefreshTokens.Add(new RefreshToken { UserId = userId, TokenHash = tokenHash, ExpiresAt = expiresAt });
        await db.SaveChangesAsync(ct);
    }

    public async Task<RefreshRecord?> FindRefreshTokenAsync(string tokenHash, CancellationToken ct) =>
        await Db.RefreshTokens.AsNoTracking().Where(t => t.TokenHash == tokenHash)
            .Select(t => new RefreshRecord(t.Id, t.UserId, t.ExpiresAt, t.RevokedAt)).FirstOrDefaultAsync(ct);

    public async Task<bool> TryRevokeRefreshTokenAsync(Guid id, DateTimeOffset now, CancellationToken ct) =>
        await Db.RefreshTokens.Where(t => t.Id == id && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct) == 1;

    public Task RevokeAllRefreshTokensAsync(Guid userId, DateTimeOffset now, CancellationToken ct) =>
        Db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);

    // ---------- specialists (beauty_specialists під тим самим RLS) ----------

    public Task<bool> SpecialistExistsAsync(Guid specialistId, CancellationToken ct) =>
        Db.Specialists.AsNoTracking().AnyAsync(s => s.Id == specialistId, ct);

    public Task<bool> SpecialistHasUserAsync(Guid specialistId, CancellationToken ct) =>
        Db.Users.AsNoTracking().AnyAsync(u => u.SpecialistId == specialistId, ct);

    public Task<bool> SpecialistIsActiveAsync(Guid specialistId, CancellationToken ct) =>
        Db.Specialists.AsNoTracking().AnyAsync(s => s.Id == specialistId && s.IsActive, ct);

    // ---------- invites ----------

    public async Task<InviteRecord?> AddInviteAsync(NewInvite invite, CancellationToken ct)
    {
        await using var tx = await Db.Database.BeginTransactionAsync(ct);
        var now = DateTimeOffset.UtcNow;
        // Для specialist: lock профілю + відкликання попередніх pending з тим самим specialist_id (рівно одне активне запрошення
        // на профіль, навіть для іншого email і при паралельних запитах).
        if (invite.SpecialistId is { } specialistId)
        {
            await SpecialistLock.AcquireForInvitesAsync(db, specialistId, ct);
            if (!await db.Specialists.AnyAsync(s => s.Id == specialistId && s.IsActive, ct)) return null; // деактивовано паралельно
            await db.Invites.Where(i => i.SpecialistId == specialistId && i.AcceptedAt == null && i.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.RevokedAt, now), ct);
        }
        await db.Invites.Where(i => i.Email == invite.Email && i.AcceptedAt == null && i.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.RevokedAt, now), ct);
        var entity = new Invite
        {
            Email = invite.Email, Role = invite.Role, SpecialistId = invite.SpecialistId, TokenHash = invite.TokenHash,
            ExpiresAt = invite.ExpiresAt, InvitedByUserId = invite.InvitedByUserId,
        };
        db.Invites.Add(entity);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ToRecord(entity);
    }

    public async Task<IReadOnlyList<InviteRecord>> ListInvitesAsync(CancellationToken ct) =>
        (await Db.Invites.AsNoTracking().OrderByDescending(i => i.CreatedAt).ToListAsync(ct)).Select(ToRecord).ToList();

    public async Task<InviteRecord?> FindInviteByHashAsync(string tokenHash, CancellationToken ct) =>
        await Db.Invites.AsNoTracking().Where(i => i.TokenHash == tokenHash).FirstOrDefaultAsync(ct) is { } i ? ToRecord(i) : null;

    public async Task<bool> RevokeInviteAsync(Guid inviteId, DateTimeOffset now, CancellationToken ct) =>
        await Db.Invites.Where(i => i.Id == inviteId && i.AcceptedAt == null && i.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.RevokedAt, now), ct) == 1;

    public async Task<(WriteOutcome, UserRecord?)> AcceptInviteAsync(Guid inviteId, NewUser user, DateTimeOffset now, CancellationToken ct)
    {
        await using var tx = await Db.Database.BeginTransactionAsync(ct);
        var claimed = await db.Invites
            .Where(i => i.Id == inviteId && i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.AcceptedAt, now), ct);
        if (claimed != 1) return (WriteOutcome.Unavailable, null);

        var entity = NewUserEntity(user);
        db.Users.Add(entity);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return (WriteOutcome.Conflict, null); // tx відкотиться при Dispose: запрошення лишається чинним
        }
        await tx.CommitAsync(ct);
        return (WriteOutcome.Ok, ToRecord(entity));
    }

    // ---------- tenants (оператор платформи) ----------

    public async Task<(WriteOutcome, Guid)> CreateTenantWithOwnerAsync(NewTenant t, NewUser owner, CancellationToken ct)
    {
        tenant.SetTenant(t.Id); // RLS WITH CHECK (id = app.tenant_id) дозволяє вставку лише цього tenant
        var now = DateTimeOffset.UtcNow;
        Db.Tenants.Add(new Tenant
        {
            Id = t.Id, Name = t.Name, Slug = t.Slug, Modules = t.Modules.ToArray(), Status = "active", CreatedAt = now, UpdatedAt = now,
        });
        var ownerEntity = NewUserEntity(owner);
        ownerEntity.TenantId = t.Id;
        db.Users.Add(ownerEntity);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return (WriteOutcome.Conflict, Guid.Empty);
        }
        return (WriteOutcome.Ok, ownerEntity.Id);
    }

    public async Task<WriteOutcome> UpdateTenantAsync(
        Guid tenantId, IReadOnlyList<string>? modules, string? status, DateTimeOffset now, CancellationToken ct)
    {
        UseTenant(tenantId);
        var query = Db.Tenants.Where(x => x.Id == tenantId);
        var n = await query.ExecuteUpdateAsync(s => s.SetProperty(x => x.UpdatedAt, now), ct);
        if (modules is not null)
        {
            var array = modules.ToArray();
            await query.ExecuteUpdateAsync(s => s.SetProperty(x => x.Modules, array), ct);
        }
        if (status is not null)
            await query.ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, status), ct);
        return n == 1 ? WriteOutcome.Ok : WriteOutcome.Unavailable;
    }

    // ---------- helpers ----------

    private static readonly System.Linq.Expressions.Expression<Func<User, UserRecord>> Project = u =>
        new UserRecord(u.Id, u.TenantId, u.Email, u.FullName, u.PasswordHash, u.Role, u.SpecialistId, u.IsActive,
            u.FailedLoginCount, u.LockoutUntil, u.LastLoginAt);

    private static User NewUserEntity(NewUser u) => new()
    {
        Id = Guid.NewGuid(), Email = u.Email, FullName = u.FullName, PasswordHash = u.PasswordHash, Role = u.Role, SpecialistId = u.SpecialistId,
    };

    private static UserRecord ToRecord(User u) => Project.Compile()(u);

    private static InviteRecord ToRecord(Invite i) =>
        new(i.Id, i.Email, i.Role, i.SpecialistId, i.ExpiresAt, i.AcceptedAt, i.RevokedAt);

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        (ex.InnerException as PostgresException)?.SqlState == PostgresErrorCodes.UniqueViolation;
}
