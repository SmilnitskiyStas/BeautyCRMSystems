import { readFileSync } from "node:fs";
import pg from "pg";
import { PostgreSqlContainer, type StartedPostgreSqlContainer } from "@testcontainers/postgresql";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { purgeControlPlaneNonces } from "../maintenance/nonce-cleanup";

// TASK-826 (L1): minimal worker grants (SELECT, DELETE as in deploy/staging/db/grants.sql) are enough to purge nonces.
const ROLE = "beauty_worker_nonce";
let container: StartedPostgreSqlContainer;
let admin: pg.Pool;
let worker: pg.Pool;

beforeAll(async () => {
  container = await new PostgreSqlContainer("postgres:16-alpine").start();
  admin = new pg.Pool({ connectionString: container.getConnectionUri() });
  await admin.query(readFileSync(new URL("../testing/schema.sql", import.meta.url), "utf8"));
  await admin.query(`CREATE ROLE ${ROLE} LOGIN PASSWORD 'pw' NOSUPERUSER NOBYPASSRLS`);
  await admin.query(`GRANT USAGE ON SCHEMA public TO ${ROLE}; GRANT SELECT, DELETE ON control_plane_nonces TO ${ROLE}`);
  const u = new URL(container.getConnectionUri());
  u.username = ROLE; u.password = "pw";
  worker = new pg.Pool({ connectionString: u.toString(), max: 2 });
}, 180_000);

afterAll(async () => { await worker?.end(); await admin?.end(); await container?.stop(); });

describe("purgeControlPlaneNonces", () => {
  it("removes only nonces seen more than 15 minutes ago", async () => {
    const now = new Date();
    const ago = (min: number) => new Date(now.getTime() - min * 60_000);
    const rows: Array<[string, Date]> = [
      ["AAAAAAAAAAAAAAAAAAAAAA", ago(60)], ["BBBBBBBBBBBBBBBBBBBBBB", ago(16)],
      ["CCCCCCCCCCCCCCCCCCCCCC", ago(14)], ["DDDDDDDDDDDDDDDDDDDDDD", ago(0)],
    ];
    for (const [nonce, seen] of rows)
      await admin.query("INSERT INTO control_plane_nonces (key_id, nonce, seen_at) VALUES ('beauty-k1', $1, $2)", [nonce, seen]);

    expect(await purgeControlPlaneNonces(worker, now)).toEqual({ deleted: 2 });
    const left = await admin.query("SELECT nonce FROM control_plane_nonces ORDER BY nonce");
    expect(left.rows.map((r) => r.nonce)).toEqual(["CCCCCCCCCCCCCCCCCCCCCC", "DDDDDDDDDDDDDDDDDDDDDD"]);
    expect(await purgeControlPlaneNonces(worker, now)).toEqual({ deleted: 0 });
  }, 60_000);
});
