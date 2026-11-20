import { type TenantDb } from "../db/tenant-db";

/** §33.17: cosmetic transition pending_payment -> expired only. Never touches stock, payments or order lines. */
export const EXPIRE_BATCH_SIZE = 500;
/** Safety cap of batches per tenant per run (500 * 20 = 10 000 orders); the remainder is picked up by the next minute. */
export const MAX_BATCHES_PER_RUN = 20;
/** Grace after reservation_expires_at before the worker closes (the API already treats it as expired by time). */
export const EXPIRE_GRACE = "1 minute";
/** A payment started longer ago than this is counted as stuck (reconcile is the backend's job; the worker only reports). */
export const STUCK_AFTER = "30 minutes";

/** WORKER_PRODUCT_ORDERS_ENABLED: ONLY the literal `true` (case-insensitive, trimmed) enables the job. */
export function isProductOrdersFlagOn(value: string | undefined): boolean {
  return /^true$/i.test(value?.trim() ?? "");
}

export interface ProductOrdersExpireResult { expired: number; stuck: number }

export interface ProductOrdersExpireStore {
  /** Current tenant (withTenant scope): close expired unpaid orders in batches and count stuck payments. */
  runForTenant(now: Date): Promise<ProductOrdersExpireResult>;
}

// Column-level worker GRANT: SELECT id, tenant_id, status, reservation_expires_at, payment_started_at;
// UPDATE status, closed_at, close_reason, updated_at. Keep every statement inside these lists.
const EXPIRE_SQL = `
  UPDATE beauty_product_orders o
     SET status = 'expired', close_reason = 'expired', closed_at = o.reservation_expires_at, updated_at = $1
   WHERE o.id IN (SELECT s.id FROM beauty_product_orders s
                   WHERE s.status = 'pending_payment' AND s.payment_started_at IS NULL
                     AND s.reservation_expires_at <= $1::timestamptz - interval '${EXPIRE_GRACE}'
                   ORDER BY s.reservation_expires_at
                   LIMIT ${EXPIRE_BATCH_SIZE} FOR UPDATE SKIP LOCKED)
     AND o.status = 'pending_payment' AND o.payment_started_at IS NULL`;

const STUCK_SQL = `
  SELECT count(*)::int AS n FROM beauty_product_orders
   WHERE status = 'pending_payment' AND payment_started_at < $1::timestamptz - interval '${STUCK_AFTER}'`;

export class PgProductOrdersExpire implements ProductOrdersExpireStore {
  constructor(private readonly db: TenantDb) {}

  async runForTenant(now: Date): Promise<ProductOrdersExpireResult> {
    let expired = 0;
    for (let i = 0; i < MAX_BATCHES_PER_RUN; i++) {
      const n = await this.db.tx(async (c) => (await c.query(EXPIRE_SQL, [now])).rowCount ?? 0);
      expired += n;
      if (n < EXPIRE_BATCH_SIZE) break;
    }
    const stuck = await this.db.tx(async (c) => (await c.query<{ n: number }>(STUCK_SQL, [now])).rows[0]?.n ?? 0);
    return { expired, stuck };
  }
}
