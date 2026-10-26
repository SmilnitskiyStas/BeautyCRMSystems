import { randomBytes, randomUUID } from "node:crypto";
import { readFileSync } from "node:fs";
import pg from "pg";
import { PostgreSqlContainer, type StartedPostgreSqlContainer } from "@testcontainers/postgresql";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { writeCredentials } from "../adapters/channel-crypto";
import { PgBeautyData } from "../adapters/pg-data";
import { PgPollStore } from "../adapters/pg-poll-store";
import { PgAdvisoryIdempotency, PgQueuePort, type Logger } from "../adapters/pg-support";
import { TenantDb } from "../db/tenant-db";
import { StaticTenantDirectory } from "../db/tenants";
import type { BeautyDeps, StoredMessage } from "../ports";
import { pollOutbox, pollReviews, pollWinback, type PollContext } from "../scheduler/pollers";

/** TASK-760 (security review M2, L7): review/winback respect STOP / preference none / the flag; poller logs carry no personal data. */
const NOW = new Date("2026-03-10T10:00:00Z"); // Kyiv 12:00
const KEY = randomBytes(32);
const ROLE = "beauty_worker_optout";
const NAME = "Іванна Тестова";
const CHAT = "777000111";
const PHONE = "+380501234567";

let container: StartedPostgreSqlContainer;
let admin: pg.Pool;
let db: TenantDb;
let lockPool: pg.Pool;

beforeAll(async () => {
  container = await new PostgreSqlContainer("postgres:16-alpine").start();
  admin = new pg.Pool({ connectionString: container.getConnectionUri() });
  await admin.query(readFileSync(new URL("../testing/schema.sql", import.meta.url), "utf8"));
  await admin.query(`CREATE ROLE ${ROLE} LOGIN PASSWORD 'pw' NOSUPERUSER NOBYPASSRLS`);
  await admin.query(`GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO ${ROLE}`);
  const u = new URL(container.getConnectionUri());
  u.username = ROLE; u.password = "pw";
  db = TenantDb.create(u.toString(), 8);
  lockPool = new pg.Pool({ connectionString: u.toString(), max: 8 });
}, 180_000);

afterAll(async () => {
  await lockPool?.end(); await db?.close(); await admin?.end(); await container?.stop();
});

interface Opts { completed?: "recent" | "old"; consent?: boolean; unsubscribed?: boolean; pref?: "none" | "telegram" }
async function seed(o: Opts) {
  const tenantId = randomUUID();
  const [loc, spec, svc, client, channel, conv] = Array.from({ length: 6 }, () => randomUUID());
  const q = (sql: string, p: unknown[]) => admin.query(sql, p);
  await q("INSERT INTO beauty_locations (id, tenant_id, name, timezone, is_active) VALUES ($1,$2,'L','Europe/Kyiv',true)", [loc, tenantId]);
  await q("INSERT INTO beauty_specialists (id, tenant_id, full_name, is_active) VALUES ($1,$2,'S',true)", [spec, tenantId]);
  await q("INSERT INTO beauty_services (id, tenant_id, name, duration_minutes, is_active) VALUES ($1,$2,'Svc',60,true)", [svc, tenantId]);
  await q("INSERT INTO beauty_clients (id, tenant_id, full_name, phone, marketing_consent, unsubscribed) VALUES ($1,$2,$3,$4,$5,$6)",
    [client, tenantId, NAME, PHONE, o.consent ?? true, o.unsubscribed ?? false]);
  const starts = o.completed === "old" ? new Date("2025-12-01T10:00:00Z") : new Date(NOW.getTime() - 3_600_000);
  await q(`INSERT INTO beauty_appointments (tenant_id, location_id, specialist_id, service_id, client_id, starts_at, duration_minutes,
             status, source, price_original, price_final, reminder_option)
           VALUES ($1,$2,$3,$4,$5,$6,60,'completed','admin',100,100,'none')`, [tenantId, loc, spec, svc, client, starts]);
  await q("INSERT INTO beauty_channels (id, tenant_id, type, name, credentials_encrypted, is_active) VALUES ($1,$2,'telegram','ch',$3,true)",
    [channel, tenantId, writeCredentials(KEY, { token: "TG-TOKEN", webhookSecret: "s" })]);
  await q("INSERT INTO beauty_conversations (id, tenant_id, channel_id, client_id, external_chat_id) VALUES ($1,$2,$3,$4,$5)", [conv, tenantId, channel, client, CHAT]);
  if (o.pref === "none") {
    await q("INSERT INTO beauty_client_contact_preferences (tenant_id, client_id, method, offsets_minutes, source) VALUES ($1,$2,'none','{}','stop')", [tenantId, client]);
  } else if (o.pref === "telegram") {
    await q("INSERT INTO beauty_client_contact_preferences (tenant_id, client_id, method, offsets_minutes, telegram_conversation_id, source) VALUES ($1,$2,'telegram','{120}',$3,'chat')", [tenantId, client, conv]);
  }
  return { tenantId, clientId: client };
}

