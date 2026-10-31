import { randomBytes, randomUUID } from "node:crypto";
import { readFileSync } from "node:fs";
import pg from "pg";
import { PostgreSqlContainer, type StartedPostgreSqlContainer } from "@testcontainers/postgresql";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { HttpChannelSender } from "../adapters/channel-sender";
import { writeCredentials } from "../adapters/channel-crypto";
import { MockChannelSender } from "../adapters/mock-channel-sender";
import { PgBeautyData } from "../adapters/pg-data";
import { PgPollStore } from "../adapters/pg-poll-store";
import { PgAdvisoryIdempotency, PgQueuePort, LoggerNotificationLog, type Logger } from "../adapters/pg-support";
import { TenantDb, withTenant } from "../db/tenant-db";
import { StaticTenantDirectory } from "../db/tenants";
import type { BeautyDeps, NotificationLogEntry } from "../ports";
import { pollOutbox, pollReminders, type PollContext } from "../scheduler/pollers";

/**
 * TASK-751 (§23.7 p. 10, §23.13 tests 3 / 12 / 19, worker side): reply_markup from beauty_messages.actions,
 * message_id -> external_message_id, the reminder keyboard in beauty_chat_keyboards. Real PostgreSQL, role without
 * BYPASSRLS and with the runbook grants only (no UPDATE on beauty_chat_keyboards). Telegram is never called.
 */
const KEY = randomBytes(32);
const ROLE = "beauty_worker_buttons";
const HOUR = 3_600_000;
const T = (iso: string) => new Date(iso);

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
  await admin.query(`REVOKE UPDATE, DELETE ON beauty_chat_keyboards FROM ${ROLE}`); // runbook: worker = SELECT, INSERT only
  const u = new URL(container.getConnectionUri());
  u.username = ROLE; u.password = "pw";
  db = TenantDb.create(u.toString(), 8);
  lockPool = new pg.Pool({ connectionString: u.toString(), max: 8 });
}, 180_000);

afterAll(async () => {
  await lockPool?.end(); await db?.close(); await admin?.end(); await container?.stop();
});

const PHONE = "+380501234567";
const CHAT = "777000111";
const NAME = "Іванна Тестова";
const SERVICE = "Манікюр";
const TOKEN = "987654:SECRET-TOKEN";

interface Seed { tenantId: string; apptId: string; reminderId: string; convId: string | null }
interface SeedOpts {
  starts: Date; scheduledAt: Date; channel?: "telegram" | "instagram"; settings?: object | null; reminder?: boolean;
}

async function seed(o: SeedOpts): Promise<Seed> {
  const tenantId = randomUUID();
  const [loc, spec, svc, client, appt, channel, conv, reminderId] = Array.from({ length: 8 }, () => randomUUID());
  const q = (sql: string, p: unknown[]) => admin.query(sql, p);
  await q("INSERT INTO beauty_locations (id, tenant_id, name, address, timezone, is_active) VALUES ($1,$2,'Салон Лілія','вул. Хрещатик, 1','Europe/Kyiv',true)", [loc, tenantId]);
  await q("INSERT INTO beauty_specialists (id, tenant_id, full_name, is_active) VALUES ($1,$2,'S',true)", [spec, tenantId]);
  await q("INSERT INTO beauty_services (id, tenant_id, name, duration_minutes, is_active) VALUES ($1,$2,$3,60,true)", [svc, tenantId, SERVICE]);
  await q("INSERT INTO beauty_clients (id, tenant_id, full_name, phone) VALUES ($1,$2,$3,$4)", [client, tenantId, NAME, PHONE]);
  await q(`INSERT INTO beauty_appointments (id, tenant_id, location_id, specialist_id, service_id, client_id, starts_at, duration_minutes,
             status, source, price_original, price_final, reminder_option)
           VALUES ($1,$2,$3,$4,$5,$6,$7,60,'confirmed','online',100,100,'none')`, [appt, tenantId, loc, spec, svc, client, o.starts]);
  const settings = o.settings === undefined ? { assistant: { enabled: true } } : o.settings;
  await q("INSERT INTO beauty_channels (id, tenant_id, type, name, credentials_encrypted, is_active, settings) VALUES ($1,$2,$3,'ch',$4,true,$5)",
    [channel, tenantId, o.channel ?? "telegram", writeCredentials(KEY, { token: TOKEN, webhookSecret: "s" }), settings === null ? null : JSON.stringify(settings)]);
  await q("INSERT INTO beauty_conversations (id, tenant_id, channel_id, client_id, external_chat_id, last_message_at) VALUES ($1,$2,$3,$4,$5, now())", [conv, tenantId, channel, client, CHAT]);
  if (o.reminder !== false) {
    await q(`INSERT INTO beauty_reminders (id, tenant_id, appointment_id, scheduled_at, status, offset_minutes, method, planned_starts_at)
             VALUES ($1,$2,$3,$4,'scheduled',120,'auto',$5)`, [reminderId, tenantId, appt, o.scheduledAt, o.starts]);
  }
  return { tenantId, apptId: appt, reminderId, convId: conv };
}

