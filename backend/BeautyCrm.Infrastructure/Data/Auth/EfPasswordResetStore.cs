using BeautyCrm.Application.Features.BeautyAuth;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;

namespace BeautyCrm.Infrastructure.Data.Auth;

/// <summary>
/// <see cref="IPasswordResetStore"/> над <c>auth_password_reset_issue</c> (void) і <c>auth_password_reset_consume</c> (§31.7, §31.16).
/// Власне з'єднання, без стану. Токена, його хеша й пошти з таблиці застосунок не читає ніколи.
/// </summary>
public sealed class EfPasswordResetStore(IConfiguration config) : IPasswordResetStore
{
    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var cs = config.GetConnectionString(DataServiceExtensions.ConnectionStringName)
            ?? throw new InvalidOperationException("Connection string 'Default' is not configured.");
        var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(ct);
        return conn;
    }

    public async Task IssueAsync(string normalizedEmail, byte[] loginKey, DateTimeOffset expiresAt, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT auth_password_reset_issue(@e, @k, @x)", conn);
        cmd.Parameters.Add(new NpgsqlParameter("e", NpgsqlDbType.Text) { Value = normalizedEmail });
        cmd.Parameters.Add(new NpgsqlParameter("k", NpgsqlDbType.Bytea) { Value = loginKey });
        cmd.Parameters.Add(new NpgsqlParameter("x", NpgsqlDbType.TimestampTz) { Value = expiresAt.ToUniversalTime() });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<Guid?> ConsumeAsync(byte[] tokenHash, string newPasswordHash, DateTimeOffset now, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT auth_password_reset_consume(@h, @p, @now)", conn);
        cmd.Parameters.Add(new NpgsqlParameter("h", NpgsqlDbType.Bytea) { Value = tokenHash });
        cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Text) { Value = newPasswordHash });
        cmd.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now.ToUniversalTime() });
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is Guid g ? g : null;
    }
}
