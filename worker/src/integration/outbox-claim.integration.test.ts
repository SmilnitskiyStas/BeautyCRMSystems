import { randomBytes, randomUUID } from "node:crypto";
import { readFileSync } from "node:fs";
import pg from "pg";
import { PostgreSqlContainer, type StartedPostgreSqlContainer } from "@testcontainers/postgresql";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { writeCredentials } from "../adapters/channel-crypto";
import { PgBeautyData } from "../adapters/pg-data";
import { PgQueuePort } from "../adapters/pg-support";
import { TenantDb, withTenant } from "../db/tenant-db";
import { processOutbox } from "../jobs/beauty-outbox";
import type { BeautyDeps, StoredMessage } from "../ports";

// L3 (TASK-733): the .NET sweeper/inline dispatcher and this worker may both pick the same `pending` row.
// Only the CAS pending -> sending lets one of them deliver. The idempotency port here ALWAYS grants the claim, so only the CAS protects.

const NOW = new Date("2026-03-10T10:00:00Z");
const ROLE = "beauty_worker_claim";
const ROLE_PW = "worker_pw";

let container: StartedPostgreSqlContainer;
let admin: pg.Pool;
let db: TenantDb;

beforeAll(async () => {
  container = await new PostgreSqlContainer("postgres:16-alpine").start();
  admin = new pg.Pool({ connectionString: container.getConnectionUri() });
  await admin.query(readFileSync(new URL("../testing/schema.sql", import.meta.url), "utf8"));
  await admin.query(`CREATE ROLE ${ROLE} LOGIN PASSWORD '${ROLE_PW}' NOSUPERUSER NOBYPASSRLS`);
  await admin.query(`GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO ${ROLE}`);
  const u = new URL(container.getConnectionUri());
  u.username = ROLE; u.password = ROLE_PW;
  db = TenantDb.create(u.toString(), 8);
}, 180_000);

afterAll(async () => {
  await db?.close(); await admin?.end(); await container?.stop();
});

/** One tenant, telegram channel, conversation and one outbound AI reply in the given status. */
async function seedMessage(status: string): Promise<{ tenantId: string; messageId: string }> {
  const tenantId = randomUUID();
  const [client, channel, conv, messageId] = Array.from({ length: 4 }, () => randomUUID());
  const q = (sql: string, p: unknown[]) => admin.query(sql, p);
  await q("INSERT INTO beauty_clients (id, tenant_id, full_name) VALUES ($1,$2,'Anna')", [client, tenantId]);
  await q("INSERT INTO beauty_channels (id, tenant_id, type, name, credentials_encrypted, is_active) VALUES ($1,$2,'telegram','ch',$3,true)",
    [channel, tenantId, writeCredentials(randomBytes(32), { token: "TG-TOKEN-123", webhookSecret: "s" })]);
  await q("INSERT INTO beauty_conversations (id, tenant_id, channel_id, client_id, external_chat_id) VALUES ($1,$2,$3,$4,'555')",
    [conv, tenantId, channel, client]);
  await q(`INSERT INTO beauty_messages (id, tenant_id, conversation_id, direction, sender_type, body, status, idempotency_key, sent_at)
           VALUES ($1,$2,$3,'outbound','ai','hello',$4,$5,$6)`, [messageId, tenantId, conv, status, `ai-reply:${conv}:${messageId}`, NOW]);
  return { tenantId, messageId };
}

const statusOf = async (id: string) => (await admin.query("SELECT status, attempts FROM beauty_messages WHERE id=$1", [id])).rows[0];

function makeDeps(sender: (m: StoredMessage) => Promise<void>): { deps: BeautyDeps; sent: StoredMessage[] } {
  const sent: StoredMessage[] = [];
  const deps: BeautyDeps = {
    now: () => NOW,
    data: new PgBeautyData(db, () => NOW),
    sender: { send: async (m) => { await sender(m); sent.push(m); } },
    idempotency: { claim: async () => true, complete: async () => undefined, release: async () => undefined },
    log: { append: async () => undefined },
    queue: new PgQueuePort(),
  };
  return { deps, sent };
}

describe("outbox claim pending -> sending (PostgreSQL CAS)", () => {
  it("claimForSend_two-parallel-claims_exactly-one-wins_and-row-becomes-sending", async () => {
    const { tenantId, messageId } = await seedMessage("pending");
    const { deps } = makeDeps(async () => undefined);
    const results = await withTenant(tenantId, () => Promise.all([deps.data.claimForSend(messageId), deps.data.claimForSend(messageId)]));
    expect(results.filter(Boolean)).toHaveLength(1);
    expect(await statusOf(messageId)).toMatchObject({ status: "sending" });
  });

  it.each(["sending", "sent", "failed", "draft"])("claimForSend_row-in-%s_is-not-claimed_and-status-is-unchanged", async (status) => {
    const { tenantId, messageId } = await seedMessage(status);
    const { deps } = makeDeps(async () => undefined);
    expect(await withTenant(tenantId, () => deps.data.claimForSend(messageId))).toBe(false);
    expect(await statusOf(messageId)).toMatchObject({ status });
  });

  it("processOutbox_row-already-taken-by-dotnet-sweeper_is-skipped_without-sending", async () => {
    const { tenantId, messageId } = await seedMessage("sending");
    const { deps, sent } = makeDeps(async () => undefined);
    expect(await withTenant(tenantId, () => processOutbox(deps, { messageId }))).toBe("skipped");
    expect(sent).toHaveLength(0);
    expect(await statusOf(messageId)).toMatchObject({ status: "sending" });
  });

  it("processOutbox_two-workers-on-one-pending-row_deliver-exactly-once", async () => {
    const { tenantId, messageId } = await seedMessage("pending");
    const a = makeDeps(async () => { await new Promise((r) => setTimeout(r, 50)); });
    const b = makeDeps(async () => { await new Promise((r) => setTimeout(r, 50)); });
    const outcomes = await withTenant(tenantId, () => Promise.all([processOutbox(a.deps, { messageId }), processOutbox(b.deps, { messageId })]));
    expect(outcomes.slice().sort()).toEqual(["sent", "skipped"]);
    expect(a.sent.length + b.sent.length).toBe(1);
    expect(await statusOf(messageId)).toMatchObject({ status: "sent" });
  });

  it("processOutbox_failed-attempt_returns-row-to-pending_and-retry-delivers", async () => {
    const { tenantId, messageId } = await seedMessage("pending");
    let fail = true;
    const { deps, sent } = makeDeps(async () => { if (fail) throw new Error("channel down"); });
    await expect(withTenant(tenantId, () => processOutbox(deps, { messageId }, 0))).rejects.toThrow("channel down");
    expect(await statusOf(messageId)).toMatchObject({ status: "pending", attempts: 1 });
    fail = false;
    expect(await withTenant(tenantId, () => processOutbox(deps, { messageId }, 1))).toBe("sent");
    expect(sent).toHaveLength(1);
    expect(await statusOf(messageId)).toMatchObject({ status: "sent" });
  });
});