interface CtxOpts { now: Date; fetch?: typeof fetch; buttons?: boolean }
function makeCtx(tenantIds: string[], o: CtxOpts) {
  const lines: string[] = [];
  const logger: Logger = {
    info: (m, f) => { lines.push(JSON.stringify({ m, ...f })); },
    error: (m, f) => { lines.push(JSON.stringify({ m, ...f })); },
  };
  const entries: NotificationLogEntry[] = [];
  const now = () => o.now;
  const sender = o.fetch ? new HttpChannelSender(db, KEY, o.fetch, now) : new MockChannelSender(logger);
  const deps: BeautyDeps = {
    now, data: new PgBeautyData(db, now), sender,
    idempotency: new PgAdvisoryIdempotency(lockPool),
    log: { append: async (e) => { entries.push(e); await new LoggerNotificationLog(logger).append(e); } },
    queue: new PgQueuePort(),
  };
  const ctx: PollContext = {
    deps, store: new PgPollStore(db), tenants: new StaticTenantDirectory(tenantIds), logger, backoffMs: 0,
    remindersEnabled: true, reminderButtonsEnabled: o.buttons,
  };
  return { ctx, lines, entries };
}

const rows = async (sql: string, p: unknown[] = []) => (await admin.query(sql, p)).rows;
const reminder = async (s: Seed) => (await rows("SELECT status FROM beauty_reminders WHERE id=$1", [s.reminderId]))[0];
const messages = (s: Seed) => rows(
  "SELECT id, body, status, actions, external_message_id FROM beauty_messages WHERE tenant_id=$1 AND direction='outbound' ORDER BY created_at", [s.tenantId]);
const keyboards = (s: Seed) => rows("SELECT * FROM beauty_chat_keyboards WHERE tenant_id=$1", [s.tenantId]);

// 2026-03-10: Kyiv = UTC+2, 12:00Z = 14:00 local (outside quiet hours).
const DAY = T("2026-03-10T12:00:00Z");
const visit = () => new Date(DAY.getTime() + 2 * HOUR);

interface Call { url: string; body: Record<string, any> }
function telegram(statuses: number[] = [200], messageId: number | null = 4242) {
  const calls: Call[] = [];
  const f = (async (url: string, init: RequestInit) => {
    calls.push({ url: String(url), body: JSON.parse(String(init.body)) });
    const st = statuses[Math.min(calls.length - 1, statuses.length - 1)]!;
    const body = st === 200 ? JSON.stringify(messageId === null ? { ok: true, result: {} } : { ok: true, result: { message_id: messageId } }) : "{}";
    return new Response(body, { status: st });
  }) as unknown as typeof fetch;
  return { f, calls };
}
const remind = async (s: Seed, buttons: boolean | undefined) => {
  const c = makeCtx([s.tenantId], { now: DAY, buttons });
  await pollReminders(c.ctx);
  return c;
};

