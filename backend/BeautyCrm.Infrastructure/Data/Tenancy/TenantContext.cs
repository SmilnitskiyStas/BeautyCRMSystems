namespace BeautyCrm.Infrastructure.Data.Tenancy;

/// <summary>Current tenant for the request/job scope. Null = no tenant (RLS returns no rows).</summary>
public interface ITenantContext
{
    Guid? TenantId { get; }
}

/// <summary>
/// Scoped, mutable holder. Set it (auth middleware / worker job) BEFORE the first DB call in the
/// scope: app.tenant_id is applied when EF opens a connection, so changing the tenant while a
/// connection/transaction is already open does not affect that connection.
/// </summary>
public sealed class TenantContext : ITenantContext
{
    public TenantContext()
    {
    }

    public TenantContext(Guid tenantId) => TenantId = tenantId;

    public Guid? TenantId { get; private set; }

    public void SetTenant(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id must not be empty.", nameof(tenantId));
        TenantId = tenantId;
    }

    public void Clear() => TenantId = null;
}