function makeCtx(tenantIds: string[], extra: Partial<PollContext> = {}, store: PgPollStore = new PgPollStore(db)) {
  const sent: StoredMessage[] = [];
  const lines: string[] = [];
  const logger: Logger = {
    info: (m, f) => { lines.push(JSON.stringify({ m, ...f })); },
    error: (m, f) => { lines.push(JSON.stringify({ m, ...f })); },
  };
  const now = () => NOW;
  const deps: BeautyDeps = {
    now, data: new PgBeautyData(db, now),
    sender: { send: async (m) => { sent.push(m); } },
    idempotency: new PgAdvisoryIdempotency(lockPool),
    log: { append: async () => undefined },
    queue: new PgQueuePort(),
  };
  const ctx: PollContext = { deps, store, tenants: new StaticTenantDirectory(tenantIds), logger, backoffMs: 0, ...extra };
  return { ctx, sent, lines };
}
const count = async (tenantId: string) =>
  (await admin.query("SELECT count(*)::int AS n FROM beauty_messages WHERE tenant_id=$1", [tenantId])).rows[0].n as number;
const STOP_PREF = "INSERT INTO beauty_client_contact_preferences (tenant_id, client_id, method, offsets_minutes, source) VALUES ($1,$2,'none','{}','stop')";