describe("reminder keyboard (§23.7 p. 10)", () => {
  it("reminder_with-flag-and-channel-enabled_creates-message-actions-and-keyboard", async () => {
    const s = await seed({ starts: visit(), scheduledAt: DAY });
    await remind(s, true);
    const [m] = await messages(s);
    const [k] = await keyboards(s);
    expect(k.kind).toBe("appointment_actions");
    expect(k.token).toMatch(/^[A-Za-z0-9_-]{22}$/);
    expect(k.status).toBe("active");
    expect(k.message_id).toBe(m.id);
    expect(k.conversation_id).toBe(s.convId);
    expect(k.appointment_id).toBe(s.apptId);
    expect(new Date(k.expires_at).getTime()).toBe(visit().getTime());
    const flat = m.actions.rows.flat() as { label: string; data: string }[];
    expect(flat.map((b) => b.label)).toEqual(["✅ Підтверджую візит", "❌ Скасувати візит", "🔕 Не нагадувати"]);
    expect(flat.map((b) => b.data)).toEqual([`aa:${k.token}`, `ac:${k.token}`, "i:stop"]);
    for (const b of flat) expect(Buffer.byteLength(b.data, "utf8")).toBeLessThanOrEqual(64);
    expect(k.options.map((o: { verb: string }) => o.verb)).toEqual(["aa", "ac", "i:stop"]);
    expect(m.body).not.toContain("Підтверджую візит"); // the reminder text itself is unchanged
  });

  it("reminder_tokens_are-unique-per-reminder", async () => {
    const a = await seed({ starts: visit(), scheduledAt: DAY });
    const b = await seed({ starts: visit(), scheduledAt: DAY });
    await remind(a, true); await remind(b, true);
    expect((await keyboards(a))[0].token).not.toBe((await keyboards(b))[0].token);
  });

  it("reminder_flag-off_or-channel-buttons-false_or-assistant-off_or-no-settings_has-no-actions-and-no-keyboard", async () => {
    const off = await seed({ starts: visit(), scheduledAt: DAY });
    await remind(off, false);
    const noBtn = await seed({ starts: visit(), scheduledAt: DAY, settings: { assistant: { enabled: true, buttons: false } } });
    await remind(noBtn, true);
    const noAssistant = await seed({ starts: visit(), scheduledAt: DAY, settings: { assistant: { enabled: false } } });
    await remind(noAssistant, true);
    const noSettings = await seed({ starts: visit(), scheduledAt: DAY, settings: null });
    await remind(noSettings, true);
    for (const s of [off, noBtn, noAssistant, noSettings]) {
      const ms = await messages(s);
      expect(ms).toHaveLength(1);
      expect(ms[0].actions).toBeNull();
      expect(await keyboards(s)).toHaveLength(0);
    }
  });

  it("reminder_poller-rerun_creates-one-message-and-one-keyboard", async () => {
    const s = await seed({ starts: visit(), scheduledAt: DAY });
    const c = makeCtx([s.tenantId], { now: DAY, buttons: true });
    await pollReminders(c.ctx);
    await pollReminders(c.ctx);
    expect(await messages(s)).toHaveLength(1);
    expect(await keyboards(s)).toHaveLength(1);
  });

  it("reminder_closed-concurrently_rolls-the-keyboard-back-with-the-message", async () => {
    const s = await seed({ starts: visit(), scheduledAt: DAY });
    await admin.query("UPDATE beauty_reminders SET status='cancelled' WHERE id=$1", [s.reminderId]);
    const res = await withTenant(s.tenantId, () => new PgPollStore(db).enqueueReminderMessage(
      s.reminderId, s.convId!, "x", DAY, { appointmentId: s.apptId, expiresAt: visit() }));
    expect(res.result).toBe("closed");
    expect(await messages(s)).toHaveLength(0);
    expect(await keyboards(s)).toHaveLength(0);
  });

  it("reminder_quiet-hours_defers-without-message-or-keyboard", async () => {
    const night = T("2026-03-10T21:30:00Z"); // 23:30 Kyiv
    const s = await seed({ starts: new Date(night.getTime() + 12 * HOUR), scheduledAt: night });
    await pollReminders(makeCtx([s.tenantId], { now: night, buttons: true }).ctx);
    expect(await messages(s)).toHaveLength(0);
    expect(await keyboards(s)).toHaveLength(0);
    expect((await reminder(s)).status).toBe("scheduled");
  });

  it("reminder_dst-day_local-time-text-and-keyboard", async () => {
    // 2026-03-29: Kyiv moves to UTC+3 at 03:00 local; 09:00Z = 12:00 local, visit 11:00Z = 14:00 local.
    const now = T("2026-03-29T09:00:00Z");
    const s = await seed({ starts: new Date(now.getTime() + 2 * HOUR), scheduledAt: now });
    await pollReminders(makeCtx([s.tenantId], { now, buttons: true }).ctx);
    const [m] = await messages(s);
    expect(m.body).toContain("14:00");
    expect(await keyboards(s)).toHaveLength(1);
  });
});

