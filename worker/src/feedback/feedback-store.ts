import type { TenantDb } from "../db/tenant-db";
import { LEASE_MS, type FeedbackRow, type Resolution } from "./delivery";

export interface FeedbackStore {
  /** Atomically leases due pending rows of the current tenant (attempts+1, next_attempt_at=now+lease). */
  claimDue(now: Date, limit: number): Promise<FeedbackRow[]>;
  record(id: string, r: Resolution, now: Date): Promise<void>;
}

/**
 * Tenant-scoped (RLS) store. The claim is one statement with FOR UPDATE SKIP LOCKED + a lease, so parallel workers never take the
 * same row and no transaction is held open during the HTTP call; a crashed worker's row simply becomes due again after the lease.
 */
export class PgFeedbackStore implements FeedbackStore {
  constructor(private readonly db: TenantDb) {}

  claimDue(now: Date, limit: number): Promise<FeedbackRow[]> {
    return this.db.tx(async (c) => {
      const { rows } = await c.query(
        `WITH due AS (
           SELECT id FROM beauty_platform_feedback
            WHERE delivery_status = 'pending' AND next_attempt_at <= $1
            ORDER BY next_attempt_at, id LIMIT $2
              FOR UPDATE SKIP LOCKED),
         leased AS (
           UPDATE beauty_platform_feedback f
              SET attempts = f.attempts + 1, next_attempt_at = $3, updated_at = $1
             FROM due WHERE f.id = due.id
           RETURNING f.id, f.tenant_id, f.user_id, f.type, f.severity, f.message, f.page_url, f.attempts)
         SELECT l.id, l.type, l.severity, l.message, l.page_url, l.attempts,
                t.name AS tenant_name, COALESCE(t.external_ref, t.id::text) AS tenant_ref,
                u.email AS user_email, u.role AS user_role
           FROM leased l
           JOIN tenants t ON t.id = l.tenant_id
           LEFT JOIN users u ON u.id = l.user_id AND u.tenant_id = l.tenant_id
          ORDER BY l.id`,
        [now, limit, new Date(now.getTime() + LEASE_MS)],
      );
      return rows.map((r): FeedbackRow => ({
        id: r.id, type: r.type, severity: r.severity, message: r.message, pageUrl: r.page_url, attempts: r.attempts,
        tenantName: r.tenant_name, tenantRef: r.tenant_ref, userEmail: r.user_email, userRole: r.user_role,
      }));
    });
  }

  record(id: string, r: Resolution, now: Date): Promise<void> {
    return this.db.tx(async (c) => {
      // guarded by delivery_status = 'pending': a row already finished by another path is never overwritten
      if (r.status === "delivered") {
        await c.query(
          `UPDATE beauty_platform_feedback SET delivery_status='delivered', delivered_at=$2, external_lead_id=$3, last_error=NULL, updated_at=$2
            WHERE id=$1 AND delivery_status='pending'`, [id, now, r.leadId]);
      } else if (r.status === "failed") {
        await c.query(
          `UPDATE beauty_platform_feedback SET delivery_status='failed', last_error=$2, updated_at=$3
            WHERE id=$1 AND delivery_status='pending'`, [id, r.error.slice(0, 200), now]);
      } else {
        await c.query(
          `UPDATE beauty_platform_feedback SET next_attempt_at=$2, last_error=$3, updated_at=$4
            WHERE id=$1 AND delivery_status='pending'`, [id, r.nextAttemptAt, r.error.slice(0, 200), now]);
      }
    });
  }
}
