using BeautyCrm.Application.Features.BeautyAuth;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;

namespace BeautyCrm.Infrastructure.Data.Auth;

/// <summary>
/// <see cref="IIdentityStore"/> над SECURITY DEFINER-функціями <c>auth_*</c> (ADR-024, §31.16). Власне з'єднання на кожен виклик
/// (як <see cref="EfAuthStore.FindTenantBySlugAsync"/>): виклики йдуть до визначення tenant і не залежать від app.tenant_id.
/// Прямого доступу до таблиць identities / auth_* у ролі застосунку немає. Пошта нормалізується й усередині функцій.
/// </summary>
public sealed class EfIdentityStore(IConfiguration config) : IIdentityStore
{
    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var cs = config.GetConnectionString(DataServiceExtensions.ConnectionStringName)
            ?? throw new InvalidOperationException("Connection string 'Default' is not configured.");
        var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(ct);
        return conn;
    }

    private static NpgsqlParameter P(string name, NpgsqlDbType type, object? value) =>
        new(name, type) { Value = value ?? DBNull.Value };

    private async Task<T?> ScalarAsync<T>(string sql, CancellationToken ct, params NpgsqlParameter[] args)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddRange(args);
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is null or DBNull ? default : (T)v;
    }

    public async Task<IdentityRecord?> FindAsync(string normalizedEmail, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT identity_id, password_hash, state FROM auth_identity_find(@e)", conn);
        cmd.Parameters.Add(P("e", NpgsqlDbType.Text, normalizedEmail));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new IdentityRecord(r.GetGuid(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2)) : null;
    }

    public async Task<bool> IsLockedAsync(byte[] loginKey, DateTimeOffset now, CancellationToken ct) =>
        await ScalarAsync<bool>("SELECT auth_login_attempt_is_locked(@k, @now)", ct,
            P("k", NpgsqlDbType.Bytea, loginKey), P("now", NpgsqlDbType.TimestampTz, now.ToUniversalTime()));

    public async Task<bool> RegisterFailedAsync(
        byte[] loginKey, Guid? identityId, int max, int windowSeconds, int lockoutSeconds, DateTimeOffset now, CancellationToken ct) =>
        await ScalarAsync<bool>("SELECT auth_login_attempt_failed(@k, @i, @max, @w, @l, @now)", ct,
            P("k", NpgsqlDbType.Bytea, loginKey), P("i", NpgsqlDbType.Uuid, identityId), P("max", NpgsqlDbType.Integer, max),
            P("w", NpgsqlDbType.Integer, windowSeconds), P("l", NpgsqlDbType.Integer, lockoutSeconds),
            P("now", NpgsqlDbType.TimestampTz, now.ToUniversalTime()));

    public Task ResetAttemptsAsync(byte[] loginKey, CancellationToken ct) =>
        ScalarAsync<object>("SELECT auth_login_attempt_reset(@k)", ct, P("k", NpgsqlDbType.Bytea, loginKey));

    public Task LoginSucceededAsync(Guid identityId, DateTimeOffset now, CancellationToken ct) =>
        ScalarAsync<object>("SELECT auth_identity_login_succeeded(@i, @now)", ct,
            P("i", NpgsqlDbType.Uuid, identityId), P("now", NpgsqlDbType.TimestampTz, now.ToUniversalTime()));

    public async Task<IReadOnlyList<MembershipRow>> MembershipsAsync(Guid identityId, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT tenant_id, user_id, tenant_name, tenant_slug, role FROM auth_identity_memberships(@i)", conn);
        cmd.Parameters.Add(P("i", NpgsqlDbType.Uuid, identityId));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<MembershipRow>();
        while (await r.ReadAsync(ct))
            rows.Add(new MembershipRow(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4)));
        return rows;
    }

    public async Task<bool> IsActiveAsync(Guid identityId, CancellationToken ct) =>
        await ScalarAsync<bool>("SELECT auth_identity_is_active(@i)", ct, P("i", NpgsqlDbType.Uuid, identityId));

    public async Task<bool> EmailInUseAsync(string normalizedEmail, CancellationToken ct) =>
        await ScalarAsync<bool>("SELECT auth_identity_email_in_use(@e)", ct, P("e", NpgsqlDbType.Text, normalizedEmail));

    public Task IssueTicketAsync(Guid identityId, byte[] tokenHash, DateTimeOffset expiresAt, CancellationToken ct) =>
        ScalarAsync<object>("SELECT auth_login_ticket_issue(@i, @h, @e)", ct,
            P("i", NpgsqlDbType.Uuid, identityId), P("h", NpgsqlDbType.Bytea, tokenHash), P("e", NpgsqlDbType.TimestampTz, expiresAt.ToUniversalTime()));

    public async Task<Guid?> ConsumeTicketAsync(byte[] tokenHash, Guid tenantId, DateTimeOffset now, CancellationToken ct)
    {
        var userId = await ScalarAsync<Guid>("SELECT auth_login_ticket_consume(@h, @t, @now)", ct,
            P("h", NpgsqlDbType.Bytea, tokenHash), P("t", NpgsqlDbType.Uuid, tenantId), P("now", NpgsqlDbType.TimestampTz, now.ToUniversalTime()));
        return userId == Guid.Empty ? null : userId; // NULL = квиток/членство непридатні
    }
}
