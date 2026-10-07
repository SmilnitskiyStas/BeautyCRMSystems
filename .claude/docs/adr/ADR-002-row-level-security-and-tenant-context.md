# ADR-002: Row-Level Security (RLS) & Tenant Context

**Date:** 2026-10-07

**Status:** Accepted (TASK-674)

**Context:**

Beauty CRM serves multiple independent tenants (salons, networks) in a single deployment. Tenant data isolation is **critical**: accidental data leaks are unacceptable (e.g., revealing competitor's client list, payment info).

Two approaches:
1. **Application-level isolation:** Filter every query with `WHERE tenant_id = ?`
2. **Database-level isolation:** PostgreSQL RLS policies enforce isolation regardless of query

---

## Decision

We implement **PostgreSQL Row-Level Security (RLS) in FORCE mode** as the primary isolation mechanism.

### How it works

1. **Session context:** Before each database operation, set PostgreSQL session variable:
   ```sql
   SELECT set_config('app.tenant_id', '550e8400-e29b-41d4-a716-446655440000', true);
   ```

2. **RLS Policy:** On all `beauty_*` tables:
   ```sql
   CREATE POLICY tenant_isolation ON beauty_appointments
     USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
     WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
   ```

3. **FORCE RLS:** Table owner (app role) is also subject to RLS; cannot be bypassed:
   ```sql
   ALTER TABLE beauty_appointments FORCE ROW LEVEL SECURITY;
   ```

4. **No superuser/BYPASSRLS:** Runtime role `beautycrm_app` is neither superuser nor BYPASSRLS:
   ```sql
   CREATE ROLE beautycrm_app LOGIN PASSWORD '...';
   -- Revoke all defaults; grant only what's needed
   REVOKE ALL ON DATABASE beautycrm FROM beautycrm_app;
   GRANT CONNECT, TEMPORARY ON DATABASE beautycrm TO beautycrm_app;
   GRANT USAGE ON SCHEMA public TO beautycrm_app;
   GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO beautycrm_app;
   ```

5. **Application enforcement:** `TenantContext` sets the session variable at the start of each request:
   ```csharp
   public class TenantConnectionInterceptor : DbConnectionInterceptor
   {
       public override async ValueTask<InterceptionResult> ConnectionOpeningAsync(
           DbConnection connection, ConnectionEventData eventData, InterceptionResult result,
           CancellationToken cancellationToken = default)
       {
           if (_tenantContext.TenantId != null)
           {
               await connection.ExecuteAsync("SELECT set_config('app.tenant_id', @tid::text, true)",
                   new { tid = _tenantContext.TenantId.ToString() });
           }
           return result;
       }
   }
   ```

---

## Rationale

### Why FORCE RLS?

Without FORCE, a PostgreSQL superuser (or the table owner in some contexts) can read all rows. In production:
- If someone gains admin access to the database
- If a rogue DBA or cloud provider employee accesses the database directly
- **FORCE RLS prevents lateral movement** — even at the database layer, one tenant cannot see another's data

### Why not just application-level filtering?

**Risks:**
- Query bug (missing `WHERE tenant_id = ?`) leaks data silently
- Middleware misconfiguration (tenant context set to wrong value)
- No audit trail of data access
- ORM or SQL mistake is hard to catch in tests

**Advantages of RLS:**
- Defense in depth: even if app layer fails, DB layer protects
- Audit trail: PostgreSQL logs policy violations
- Enforced by constraint (cannot accidentally query other tenant's data)

### Why not use PostgreSQL built-in tenant extensions?

Some extensions (e.g., `pg_partman`) partition data by tenant, but they:
- Add complexity (partition management, query planning)
- Don't provide per-table isolation (still need RLS or application filters)

**Decision:** RLS is simpler and sufficient for current scale.

---

## Implementation

### TenantContext (Application Layer)

```csharp
public interface ITenantContext
{
    Guid? TenantId { get; }
    void SetTenant(Guid tenantId);
}

public class TenantContext : ITenantContext
{
    private static readonly AsyncLocal<Guid?> Current = new();

    public Guid? TenantId => Current.Value;
    public void SetTenant(Guid tenantId) => Current.Value = tenantId;
}
```

**Scoped per request:**
```csharp
services.AddScoped<ITenantContext, TenantContext>();
```

### Middleware (sets tenant from JWT claim or dev header)

```csharp
public class TenantMiddleware(IServiceProvider services)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var tenantContext = services.GetRequiredService<ITenantContext>();

        // Production: from JWT claim
        var tenantClaim = context.User.FindFirst("tenant_id");
        if (tenantClaim?.Value is { } id && Guid.TryParse(id, out var tenantId))
        {
            tenantContext.SetTenant(tenantId);
        }

        // Development only: from header
        else if (!context.RequestServices.GetRequiredService<IHostEnvironment>().IsProduction())
        {
            if (context.Request.Headers.TryGetValue("X-Tenant-Id", out var header)
                && Guid.TryParse(header.ToString(), out var devTenantId))
            {
                tenantContext.SetTenant(devTenantId);
            }
        }

        await next(context);
    }
}
```

**Registered in Program.cs:**
```csharp
app.UseMiddleware<TenantMiddleware>(); // After auth, before controllers
```

### Database Connection Interceptor

EF Core `DbConnectionInterceptor` sets the session variable when connection opens:

```csharp
public class TenantConnectionInterceptor(ITenantContext context) : DbConnectionInterceptor
{
    public override async ValueTask<InterceptionResult> ConnectionOpeningAsync(
        DbConnection connection, ConnectionEventData eventData, InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        if (context.TenantId is { } tenantId)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT set_config('app.tenant_id', $1::text, true)";
            cmd.Parameters.Add(new NpgsqlParameter("$1", tenantId.ToString()));
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        return result;
    }
}
```

**Registered:**
```csharp
services.AddScoped<TenantConnectionInterceptor>();
services.AddDbContext<BeautyDbContext>((provider, options) =>
{
    var interceptor = provider.GetRequiredService<TenantConnectionInterceptor>();
    options.AddInterceptors(interceptor);
});
```

### Database-Level Policy

All `beauty_*` tables include:

```sql
ALTER TABLE beauty_appointments ENABLE ROW LEVEL SECURITY;
ALTER TABLE beauty_appointments FORCE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation ON beauty_appointments
  USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
  WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
```

**USING:** Applies to SELECT/UPDATE/DELETE (which rows can the user see?)
**WITH CHECK:** Applies to INSERT/UPDATE (which rows can the user modify?)

Both use the same predicate to prevent data escape.

---

## Special Cases

### Webhook handling (channel_webhook_lookup)

Webhooks receive a channel ID but not a tenant ID (webhook URL is public). We use a special policy:

```sql
CREATE POLICY channel_webhook_lookup ON beauty_channels
  FOR SELECT
  USING (id = NULLIF(current_setting('app.channel_id', true), '')::uuid);
```

**Flow:**
1. Webhook handler reads channel ID from URL path.
2. Sets `app.channel_id` (but not `app.tenant_id`).
3. Queries `SELECT * FROM beauty_channels WHERE id = ...`.
4. Policy allows exactly that row (due to `channel_webhook_lookup`).
5. App reads `tenant_id` from the row.
6. Sets `app.tenant_id` to the resolved tenant.
7. Continues processing (conversations, messages) under full RLS.

### Login (tenants_login_lookup)

Similar pattern for login (before JWT is issued):

```sql
CREATE POLICY tenants_login_lookup ON tenants
  FOR SELECT
  USING (slug = current_setting('app.tenant_slug', true));
```

Allows looking up a tenant by slug during login.

---

## Consequences

### Positive

1. **Defense in depth:** Data isolation enforced at DB, not just app
2. **Audit trail:** PostgreSQL logs policy violations (optional, can configure)
3. **No query mistakes:** Forgot a WHERE clause? Policy still filters
4. **Scaleability:** Tenant-specific indexes work without app changes

### Negative

1. **RLS overhead:** ~5% query latency penalty (policy evaluation); negligible for most workloads
2. **Debugging complexity:** Errors may point to "0 rows returned" instead of "access denied"
   - **Mitigation:** Log all policy violations; monitor PostgreSQL logs
3. **Connection pooling:** If using connection pool (PgBouncer), session variables are lost on reuse
   - **Mitigation:** PgBouncer in transaction mode (SET resets between queries); or disable pooling for Kubernetes where connections are short-lived

---

## Testing

See `backend/BeautyCrm.Tests/Data/TenantIsolationTests.cs`:

- Tenant A creates appointment → Tenant B cannot see it
- Tenant A's queries filtered to tenant_id = A (no cross-tenant leaks)
- Exclusion constraint prevents overlaps even when isolation tested

**Run tests:**
```bash
export BEAUTY_TEST_PG_ADMIN="Host=localhost;Username=postgres;Password=..."
dotnet test --filter "TenantIsolation"
```

---

## Alternatives Considered

### 1. No RLS, application-level filtering only
**Pros:** Simpler setup, lower DB overhead
**Cons:** Single query bug leaks data; no protection if app is compromised

### 2. Per-tenant database
**Pros:** Complete isolation, no cross-tenant bugs possible
**Cons:** Operational nightmare (N databases to manage, backup, upgrade); higher cost

### 3. Row encryption (app-level)
**Pros:** Confidentiality even if DB is stolen
**Cons:** Encryption/decryption overhead; complex key management; doesn't prevent row-count leaks

---

## References

- [PostgreSQL RLS Documentation](https://www.postgresql.org/docs/16/ddl-rowsecurity.html)
- `.claude/docs/database.md` — Full schema & RLS policies
- `backend/BeautyCrm.Infrastructure/Data/Tenancy/` — Implementation
- `backend/BeautyCrm.Api/Tenancy/TenantMiddleware.cs` — Middleware