describe("outbox: reply_markup and message_id (§23.4)", () => {
  it("send_telegram_posts-exact-inline_keyboard-and-stores-message_id", async () => {
    const s = await seed({ starts: visit(), scheduledAt: DAY });
    await remind(s, true);
    const [m0] = await messages(s);
    const t = telegram([200], 4242);
    await pollOutbox(makeCtx([s.tenantId], { now: DAY, fetch: t.f }).ctx);
    expect(t.calls).toHaveLength(1);
    expect(t.calls[0]!.body.reply_markup).toEqual({
      inline_keyboard: m0.actions.rows.map((r: { label: string; data: string }[]) => r.map((b) => ({ text: b.label, callback_data: b.data }))),
    });
    expect(t.calls[0]!.body.parse_mode).toBeUndefined();
    const [m] = await messages(s);
    expect(m.status).toBe("sent");
    expect(m.external_message_id).toBe("4242");
  });

  it("send_retry-after-failure_sends-the-same-keyboard-and-keeps-actions", async () => {
    const s = await seed({ starts: visit(), scheduledAt: DAY });
    await remind(s, true);
    const before = (await messages(s))[0];
    const t = telegram([500, 200], 77);
    const c = makeCtx([s.tenantId], { now: DAY, fetch: t.f });
    await pollOutbox(c.ctx); // attempt 1: HTTP 500
    expect((await messages(s))[0].status).toBe("pending");
    expect((await messages(s))[0].external_message_id).toBeNull();
    await pollOutbox(c.ctx); // attempt 2
    expect(t.calls).toHaveLength(2);
    expect(t.calls[1]!.body.reply_markup).toEqual(t.calls[0]!.body.reply_markup);
    const after = (await messages(s))[0];
    expect(after.actions).toEqual(before.actions);
    expect(after.external_message_id).toBe("77");
    expect(await keyboards(s)).toHaveLength(1);
  });

  it("send_already-sent_is-not-resent-and-external-id-stays", async () => {
    const s = await seed({ starts: visit(), scheduledAt: DAY });
    await remind(s, true);
    const t = telegram([200], 11);
    const c = makeCtx([s.tenantId], { now: DAY, fetch: t.f });
    await pollOutbox(c.ctx);
    await pollOutbox(c.ctx);
    expect(t.calls).toHaveLength(1);
    expect((await messages(s))[0].external_message_id).toBe("11");
  });

  it("send_without-actions_has-no-reply_markup_and-response-without-message_id-keeps-null", async () => {
    const s = await seed({ starts: visit(), scheduledAt: DAY });
    await remind(s, false);
    const t = telegram([200], null);
    await pollOutbox(makeCtx([s.tenantId], { now: DAY, fetch: t.f }).ctx);
    expect(t.calls[0]!.body.reply_markup).toBeUndefined();
    const [m] = await messages(s);
    expect(m.status).toBe("sent");
    expect(m.external_message_id).toBeNull();
  });

  it("send_callback-data-over-64-bytes_sends-text-without-markup", async () => {
    const s = await seed({ starts: visit(), scheduledAt: DAY });
    await remind(s, false);
    const [m] = await messages(s);
    await admin.query("UPDATE beauty_messages SET actions=$2::jsonb WHERE id=$1",
      [m.id, JSON.stringify({ rows: [[{ label: "x", data: "aa:" + "я".repeat(40) }]] })]);
    const t = telegram();
    await pollOutbox(makeCtx([s.tenantId], { now: DAY, fetch: t.f }).ctx);
    expect(t.calls[0]!.body.reply_markup).toBeUndefined();
    expect(t.calls[0]!.body.text).toBe(m.body);
  });

  it("send_dotnet-pascal-case-actions_are-read", async () => {
    const s = await seed({ starts: visit(), scheduledAt: DAY });
    await remind(s, false);
    const [m] = await messages(s);
    await admin.query("UPDATE beauty_messages SET actions=$2::jsonb WHERE id=$1",
      [m.id, JSON.stringify({ Rows: [[{ Label: "Так", Data: "by:AAAAAAAAAAAAAAAAAAAAAA" }]] })]);
    const t = telegram();
    await pollOutbox(makeCtx([s.tenantId], { now: DAY, fetch: t.f }).ctx);
    expect(t.calls[0]!.body.reply_markup).toEqual({ inline_keyboard: [[{ text: "Так", callback_data: "by:AAAAAAAAAAAAAAAAAAAAAA" }]] });
  });

  it("send_instagram_ignores-actions", async () => {
    const ig = await seed({ starts: visit(), scheduledAt: DAY, channel: "instagram", reminder: false });
    await admin.query(`INSERT INTO beauty_messages (tenant_id, conversation_id, direction, sender_type, body, status, idempotency_key, sent_at, actions)
      VALUES ($1,$2,'outbound','system','привіт','pending','message:ig1',now(),$3::jsonb)`,
      [ig.tenantId, ig.convId, JSON.stringify({ rows: [[{ label: "A", data: "i:help" }]] })]);
    await admin.query("INSERT INTO beauty_messages (tenant_id, conversation_id, direction, sender_type, body, status, sent_at) VALUES ($1,$2,'inbound','client','hi','processed',$3)",
      [ig.tenantId, ig.convId, DAY]);
    const t = telegram();
    await pollOutbox(makeCtx([ig.tenantId], { now: DAY, fetch: t.f }).ctx);
    expect(t.calls).toHaveLength(1);
    expect(t.calls[0]!.url).toContain("graph.facebook.com");
    expect(JSON.stringify(t.calls[0]!.body)).not.toContain("inline_keyboard");
    expect(JSON.stringify(t.calls[0]!.body)).not.toContain("reply_markup");
  });

  it("send_mock-channel_makes-no-network-call-and-keeps-stored-actions", async () => {
    const s = await seed({ starts: visit(), scheduledAt: DAY });
    await remind(s, true);
    await pollOutbox(makeCtx([s.tenantId], { now: DAY }).ctx); // no fetch -> MockChannelSender
    const [m] = await messages(s);
    expect(m.status).toBe("sent");
    expect(m.external_message_id).toBeNull();
    expect(m.actions).not.toBeNull();
  });
});

