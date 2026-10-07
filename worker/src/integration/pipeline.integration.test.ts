import { randomBytes, randomUUID } from "node:crypto";
import { readFileSync } from "node:fs";
import pg from "pg";
import { PostgreSqlContainer, type StartedPostgreSqlContainer } from "@testcontainers/postgresql";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { HttpChannelSender } from "../adapters/channel-sender";
import { writeCredentials } from "../adapters/channel-crypto";
import { PgBeautyData } from "../adapters/pg-data";
import { PgPollStore } from "../adapters/pg-poll-store";
import { PgAdvisoryIdempotency, PgQueuePort, type Logger } from "../adapters/pg-support";
import { TenantDb, withTenant } from "../db/tenant-db";
import { PgFunctionTenantDirectory, StaticTenantDirectory } from "../db/tenants";
import type { BeautyDeps, NotificationLogEntry, StoredMessage } from "../ports";
import { pollOutbox, pollReminders, pollWinback, type PollContext } from "../scheduler/pollers";

const NOW = new Date("2026-03-10T10:00:00Z"); // Kyiv 12:00 (inside the 09-20 window)
const NIGHT = new Date("2026-03-10T22:00:00Z"); // Kyiv 00:00 (outside)
const KEY = randomBytes(32);
const silent: Logger = { info: () => undefined, error: () => undefined };
const ROLE = "beauty_worker_test";
const ROLE_PW = "worker_pw";

let container: StartedPostgreSqlContainer;
let admin: pg.Pool;
let db: TenantDb;
let lockPool: pg.Pool;

beforeAll(async () => {
  container = await new PostgreSqlContainer("postgres:16-alpine").start();
  admin = new pg.Pool({ connectionString: container.getConnectionUri() });
  await admin.query(readFileSync(new URL("../testing/schema.sql", import.meta.url), "utf8"));
  await admin.query(`CREATE ROLE ${ROLE} LOGIN PASSWORD '${ROLE_PW}' NOSUPERUSER NOBYPASSRLS`);
  await admin.query(`GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO ${ROLE}`);
  const u = new URL(container.getConnectionUri());
  u.username = ROLE; u.password = ROLE_PW;
  db = TenantDb.create(u.toString(), 8);
  lockPool = new pg.Pool({ connectionString: u.toString(), max: 8 });
}, 180_000);

afterAll(async () => {
  await lockPool?.end(); await db?.close(); await admin?.end(); await container?.stop();
});

interface Seeded { tenantId: string; clientId: string; apptId: string; reminderId: string; channelId: string; convId: string }

/** Seeds one tenant (as the table owner, i.e. like the .NET backend would write): booking + reminder + telegram chat. */
async function seed(o: {
  starts?: Date; reminderOption?: string; apptStatus?: string; consent?: boolean; unsubscribed?: boolean;
  reminderAt?: Date | null; channelType?: string; completedAgo?: Date; token?: string;
} = {}): Promise<Seeded> {
  const tenantId = randomUUID();
  const [loc, spec, svc, client, appt, channel, conv] = Array.from({ length: 7 }, () => randomUUID());
  const q = (sql: string, p: unknown[]) => admin.query(sql, p);
  await q("INSERT INTO beauty_locations (id, tenant_id, name, timezone, is_active) VALUES ($1,$2,'L','Europe/Kyiv',true)", [loc, tenantId]);
  await q("INSERT INTO beauty_specialists (id, tenant_id, full_name, is_active) VALUES ($1,$2,'S',true)", [spec, tenantId]);
  await q("INSERT INTO beauty_services (id, tenant_id, name, duration_minutes, is_active) VALUES ($1,$2,'Svc',60,true)", [svc, tenantId]);
  await q("INSERT INTO beauty_clients (id, tenant_id, full_name, marketing_consent, unsubscribed) VALUES ($1,$2,'Anna',$3,$4)",
    [client, tenantId, o.consent ?? false, o.unsubscribed ?? false]);
  const starts = o.starts ?? new Date(NOW.getTime() + 2 * 3_600_000);
  await q(`INSERT INTO beauty_appointments (id, tenant_id, location_id, specialist_id, service_id, client_id, starts_at, duration_minutes,
             status, source, price_original, price_final, reminder_option)
           VALUES ($1,$2,$3,$4,$5,$6,$7,60,$8,'online',100,100,$9)`,
    [appt, tenantId, loc, spec, svc, client, starts, o.apptStatus ?? "confirmed", o.reminderOption ?? "2h"]);
  if (o.completedAgo) {
    await q(`INSERT INTO beauty_appointments (tenant_id, location_id, specialist_id, service_id, client_id, starts_at, duration_minutes,
               status, source, price_original, price_final, reminder_option)
             VALUES ($1,$2,$3,$4,$5,$6,60,'completed','admin',100,100,'none')`, [tenantId, loc, spec, svc, client, o.completedAgo]);
  }
  const creds = writeCredentials(KEY, { token: o.token ?? "TG-TOKEN-123", webhookSecret: "s" });
  await q("INSERT INTO beauty_channels (id, tenant_id, type, name, credentials_encrypted, is_active) VALUES ($1,$2,$3,'ch',$4,true)",
    [channel, tenantId, o.channelType ?? "telegram", creds]);
  await q("INSERT INTO beauty_conversations (id, tenant_id, channel_id, client_id, external_chat_id) VALUES ($1,$2,$3,$4,'555')",
    [conv, tenantId, channel, client]);
  const reminderId = randomUUID();
  const reminderAt = o.reminderAt === undefined ? new Date(starts.getTime() - 2 * 3_600_000) : o.reminderAt;
  if (reminderAt) {
    await q("INSERT INTO beauty_reminders (id, tenant_id, appointment_id, scheduled_at) VALUES ($1,$2,$3,$4)", [reminderId, tenantId, appt, reminderAt]);
  }
  return { tenantId, clientId: client, apptId: appt, reminderId, channelId: channel, convId: conv };
}

