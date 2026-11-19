import type { PoolClient } from "pg";
import { currentTenantId, type TenantDb } from "../db/tenant-db";
import type { Logger } from "../adapters/pg-support";

/** §32.20: thresholds (days), local-hour gate and per-run cap. */
export interface InventoryAlertConfig {
  expiryDays: number[];
  localHour: number;
  maxNewPerRun: number;
}

export const DEFAULT_INVENTORY_CONFIG: InventoryAlertConfig = { expiryDays: [30, 14, 7], localHour: 8, maxNewPerRun: 500 };

/** WORKER_INVENTORY_EXPIRY_DAYS (1..5 distinct ints 1..365) and WORKER_INVENTORY_LOCAL_HOUR (0..23); invalid => throws (worker does not start). */
export function loadInventoryConfig(env: NodeJS.ProcessEnv): InventoryAlertConfig {
  const cfg = { ...DEFAULT_INVENTORY_CONFIG, expiryDays: [...DEFAULT_INVENTORY_CONFIG.expiryDays] };
  const days = env.WORKER_INVENTORY_EXPIRY_DAYS?.trim();
  if (days) {
    const parts = days.split(",").map((s) => s.trim());
    const nums = parts.map((s) => (/^\d+$/.test(s) ? Number(s) : NaN));
    if (nums.length < 1 || nums.length > 5 || nums.some((n) => !Number.isInteger(n) || n < 1 || n > 365) || new Set(nums).size !== nums.length)
      throw new Error("Invalid WORKER_INVENTORY_EXPIRY_DAYS: expected 1..5 distinct integers 1..365");
    cfg.expiryDays = nums.sort((a, b) => b - a);
  }
  const hour = env.WORKER_INVENTORY_LOCAL_HOUR?.trim();
  if (hour) {
    const h = /^\d+$/.test(hour) ? Number(hour) : NaN;
    if (!Number.isInteger(h) || h < 0 || h > 23) throw new Error("Invalid WORKER_INVENTORY_LOCAL_HOUR: expected 0..23");
    cfg.localHour = h;
  }
  return cfg;
}

/** Local hour and ISO date of `now` in an IANA zone (DST-safe, via Intl). */
export function localParts(now: Date, timeZone: string): { hour: number; date: string } {
  const parts = new Intl.DateTimeFormat("en-CA", { timeZone, hourCycle: "h23", year: "numeric", month: "2-digit", day: "2-digit", hour: "2-digit" }).formatToParts(now);
  const get = (t: string) => parts.find((p) => p.type === t)!.value;
  return { hour: Number(get("hour")) % 24, date: `${get("year")}-${get("month")}-${get("day")}` };
}

/** 0 = already expired; otherwise the smallest threshold >= days; null = not yet within any threshold. */
export function expiryBucket(daysToExpiry: number, thresholdsDesc: number[]): number | null {
  if (daysToExpiry < 0) return 0;
  const fit = thresholdsDesc.filter((t) => t >= daysToExpiry);
  return fit.length ? Math.min(...fit) : null;
}

export interface InventoryRunResult { locations: number; created: number; resolved: number; capped: boolean; errors: number }

/** Tenant has the `beauty_inventory` module (kill switch together with WORKER_INVENTORY_ENABLED). */
export async function inventoryModuleOn(c: PoolClient): Promise<boolean> {
  const { rows } = await c.query<{ ok: boolean }>(
    `SELECT EXISTS (SELECT 1 FROM tenants t WHERE t.id = NULLIF(current_setting('app.tenant_id', true), '')::uuid AND t.status = 'active'
                    AND EXISTS (SELECT 1 FROM unnest(t.modules) m WHERE lower(m) = 'beauty_inventory')) AS ok`);
  return rows[0]?.ok === true;
}

export interface InventoryAlertStore {
  /** Current tenant (withTenant scope): resolve stale alerts, create new ones. Never throws for a single bad location. */
  runForTenant(now: Date, cfg: InventoryAlertConfig): Promise<InventoryRunResult>;
}