describe("review and winback respect opt-out (M2)", () => {
  it("review_not-sent_to-unsubscribed-or-preference-none_but-sent-to-others", async () => {
    const ok = await seed({});
    const noConsentOk = await seed({ consent: false }); // review is not marketing: consent is not required
    const prefTelegram = await seed({ pref: "telegram" });
    const unsub = await seed({ unsubscribed: true });
    const none = await seed({ pref: "none" });
    const ids = [ok, noConsentOk, prefTelegram, unsub, none].map((s) => s.tenantId);
    const { ctx, sent } = makeCtx(ids);
    expect(await pollReviews(ctx)).toEqual({ processed: 3, errors: 0 });
    await pollOutbox(ctx);
    expect(sent.map((m) => m.tenantId).sort()).toEqual([ok, noConsentOk, prefTelegram].map((s) => s.tenantId).sort());
    expect(await count(unsub.tenantId)).toBe(0);
    expect(await count(none.tenantId)).toBe(0);
  });

  it("review_queued-then-stop_is-not-delivered_by-outbox", async () => {
    const s = await seed({});
    const { ctx, sent } = makeCtx([s.tenantId]);
    await pollReviews(ctx);
    await admin.query(STOP_PREF, [s.tenantId, s.clientId]);
    await pollOutbox(ctx);
    expect(sent).toHaveLength(0);
    expect((await admin.query("SELECT status, last_error FROM beauty_messages WHERE tenant_id=$1", [s.tenantId])).rows)
      .toEqual([{ status: "failed", last_error: "recipient unsubscribed" }]);
  });

  it("winback_not-sent_to-preference-none_or-unsubscribed_but-sent-to-consented", async () => {
    const ok = await seed({ completed: "old" });
    const none = await seed({ completed: "old", pref: "none" });
    const unsub = await seed({ completed: "old", unsubscribed: true });
    const noConsent = await seed({ completed: "old", consent: false });
    const ids = [ok, none, unsub, noConsent].map((s) => s.tenantId);
    const { ctx, sent } = makeCtx(ids);
    await pollWinback(ctx); await pollOutbox(ctx);
    expect(sent.map((m) => m.tenantId)).toEqual([ok.tenantId]);
    for (const s of [none, unsub, noConsent]) expect(await count(s.tenantId)).toBe(0);
  });

  it("winback_queued-then-preference-none_is-not-delivered", async () => {
    const s = await seed({ completed: "old" });
    const { ctx, sent } = makeCtx([s.tenantId]);
    await pollWinback(ctx);
    await admin.query(STOP_PREF, [s.tenantId, s.clientId]);
    await pollOutbox(ctx);
    expect(sent).toHaveLength(0);
  });

  it("pollReviews_does-nothing_when-flag-off_and-works-when-on", async () => {
    const s = await seed({});
    const off = makeCtx([s.tenantId], { reviewsEnabled: false });
    expect(await pollReviews(off.ctx)).toEqual({ processed: 0, errors: 0 });
    await pollOutbox(off.ctx);
    expect(off.sent).toHaveLength(0);
    expect(await count(s.tenantId)).toBe(0);
    const on = makeCtx([s.tenantId], { reviewsEnabled: true });
    expect(await pollReviews(on.ctx)).toEqual({ processed: 1, errors: 0 });
  });
});

describe("poller logs carry no personal data (L7)", () => {
  const leak = new Error(`insert failed: Key (full_name)=(${NAME}) phone ${PHONE} text "Дякуємо за візит" chat ${CHAT}`);
  const assertClean = (lines: string[]) => {
    const all = lines.join("\n");
    expect(all).toContain("errorCode");
    for (const v of [NAME, PHONE, "Дякуємо", CHAT, "Key (full_name)"]) expect(all).not.toContain(v);
  };

  it("tenant-level_failures_log-code-only", async () => {
    const s = await seed({});
    const store = new PgPollStore(db);
    store.listCompletedWithoutReview = async () => { throw leak; };
    store.listPendingOutbox = async () => { throw leak; };
    const { ctx, lines } = makeCtx([s.tenantId], {}, store);
    expect((await pollReviews(ctx)).errors).toBe(1);
    expect((await pollOutbox(ctx)).errors).toBe(1);
    assertClean(lines);
  });

  it("item-level_failures_log-code-only", async () => {
    const s = await seed({});
    const store = new PgPollStore(db);
    store.listCompletedWithoutReview = async () => ["00000000-0000-0000-0000-000000000001"];
    const { ctx, lines } = makeCtx([s.tenantId], {}, store);
    ctx.deps.data.getAppointment = async () => { throw leak; };
    expect(await pollReviews(ctx)).toEqual({ processed: 0, errors: 1 });
    await admin.query(
      "INSERT INTO beauty_messages (tenant_id, conversation_id, direction, sender_type, body, status) SELECT $1, id, 'outbound','staff','Привіт','pending' FROM beauty_conversations WHERE tenant_id=$1",
      [s.tenantId]);
    ctx.deps.sender = { send: async () => { throw leak; } };
    expect((await pollOutbox(ctx)).errors).toBe(1);
    const poller = lines.filter((l) => l.includes("outbox send failed") || l.includes("review request failed"));
    expect(poller).toHaveLength(2);
    assertClean(poller);
  });
});