function makeCtx(tenantIds: string[], opts: { now?: () => Date; sender?: (m: StoredMessage) => Promise<void> } = {}) {
  const sent: StoredMessage[] = [];
  const logs: NotificationLogEntry[] = [];
  const now = opts.now ?? (() => NOW);
  const deps: BeautyDeps = {
    now,
    data: new PgBeautyData(db, now),
    sender: { send: async (m) => { if (opts.sender) await opts.sender(m); sent.push(m); } },
    idempotency: new PgAdvisoryIdempotency(lockPool),
    log: { append: async (e) => { logs.push(e); } },
    queue: new PgQueuePort(),
  };
  const ctx: PollContext = {
    deps, store: new PgPollStore(db), tenants: new StaticTenantDirectory(tenantIds), logger: silent, backoffMs: 0,
  };
  return { ctx, sent, logs };
}

const row = async (sql: string, p: unknown[] = []) => (await admin.query(sql, p)).rows;

describe("reminder -> outbox -> channel (PostgreSQL, RLS enforced)", () => {
  it("worker_sends-reminder-exactly-once_and-rerun-does-not-duplicate", async () => {
    const s = await seed();
    const { ctx, sent } = makeCtx([s.tenantId]);

    expect(await pollReminders(ctx)).toEqual({ processed: 1, errors: 0 });
    expect(await row("SELECT status, attempts, idempotency_key FROM beauty_messages WHERE tenant_id=$1", [s.tenantId]))
      .toEqual([{ status: "pending", attempts: 0, idempotency_key: `reminder:${s.reminderId}` }]);
    await pollOutbox(ctx);
    expect(sent).toHaveLength(1);
    expect(sent[0]).toMatchObject({ channel: "telegram", address: "555", kind: "reminder", clientId: s.clientId });

    // second full run (and a concurrent pair) must not send again
    await pollReminders(ctx); await pollOutbox(ctx);
    await Promise.all([pollReminders(ctx), pollOutbox(ctx), pollOutbox(ctx)]);
    expect(sent).toHaveLength(1);
    expect(await row("SELECT count(*)::int AS n FROM beauty_messages WHERE tenant_id=$1", [s.tenantId])).toEqual([{ n: 1 }]);
    const [m] = await row("SELECT status, sent_at, last_error FROM beauty_messages WHERE tenant_id=$1", [s.tenantId]);
    expect(m.status).toBe("sent"); expect(m.sent_at).not.toBeNull(); expect(m.last_error).toBeNull();
    expect(await row("SELECT status, sent_at FROM beauty_reminders WHERE id=$1", [s.reminderId])).toMatchObject([{ status: "sent" }]);
  });

  it("two-workers_race_sends-once", async () => {
    const s = await seed();
    const a = makeCtx([s.tenantId]);
    const b = makeCtx([s.tenantId]);
    await pollReminders(a.ctx);
    await Promise.all([pollOutbox(a.ctx), pollOutbox(b.ctx)]);
    expect(a.sent.length + b.sent.length).toBe(1);
  });

  it("outbox_retries-3-times-total_then-fails-and-records-error", async () => {
    const s = await seed();
    const { ctx, sent, logs } = makeCtx([s.tenantId], { sender: async () => { throw new Error("channel down"); } });
    await pollReminders(ctx);
    const results = [];
    for (let i = 0; i < 6; i++) results.push(await pollOutbox(ctx));
    expect(results.slice(0, 3).map((r) => r.errors)).toEqual([1, 1, 1]);
    expect(results.slice(3).map((r) => r.processed + r.errors)).toEqual([0, 0, 0]); // nothing left to try
    expect(sent).toHaveLength(0);
    const [m] = await row("SELECT status, attempts, last_error FROM beauty_messages WHERE tenant_id=$1", [s.tenantId]);
    expect(m).toEqual({ status: "failed", attempts: 3, last_error: "channel down" });
    expect(logs.filter((l) => l.status === "retry")).toHaveLength(2);
    expect(logs.filter((l) => l.status === "failed")).toHaveLength(1);
    expect(await row("SELECT status, error FROM beauty_reminders WHERE id=$1", [s.reminderId])).toEqual([{ status: "failed", error: "channel down" }]);
  });

  it("outbox_succeeds-on-retry_after-transient-failure", async () => {
    const s = await seed();
    let calls = 0;
    const { ctx, sent } = makeCtx([s.tenantId], { sender: async () => { if (++calls < 3) throw new Error("flaky"); } });
    await pollReminders(ctx);
    for (let i = 0; i < 5; i++) await pollOutbox(ctx);
    expect(sent).toHaveLength(1);
    expect(await row("SELECT status, attempts FROM beauty_messages WHERE tenant_id=$1", [s.tenantId])).toEqual([{ status: "sent", attempts: 2 }]);
  });

  it("backoff_delays-retry_until-interval-passes", async () => {
    const s = await seed();
    let clock = NOW;
    const { ctx, sent } = makeCtx([s.tenantId], { now: () => clock, sender: async () => { throw new Error("x"); } });
    ctx.backoffMs = 5_000;
    await pollReminders(ctx); await pollOutbox(ctx);
    await pollOutbox(ctx); // too early: 0 s since failure
    expect(await row("SELECT attempts FROM beauty_messages WHERE tenant_id=$1", [s.tenantId])).toEqual([{ attempts: 1 }]);
    clock = new Date(NOW.getTime() + 6_000);
    await pollOutbox(ctx);
    expect(await row("SELECT attempts FROM beauty_messages WHERE tenant_id=$1", [s.tenantId])).toEqual([{ attempts: 2 }]);
    expect(sent).toHaveLength(0);
  });

  it("reminder_is-closed-without-sending_when-appointment-cancelled-or-option-none-or-visit-passed", async () => {
    const cancelled = await seed({ apptStatus: "cancelled" });
    const none = await seed({ reminderOption: "none" });
    const passed = await seed({ starts: new Date(NOW.getTime() - 3_600_000), reminderAt: new Date(NOW.getTime() - 3 * 3_600_000) });
    const future = await seed({ reminderAt: new Date(NOW.getTime() + 3_600_000) }); // not due yet
    const { ctx, sent } = makeCtx([cancelled.tenantId, none.tenantId, passed.tenantId, future.tenantId]);
    await pollReminders(ctx); await pollOutbox(ctx);
    expect(sent).toHaveLength(0);
    const st = async (s: Seeded) => (await row("SELECT status FROM beauty_reminders WHERE id=$1", [s.reminderId]))[0].status;
    expect([await st(cancelled), await st(none), await st(passed), await st(future)]).toEqual(["cancelled", "cancelled", "failed", "scheduled"]);
  });

  it("tenant-isolation_worker-only-touches-listed-tenants_and-role-sees-nothing-without-tenant", async () => {
    const a = await seed(); const b = await seed();
    const { ctx, sent } = makeCtx([a.tenantId]);
    await pollReminders(ctx); await pollOutbox(ctx);
    expect(sent.map((m) => m.tenantId)).toEqual([a.tenantId]);
    expect(await row("SELECT status FROM beauty_reminders WHERE id=$1", [b.reminderId])).toEqual([{ status: "scheduled" }]);

    // role has no BYPASSRLS: without a tenant nothing is visible, and a foreign tenant's row is invisible
    expect((await db.pool.query("SELECT count(*)::int AS n FROM beauty_appointments")).rows[0].n).toBe(0);
    const data = new PgBeautyData(db);
    expect(await withTenant(a.tenantId, () => data.getAppointment(b.apptId))).toBeNull();
    expect(await withTenant(a.tenantId, () => data.getAppointment(a.apptId))).not.toBeNull();
    await expect(data.getAppointment(a.apptId)).rejects.toThrow(/No tenant in scope/);
  });

  it("assertRlsEnforced_passes-for-app-role_and-rejects-superuser", async () => {
    await expect(db.assertRlsEnforced()).resolves.toBeUndefined();
    await expect(new TenantDb(admin).assertRlsEnforced()).rejects.toThrow(/BYPASSRLS/);
  });

  it("winback_creates-message-once_only-for-consented_and-sends-only-inside-window", async () => {
    const lapsed = await seed({ consent: true, starts: new Date("2025-12-01T10:00:00Z"), apptStatus: "completed", reminderAt: null });
    const noConsent = await seed({ consent: false, starts: new Date("2025-12-01T10:00:00Z"), apptStatus: "completed", reminderAt: null });
    const unsub = await seed({ consent: true, unsubscribed: true, starts: new Date("2025-12-01T10:00:00Z"), apptStatus: "completed", reminderAt: null });
    const ids = [lapsed.tenantId, noConsent.tenantId, unsub.tenantId];

    const night = makeCtx(ids, { now: () => NIGHT });
    await pollWinback(night.ctx); await pollWinback(night.ctx); // daily rerun = no duplicate
    await pollOutbox(night.ctx);
    expect(night.sent).toHaveLength(0); // 00:00 Kyiv: outside 09-20, stays pending
    expect(await row("SELECT status, idempotency_key FROM beauty_messages WHERE tenant_id = ANY($1)", [ids]))
      .toHaveLength(1);

    const day = makeCtx(ids, { now: () => NOW });
    await pollOutbox(day.ctx); await pollOutbox(day.ctx);
    expect(day.sent).toHaveLength(1);
    expect(day.sent[0]).toMatchObject({ tenantId: lapsed.tenantId, kind: "winback", clientId: lapsed.clientId });
  });

  it("outbound-messages-created-by-backend_are-sent-too_with-null-key", async () => {
    const s = await seed({ reminderAt: null });
    await admin.query(
      `INSERT INTO beauty_messages (tenant_id, conversation_id, direction, sender_type, body, status) VALUES ($1,$2,'outbound','staff','Hello','pending')`,
      [s.tenantId, s.convId]);
    await admin.query(
      `INSERT INTO beauty_messages (tenant_id, conversation_id, direction, sender_type, body, status) VALUES ($1,$2,'inbound','client','Hi','received')`,
      [s.tenantId, s.convId]);
    const { ctx, sent } = makeCtx([s.tenantId]);
    await pollOutbox(ctx); await pollOutbox(ctx);
    expect(sent).toHaveLength(1);
    expect(sent[0]).toMatchObject({ text: "Hello", kind: "message" });
    expect(await row("SELECT status FROM beauty_messages WHERE tenant_id=$1 ORDER BY direction", [s.tenantId]))
      .toEqual([{ status: "received" }, { status: "sent" }]); // inbound untouched
  });

  it("tenantDirectory_function_lists-tenants_when-db-provides-security-definer-fn", async () => {
    const s = await seed({ reminderAt: null });
    // test-only stand-in for the migration the DB owner would add (see open question)
    await admin.query(`CREATE OR REPLACE FUNCTION beauty_list_tenants() RETURNS TABLE(tenant_id uuid)
      LANGUAGE sql SECURITY DEFINER AS $$ SELECT DISTINCT l.tenant_id FROM beauty_locations l $$`);
    await admin.query(`GRANT EXECUTE ON FUNCTION beauty_list_tenants() TO ${ROLE}`);
    // SECURITY DEFINER owned by a superuser bypasses RLS; the app role itself still cannot
    expect(await new PgFunctionTenantDirectory(db.pool).listTenantIds()).toContain(s.tenantId);
  });
});