describe("no personal data in logs (§23.11, test 19 worker side)", () => {
  it("logs-and-notification-entries_contain-no-token-callback-data-chat-id-names-or-text", async () => {
    const s = await seed({ starts: visit(), scheduledAt: DAY });
    const r = await remind(s, true);
    const t = telegram([500, 200]);
    const o = makeCtx([s.tenantId], { now: DAY, fetch: t.f });
    await pollOutbox(o.ctx);
    await pollOutbox(o.ctx);
    const [k] = await keyboards(s);
    const dump = JSON.stringify([r.lines, r.entries, o.lines, o.entries]);
    for (const secret of [k.token, "aa:", "ac:", "i:stop", "callback_data", "inline_keyboard", CHAT, NAME, PHONE, SERVICE, TOKEN, "Підтверджую", "bot987654"]) {
      expect(dump, secret).not.toContain(secret);
    }
  });
});

describe("RLS", () => {
  it("keyboard_of-another-tenant_is-invisible-to-the-worker-role-and-not-updatable", async () => {
    const a = await seed({ starts: visit(), scheduledAt: DAY });
    const b = await seed({ starts: visit(), scheduledAt: DAY });
    await remind(a, true); await remind(b, true);
    const seen = await withTenant(a.tenantId, () => db.tx((c) => c.query("SELECT tenant_id FROM beauty_chat_keyboards")));
    expect(seen.rows.map((r) => r.tenant_id)).toEqual([a.tenantId]);
    await expect(withTenant(a.tenantId, () => db.tx((c) => c.query("UPDATE beauty_chat_keyboards SET status='revoked'")))).rejects.toThrow();
  });
});