// effective minimum and balance of a (location, product) pair; only pairs with an existing level row, active and enabled product
const PAIRS = `
  SELECT l.tenant_id, l.location_id, l.product_id, l.quantity - l.shortage_quantity AS bal, coalesce(s.min_stock, p.min_stock) AS eff
  FROM beauty_stock_levels l
  JOIN beauty_products p ON p.tenant_id = l.tenant_id AND p.id = l.product_id
  LEFT JOIN beauty_product_location_settings s ON s.tenant_id = l.tenant_id AND s.product_id = l.product_id AND s.location_id = l.location_id
  WHERE l.location_id = $1 AND p.is_active AND coalesce(s.is_enabled, true)`;

export class PgInventoryAlerts implements InventoryAlertStore {
  constructor(private readonly db: TenantDb, private readonly logger?: Logger) {}

  async runForTenant(now: Date, cfg: InventoryAlertConfig): Promise<InventoryRunResult> {
    const res: InventoryRunResult = { locations: 0, created: 0, resolved: 0, capped: false, errors: 0 };
    const locations = await this.db.tx(async (c) =>
      (await c.query<{ id: string; timezone: string }>("SELECT id, timezone FROM beauty_locations WHERE is_active ORDER BY id")).rows);
    let remaining = cfg.maxNewPerRun;
    for (const loc of locations) {
      res.locations++;
      try {
        const r = await this.db.tx((c) => this.runLocation(c, loc, now, cfg, remaining));
        res.created += r.created;
        res.resolved += r.resolved;
        remaining -= r.created;
        if (r.capped) res.capped = true;
      } catch (err) {
        res.errors++; // one broken location (e.g. invalid timezone) does not stop the rest
        // L3: ids and error code only; never the error message (it may carry product names/quantities)
        const e = err as { code?: unknown; name?: unknown } | null;
        const errorCode = typeof e?.code === "string" ? e.code : typeof e?.name === "string" ? e.name : "error";
        this.logger?.error(`inventory_alerts_location_failed tenant=${currentTenantId()} location=${loc.id} errorCode=${errorCode}`);
      }
    }
    return res;
  }