describe("HttpChannelSender (real DB lookup, mocked HTTP, AES-GCM credentials)", () => {
  const okFetch = (calls: { url: string; init: RequestInit }[], status = 200): typeof fetch =>
    (async (url: string, init: RequestInit) => { calls.push({ url, init }); return new Response("{}", { status }); }) as unknown as typeof fetch;
  const stored = async (s: Seeded, text = "hi"): Promise<StoredMessage> => {
    const [m] = await row(
      `INSERT INTO beauty_messages (tenant_id, conversation_id, direction, sender_type, body, status) VALUES ($1,$2,'outbound','staff',$3,'pending') RETURNING id`,
      [s.tenantId, s.convId, text]);
    return { id: m.id, tenantId: s.tenantId, clientId: s.clientId, channel: "telegram", address: "555", text, idempotencyKey: `msg:${m.id}`, kind: "message", status: "queued" };
  };

  it("telegram_posts-to-bot-api-with-decrypted-token", async () => {
    const s = await seed({ reminderAt: null, token: "123:ABC" });
    const calls: { url: string; init: RequestInit }[] = [];
    const sender = new HttpChannelSender(db, KEY, okFetch(calls), () => NOW);
    await withTenant(s.tenantId, async () => sender.send(await stored(s, "Привіт")));
    expect(calls[0]!.url).toBe("https://api.telegram.org/bot123:ABC/sendMessage");
    expect(JSON.parse(calls[0]!.init.body as string)).toEqual({ chat_id: "555", text: "Привіт" });
  });

  it("telegram_http-500-is-transient_and-400-permanent_and-error-hides-token", async () => {
    const s = await seed({ reminderAt: null, token: "SECRET-TOKEN" });
    const m = await withTenant(s.tenantId, () => stored(s));
    const run = (status: number) => withTenant(s.tenantId, () => new HttpChannelSender(db, KEY, okFetch([], status), () => NOW).send(m));
    await expect(run(500)).rejects.toMatchObject({ permanent: false, message: "Telegram sendMessage failed with HTTP 500" });
    await expect(run(400)).rejects.toMatchObject({ permanent: true });
    await run(500).catch((e: Error) => expect(e.message).not.toContain("SECRET-TOKEN"));
  });

  it("instagram_rejects-outside-24h-window-permanently_and-sends-inside", async () => {
    const s = await seed({ reminderAt: null, channelType: "instagram", token: "IGTOKEN" });
    const calls: { url: string; init: RequestInit }[] = [];
    const sender = new HttpChannelSender(db, KEY, okFetch(calls), () => NOW);
    const m = await withTenant(s.tenantId, () => stored(s));
    await expect(withTenant(s.tenantId, () => sender.send(m))).rejects.toMatchObject({ permanent: true });
    await admin.query(
      `INSERT INTO beauty_messages (tenant_id, conversation_id, direction, sender_type, body, status, sent_at) VALUES ($1,$2,'inbound','client','hi','received',$3)`,
      [s.tenantId, s.convId, new Date(NOW.getTime() - 3_600_000)]);
    await withTenant(s.tenantId, () => sender.send(m));
    expect(calls[0]!.url).toBe("https://graph.facebook.com/v21.0/me/messages");
    expect((calls[0]!.init.headers as Record<string, string>).Authorization).toBe("Bearer IGTOKEN");
  });

  it("permanent-channel-error_is-not-retried-3-times", async () => {
    const s = await seed({ reminderAt: null, channelType: "instagram" }); // no inbound -> window closed
    await admin.query(`INSERT INTO beauty_messages (tenant_id, conversation_id, direction, sender_type, body, status) VALUES ($1,$2,'outbound','staff','x','pending')`, [s.tenantId, s.convId]);
    const { ctx } = makeCtx([s.tenantId]);
    ctx.deps.sender = new HttpChannelSender(db, KEY, okFetch([]), () => NOW);
    await pollOutbox(ctx); await pollOutbox(ctx);
    expect(await row("SELECT status, attempts, last_error FROM beauty_messages WHERE tenant_id=$1", [s.tenantId]))
      .toEqual([{ status: "failed", attempts: 1, last_error: "Meta 24h messaging window is closed" }]);
  });

  it("wrong-encryption-key_fails-permanently-without-leaking", async () => {
    const s = await seed({ reminderAt: null });
    const m = await withTenant(s.tenantId, () => stored(s));
    const bad = new HttpChannelSender(db, randomBytes(32), okFetch([]), () => NOW);
    await expect(withTenant(s.tenantId, () => bad.send(m))).rejects.toMatchObject({ permanent: true });
  });
});
