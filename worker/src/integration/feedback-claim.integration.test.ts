import { randomUUID } from "node:crypto";
import { readFileSync } from "node:fs";
import pg from "pg";
import { PostgreSqlContainer, type StartedPostgreSqlContainer } from "@testcontainers/postgresql";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { TenantDb, withTenant } from "../db/tenant-db";
import { PgFeedbackStore } from "../feedback/feedback-store";

// TASK-821: parallel workers never lease the same feedback row; minimal worker grants are enough (column-level on users).
const NOW = new Date("2026-03-10T10:00:00Z");
const ROLE = "beauty_worker_fb";
let container: StartedPostgreSqlContainer;
let admin: pg.Pool;
let db: TenantDb;

beforeAll(async () => {
  container = await new PostgreSqlContainer("postgres:16-alpine").start();
  admin = new pg.Pool({ connectionString: container.getConnectionUri() });
  await admin.query(readFileSync(new URL("../testing/schema.sql", import.meta.url), "utf8"));
  await admin.query(`CREATE ROLE ${ROLE} LOGIN PASSWORD 'pw' NOSUPERUSER NOBYPASSRLS`);
  await admin.query(`GRANT USAGE ON SCHEMA public TO ${ROLE}; GRANT SELECT, UPDATE ON beauty_platform_feedback TO ${ROLE};
    GRANT SELECT ON tenants TO ${ROLE}; GRANT SELECT (id, tenant_id, email, full_name, role) ON users TO ${ROLE}`);
  const u = new URL(container.getConnectionUri());
  u.username = ROLE; u.password = "pw";
  db = TenantDb.create(u.toString(), 8);
}, 180_000);

afterAll(async () => { await db?.close(); await admin?.end(); await container?.stop(); });

describe("PgFeedbackStore", () => {
  it("leases each due row once across concurrent claimers and records outcomes", async () => {
    const tenantId = randomUUID(), userId = randomUUID();
    await admin.query("INSERT INTO tenants (id, name, slug) VALUES ($1,'Salon X','salon-fb')", [tenantId]);
    await admin.query("INSERT INTO users (id, tenant_id, email, full_name, password_hash, role) VALUES ($1,$2,'o@x.test','O','h','admin')", [userId, tenantId]);
    const ids = Array.from({ length: 6 }, () => randomUUID());
    for (const id of ids) {
      await admin.query("INSERT INTO beauty_platform_feedback (id, tenant_id, user_id, type, message, next_attempt_at) VALUES ($1,$2,$3,'bug','0123456789',$4)",
        [id, tenantId, userId, NOW]);
    }
    const store = new PgFeedbackStore(db);
    const claims = await withTenant(tenantId, () => Promise.all([1, 2, 3, 4].map(() => store.claimDue(NOW, 3))));
    const got = claims.flat().map((r) => r.id);
    expect(new Set(got).size).toBe(got.length);
    expect(got.length).toBe(6);
    expect(claims.flat()[0]).toMatchObject({ attempts: 1, tenantName: "Salon X", userRole: "admin", userEmail: "o@x.test", tenantRef: tenantId });
    expect(await withTenant(tenantId, () => store.claimDue(NOW, 10))).toEqual([]); // leased

    await withTenant(tenantId, async () => {
      await store.record(ids[0]!, { status: "delivered", leadId: "L1" }, NOW);
      await store.record(ids[1]!, { status: "failed", error: "http_422" }, NOW);
    });
    const { rows } = await admin.query("SELECT id, delivery_status, external_lead_id, last_error FROM beauty_platform_feedback WHERE id = ANY($1)", [[ids[0], ids[1]]]);
    const by = Object.fromEntries(rows.map((r) => [r.id, r]));
    expect(by[ids[0]!]).toMatchObject({ delivery_status: "delivered", external_lead_id: "L1" });
    expect(by[ids[1]!]).toMatchObject({ delivery_status: "failed", last_error: "http_422" });
  }, 60_000);
});
