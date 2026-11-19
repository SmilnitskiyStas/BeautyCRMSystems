const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/**
 * M1 (security review stage 2b): legal gate of owner Telegram alerts for the worker, same idea as `assertMarketingLegalGate`.
 * Development = ASPNETCORE_ENVIRONMENT=Development and NODE_ENV not production. Outside it, with WORKER_INVENTORY_TELEGRAM_ENABLED=true,
 * either WORKER_INVENTORY_TELEGRAM_L3_APPROVED=true (production) or a non-empty WORKER_INVENTORY_TELEGRAM_PRE_L3_TENANT_IDS
 * (pre-L3 staging: only these tenants are processed) is required; otherwise throws (worker does not start).
 * Returns the tenant allowlist to enforce (null = no restriction).
 */
export function assertInventoryTelegramGate(env: NodeJS.ProcessEnv): Set<string> | null {
  const on = (v: string | undefined) => /^true$/i.test(v?.trim() ?? "");
  if (!on(env.WORKER_INVENTORY_TELEGRAM_ENABLED)) return null;
  const development = env["ASPNETCORE_ENVIRONMENT"] === "Development" && env["NODE_ENV"] !== "production";
  if (development || on(env.WORKER_INVENTORY_TELEGRAM_L3_APPROVED)) return null;
  const ids = (env.WORKER_INVENTORY_TELEGRAM_PRE_L3_TENANT_IDS ?? "").split(",").map((s) => s.trim().toLowerCase()).filter(Boolean);
  if (ids.length === 0)
    throw new Error("WORKER_INVENTORY_TELEGRAM_ENABLED outside Development requires WORKER_INVENTORY_TELEGRAM_L3_APPROVED=true or a non-empty WORKER_INVENTORY_TELEGRAM_PRE_L3_TENANT_IDS allowlist (legal gate L3)");
  const bad = ids.find((i) => !UUID.test(i));
  if (bad) throw new Error("Invalid WORKER_INVENTORY_TELEGRAM_PRE_L3_TENANT_IDS: expected comma-separated UUIDs");
  return new Set(ids);
}
