using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BeautyCrm.Infrastructure.Data.Tenancy;

/// <summary>
/// Sets the session variable <c>app.tenant_id</c> every time EF opens a connection, so the RLS
/// policies (<c>tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid</c>) see the
/// current tenant. No tenant -> empty string -> NULL -> no rows visible, inserts rejected
/// (fail-closed). Session-level (is_local=false) so it covers statements outside a transaction;
/// Npgsql resets session state (DISCARD ALL) when the connection returns to the pool, and we
/// overwrite it on every open anyway.
/// </summary>
public sealed class TenantConnectionInterceptor : DbConnectionInterceptor
{
    internal const string SetTenantSql = "SELECT set_config('app.tenant_id', @tenant_id, false)";

    private readonly ITenantContext _tenant;

    public TenantConnectionInterceptor(ITenantContext tenant) => _tenant = tenant;

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var cmd = CreateCommand(connection);
        cmd.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var cmd = CreateCommand(connection);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private DbCommand CreateCommand(DbConnection connection)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = SetTenantSql;
        var p = cmd.CreateParameter();
        p.ParameterName = "tenant_id";
        p.Value = _tenant.TenantId?.ToString() ?? string.Empty;
        cmd.Parameters.Add(p);
        return cmd;
    }
}
