import { describe, expect, it } from "vitest";
import { NONCE_CLEANUP_EVERY_MS, NONCE_MAX_AGE_MS, purgeControlPlaneNonces } from "./nonce-cleanup";

describe("purgeControlPlaneNonces", () => {
  it("deletes rows older than 15 minutes via a parameterized cutoff and reports the count", async () => {
    const calls: Array<{ sql: string; params?: unknown[] }> = [];
    const pool = { query: async (sql: string, params?: unknown[]) => { calls.push({ sql, params }); return { rowCount: 3 }; } };
    const now = new Date("2026-03-10T10:00:00Z");
    expect(await purgeControlPlaneNonces(pool, now)).toEqual({ deleted: 3 });
    expect(calls).toHaveLength(1);
    expect(calls[0]!.sql).toBe("DELETE FROM control_plane_nonces WHERE seen_at < $1");
    expect(calls[0]!.params).toEqual([new Date("2026-03-10T09:45:00Z")]);
  });

  it("runs every 5 minutes with a 15 minute retention", () => {
    expect(NONCE_CLEANUP_EVERY_MS).toBe(300_000);
    expect(NONCE_MAX_AGE_MS).toBe(900_000);
  });
});