  private async runLocation(c: PoolClient, loc: { id: string; timezone: string }, now: Date, cfg: InventoryAlertConfig, remaining: number) {
    let resolved = 0, created = 0, capped = false;
    // 1. resolve (every run, no daily gate)
    resolved += (await c.query(
      `UPDATE beauty_staff_alerts a SET status = 'resolved', resolved_at = $2, updated_at = $2
       WHERE a.location_id = $1 AND a.kind = 'low_stock' AND a.status IN ('open', 'read')
         AND NOT EXISTS (SELECT 1 FROM (${PAIRS}) q WHERE q.product_id = a.product_id AND q.eff > 0 AND q.bal < q.eff)`, [loc.id, now])).rowCount ?? 0;
    resolved += (await c.query(
      `UPDATE beauty_staff_alerts a SET status = 'resolved', resolved_at = $2, updated_at = $2
       WHERE a.location_id = $1 AND a.kind = 'expiring' AND a.status IN ('open', 'read')
         AND EXISTS (SELECT 1 FROM beauty_stock_batches b WHERE b.tenant_id = a.tenant_id AND b.id = a.batch_id AND b.quantity = 0)`, [loc.id, now])).rowCount ?? 0;
    resolved += (await c.query(
      `UPDATE beauty_staff_alerts a SET status = 'resolved', resolved_at = $2, updated_at = $2
       WHERE a.location_id = $1 AND a.kind = 'shortage' AND a.status IN ('open', 'read')
         AND EXISTS (SELECT 1 FROM beauty_stock_levels l WHERE l.tenant_id = a.tenant_id AND l.location_id = a.location_id
                     AND l.product_id = a.product_id AND l.shortage_quantity = 0)`, [loc.id, now])).rowCount ?? 0;

    // 2. daily gate: local hour of the venue
    const local = localParts(now, loc.timezone);
    if (local.hour < cfg.localHour || remaining <= 0) return { created, resolved, capped: remaining <= 0 && local.hour >= cfg.localHour };

    // 3. low_stock
    const low = await c.query(
      `INSERT INTO beauty_staff_alerts (tenant_id, kind, location_id, product_id, quantity, threshold, dedupe_key, created_at, updated_at)
       SELECT q.tenant_id, 'low_stock', q.location_id, q.product_id, q.bal, q.eff, 'low_stock:' || q.location_id || ':' || q.product_id || ':' || $2::text, $3, $3
       FROM (${PAIRS}) q
       WHERE q.eff > 0 AND q.bal < q.eff
         AND NOT EXISTS (SELECT 1 FROM beauty_staff_alerts a WHERE a.tenant_id = q.tenant_id AND a.location_id = q.location_id
                         AND a.product_id = q.product_id AND a.kind = 'low_stock' AND a.status IN ('open', 'read'))
         AND NOT EXISTS (SELECT 1 FROM beauty_staff_alerts a WHERE a.tenant_id = q.tenant_id
                         AND a.dedupe_key = 'low_stock:' || q.location_id || ':' || q.product_id || ':' || $2::text)
       ORDER BY q.product_id LIMIT $4
       ON CONFLICT (tenant_id, dedupe_key) DO NOTHING`, [loc.id, local.date, now, remaining]);
    created += low.rowCount ?? 0;
    if ((low.rowCount ?? 0) >= remaining) capped = true;

    // 4. expiring (any product, even inactive); closest expiry first so the cap never hides the urgent ones
    const left = remaining - created;
    if (left > 0) {
      const ins = await c.query<{ batch_id: string; days_bucket: number }>(
        `WITH cand AS (
           SELECT b.tenant_id, b.id AS batch_id, b.product_id, b.quantity, b.expires_on,
                  CASE WHEN b.expires_on < $2::date THEN 0
                       ELSE (SELECT min(t) FROM unnest($3::int[]) t WHERE t >= (b.expires_on - $2::date)) END AS bucket
           FROM beauty_stock_batches b
           WHERE b.location_id = $1 AND b.quantity > 0 AND b.expires_on IS NOT NULL)
         INSERT INTO beauty_staff_alerts (tenant_id, kind, location_id, product_id, batch_id, quantity, expires_on, days_bucket, dedupe_key, created_at, updated_at)
         SELECT cand.tenant_id, 'expiring', $1, cand.product_id, cand.batch_id, cand.quantity, cand.expires_on, cand.bucket,
                'expiring:' || cand.batch_id || ':' || cand.bucket, $4, $4
         FROM cand
         WHERE cand.bucket IS NOT NULL
           AND NOT EXISTS (SELECT 1 FROM beauty_staff_alerts a WHERE a.tenant_id = cand.tenant_id AND a.dedupe_key = 'expiring:' || cand.batch_id || ':' || cand.bucket)
         ORDER BY cand.expires_on, cand.batch_id LIMIT $5
         ON CONFLICT (tenant_id, dedupe_key) DO NOTHING
         RETURNING batch_id, days_bucket`, [loc.id, local.date, cfg.expiryDays, now, left]);
      created += ins.rowCount ?? 0;
      if ((ins.rowCount ?? 0) >= left) capped = true;
      if (ins.rows.length) {
        // replaced by the closer threshold
        resolved += (await c.query(
          `UPDATE beauty_staff_alerts a SET status = 'resolved', resolved_at = $3, updated_at = $3
           FROM unnest($1::uuid[], $2::int[]) AS n(batch_id, bucket)
           WHERE a.kind = 'expiring' AND a.batch_id = n.batch_id AND a.days_bucket <> n.bucket AND a.status IN ('open', 'read')`,
          [ins.rows.map((r) => r.batch_id), ins.rows.map((r) => r.days_bucket), now])).rowCount ?? 0;
      }
    }
    return { created, resolved, capped };
  }
}
