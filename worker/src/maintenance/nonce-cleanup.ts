/**
 * TASK-826 (L1): control_plane_nonces cleanup (ADR-018 §4). The signature window is 300 s, so a nonce older than 15 minutes
 * can never pass the timestamp check again and is safe to delete. The table has no tenant RLS: runs on the plain pool,
 * not through TenantDb.tx (which requires a tenant). The worker role needs only SELECT, DELETE (deploy/staging/db/grants.sql).
 */
export const NONCE_MAX_AGE_MS = 15 * 60_000;
export const NONCE_CLEANUP_EVERY_MS = 5 * 60_000;

export interface Queryable { query(sql: string, params?: unknown[]): Promise<{ rowCount: number | null }> }

export async function purgeControlPlaneNonces(pool: Queryable, now: Date, maxAgeMs: number = NONCE_MAX_AGE_MS): Promise<{ deleted: number }> {
  const cutoff = new Date(now.getTime() - maxAgeMs);
  const res = await pool.query("DELETE FROM control_plane_nonces WHERE seen_at < $1", [cutoff]);
  return { deleted: res.rowCount ?? 0 };
}
