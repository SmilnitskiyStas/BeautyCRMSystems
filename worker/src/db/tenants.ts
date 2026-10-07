import type pg from "pg";

/**
 * Which tenants to poll. Under RLS the app role sees nothing without a tenant, so the list must come
 * from outside the tenant tables: static config now; `PgFunctionTenantDirectory` once the DB owner
 * provides a SECURITY DEFINER function (needs a migration - see TASK-686 open question).
 */
export interface TenantDirectory { listTenantIds(): Promise<string[]> }

export class StaticTenantDirectory implements TenantDirectory {
  constructor(private readonly ids: string[]) {}
  static fromEnv(value = process.env.WORKER_TENANT_IDS): StaticTenantDirectory {
    return new StaticTenantDirectory((value ?? "").split(",").map((s) => s.trim()).filter(Boolean));
  }
  async listTenantIds(): Promise<string[]> { return [...this.ids]; }
}

/** Calls `SELECT tenant_id FROM beauty_list_tenants()` (SECURITY DEFINER, EXECUTE granted to the worker role only). */
export class PgFunctionTenantDirectory implements TenantDirectory {
  constructor(private readonly pool: pg.Pool, private readonly fn = "beauty_list_tenants") {
    if (!/^[a-z_][a-z0-9_]*$/.test(fn)) throw new Error("Invalid function name");
  }
  async listTenantIds(): Promise<string[]> {
    const { rows } = await this.pool.query<{ tenant_id: string }>(`SELECT tenant_id FROM ${this.fn}()`);
    return rows.map((r) => r.tenant_id);
  }
}
