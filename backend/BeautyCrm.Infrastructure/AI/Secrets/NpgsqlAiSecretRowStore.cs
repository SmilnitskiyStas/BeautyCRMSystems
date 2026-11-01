using Microsoft.Extensions.Configuration;
using Npgsql;

namespace BeautyCrm.Infrastructure.AI.Secrets;

/// <summary>
/// Npgsql-реалізація <see cref="IAiSecretRowStore"/> над <c>beauty_ai_provider_keys</c> (ADR-010 §2.3; схему створює TASK-713).
/// Рядки читаються/пишуться runtime-роллю (<c>ConnectionStrings:Default</c>, NOBYPASSRLS) у транзакції з
/// <c>set_config('app.tenant_id', ..., true)</c>. Перелік tenant-ів потребує з'єднання, що бачить усі рядки
/// <c>tenants</c> (<c>ConnectionStrings:Migrator</c>, роль-власник), бо runtime-роль під RLS бачить лише поточний tenant.
/// Колонки: tenant_id, provider, key_encrypted, key_kid. Не перевірено на реальній таблиці до міграції TASK-713.
/// </summary>
public sealed class NpgsqlAiSecretRowStore(IConfiguration config) : IAiSecretRowStore
{
    private string Runtime => config.GetConnectionString("Default")
        ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured.");

    public async Task<IReadOnlyList<Guid>> ListTenantIdsAsync(CancellationToken ct)
    {
        var cs = config.GetConnectionString("Migrator")
            ?? throw new InvalidOperationException("ConnectionStrings:Migrator is required to enumerate tenants (or pass a single tenant id).");
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT id FROM tenants ORDER BY id", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var ids = new List<Guid>();
        while (await r.ReadAsync(ct)) ids.Add(r.GetGuid(0));
        return ids;
    }

    public async Task<IReadOnlyList<AiSecretRow>> ListRowsAsync(Guid tenantId, CancellationToken ct)
    {
        await using var conn = await OpenAsync(tenantId, ct);
        await using var tx = await BeginAsync(conn, tenantId, ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT provider, key_encrypted FROM beauty_ai_provider_keys WHERE tenant_id = @t ORDER BY provider", conn, tx);
        cmd.Parameters.AddWithValue("t", tenantId);
        var rows = new List<AiSecretRow>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) rows.Add(new AiSecretRow(r.GetString(0), r.GetString(1)));
        await tx.CommitAsync(ct);
        return rows;
    }

    public async Task<bool> ReplaceAsync(Guid tenantId, string provider, string expectedOld, string newValue, string newKeyId, CancellationToken ct)
    {
        await using var conn = await OpenAsync(tenantId, ct);
        await using var tx = await BeginAsync(conn, tenantId, ct);
        await using var cmd = new NpgsqlCommand(
            "UPDATE beauty_ai_provider_keys SET key_encrypted = @n, key_kid = @k " +
            "WHERE tenant_id = @t AND provider = @p AND key_encrypted = @o", conn, tx);
        cmd.Parameters.AddWithValue("n", newValue);
        cmd.Parameters.AddWithValue("k", newKeyId);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("p", provider);
        cmd.Parameters.AddWithValue("o", expectedOld);
        var changed = await cmd.ExecuteNonQueryAsync(ct) == 1;
        await tx.CommitAsync(ct);
        return changed;
    }

    public async Task<bool> DeleteAsync(Guid tenantId, string provider, string expectedOld, CancellationToken ct)
    {
        await using var conn = await OpenAsync(tenantId, ct);
        await using var tx = await BeginAsync(conn, tenantId, ct);
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM beauty_ai_provider_keys WHERE tenant_id = @t AND provider = @p AND key_encrypted = @o", conn, tx);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("p", provider);
        cmd.Parameters.AddWithValue("o", expectedOld);
        var changed = await cmd.ExecuteNonQueryAsync(ct) == 1;
        await tx.CommitAsync(ct);
        return changed;
    }

    private async Task<NpgsqlConnection> OpenAsync(Guid tenantId, CancellationToken ct)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant id must not be empty.", nameof(tenantId));
        var conn = new NpgsqlConnection(Runtime);
        await conn.OpenAsync(ct);
        return conn;
    }

    // app.tenant_id з is_local=true: RLS діє в межах транзакції, tenant не витікає в пул.
    private static async Task<NpgsqlTransaction> BeginAsync(NpgsqlConnection conn, Guid tenantId, CancellationToken ct)
    {
        var tx = await conn.BeginTransactionAsync(ct);
        await using var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, true)", conn, tx);
        set.Parameters.AddWithValue("t", tenantId.ToString());
        await set.ExecuteNonQueryAsync(ct);
        return tx;
    }
}
