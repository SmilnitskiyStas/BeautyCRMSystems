import { AsyncLocalStorage } from "node:async_hooks";
import pg from "pg";

const scope = new AsyncLocalStorage<{ tenantId: string }>();
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** Runs `fn` for one tenant; every DB operation inside is bound to it (ports stay tenant-less). */
export function withTenant<T>(tenantId: string, fn: () => Promise<T>): Promise<T> {
  if (!UUID.test(tenantId)) throw new Error(`Invalid tenant id: ${tenantId}`);
  return scope.run({ tenantId }, fn);
}

export function currentTenantId(): string {
  const s = scope.getStore();
  if (!s) throw new Error("No tenant in scope: wrap the call in withTenant()");
  return s.tenantId;
}

/**
 * Pool over a role WITHOUT BYPASSRLS. Each operation = one transaction in which
 * `app.tenant_id` is set (transaction-local, so it can never leak through the pool) — the same
 * variable TenantConnectionInterceptor sets in the .NET backend. Fail-closed: no tenant => throws.
 */
export class TenantDb {
  constructor(readonly pool: pg.Pool) {}

  static create(connectionString: string, max = 10): TenantDb {
    return new TenantDb(new pg.Pool({ connectionString, max }));
  }

  async tx<T>(fn: (c: pg.PoolClient) => Promise<T>): Promise<T> {
    const tenantId = currentTenantId();
    const client = await this.pool.connect();
    try {
      await client.query("BEGIN");
      await client.query("SELECT set_config('app.tenant_id', $1, true)", [tenantId]);
      const result = await fn(client);
      await client.query("COMMIT");
      return result;
    } catch (err) {
      await client.query("ROLLBACK").catch(() => undefined);
      throw err;
    } finally {
      client.release();
    }
  }

  /** Refuses to run as a superuser / BYPASSRLS role: RLS would silently not apply. */
  async assertRlsEnforced(): Promise<void> {
    const { rows } = await this.pool.query<{ rolsuper: boolean; rolbypassrls: boolean; rolname: string }>(
      "SELECT rolname, rolsuper, rolbypassrls FROM pg_roles WHERE rolname = current_user",
    );
    const r = rows[0];
    if (!r || r.rolsuper || r.rolbypassrls) {
      throw new Error(`Worker DB role '${r?.rolname}' is superuser/BYPASSRLS; use a plain role so RLS is enforced`);
    }
  }

  close(): Promise<void> { return this.pool.end(); }
}
