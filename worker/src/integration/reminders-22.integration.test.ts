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
import { TenantDb } from "../db/tenant-db";
import { StaticTenantDirectory } from "../db/tenants";
import type { BeautyDeps, NotificationLogEntry, StoredMessage } from "../ports";
import { pollOutbox, pollReminders, type PollContext } from "../scheduler/pollers";

/**
 * TASK-742 (§22.5, §22.12 tests 3, 6, 8-10, 19 worker part + DST / night boundaries / 403 / STOP / duplicate).
 * Real PostgreSQL, role without BYPASSRLS, rows written the way ReminderPlanner will write them (offset_minutes,
 * planned_starts_at, method). Telegram is never called: HttpChannelSender gets a fake fetch.
 */
const KEY = randomBytes(32);
const ROLE = "beauty_worker_reminders";
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

interface Seed { tenantId: string; apptId: string; clientId: string; locId: string; reminderId: string; convId: string | null; channelId: string | null }
interface SeedOpts {
  tz?: string; starts: Date; scheduledAt?: Date; offsetMinutes?: number | null; planned?: Date | null | "same";
  apptStatus?: string; phone?: string | null; channel?: "telegram" | "instagram" | "none"; channelActive?: boolean;
  method?: "auto" | "telegram" | null; legacyOption?: string; pref?: "none" | "telegram" | "call" | null;
  settings?: { callEnabled?: boolean; quietStart?: string; quietEnd?: string }; extraTelegramConv?: boolean; reminder?: boolean;
}

async function seed(o: SeedOpts): Promise<Seed> {
  const tenantId = randomUUID();
  const [loc, spec, svc, client, appt, channel, conv, reminderId] = Array.from({ length: 8 }, () => randomUUID());
  const q = (sql: string, p: unknown[]) => admin.query(sql, p);
  await q("INSERT INTO beauty_locations (id, tenant_id, name, address, timezone, is_active) VALUES ($1,$2,'Салон Лілія','вул. Хрещатик, 1',$3,true)", [loc, tenantId, o.tz ?? "Europe/Kyiv"]);
  await q("INSERT INTO beauty_specialists (id, tenant_id, full_name, is_active) VALUES ($1,$2,'S',true)", [spec, tenantId]);
  await q("INSERT INTO beauty_services (id, tenant_id, name, duration_minutes, is_active) VALUES ($1,$2,$3,60,true)", [svc, tenantId, SERVICE]);
  await q("INSERT INTO beauty_clients (id, tenant_id, full_name, phone) VALUES ($1,$2,$3,$4)", [client, tenantId, NAME, o.phone === undefined ? PHONE : o.phone]);
  await q(`INSERT INTO beauty_appointments (id, tenant_id, location_id, specialist_id, service_id, client_id, starts_at, duration_minutes,
             status, source, price_original, price_final, reminder_option)
           VALUES ($1,$2,$3,$4,$5,$6,$7,60,$8,'online',100,100,$9)`,
    [appt, tenantId, loc, spec, svc, client, o.starts, o.apptStatus ?? "confirmed", o.legacyOption ?? "none"]);
  let channelId: string | null = null, convId: string | null = null;
  if (o.channel !== "none") {
    channelId = channel; convId = conv;
    await q("INSERT INTO beauty_channels (id, tenant_id, type, name, credentials_encrypted, is_active) VALUES ($1,$2,$3,'ch',$4,$5)",
      [channel, tenantId, o.channel ?? "telegram", writeCredentials(KEY, { token: TOKEN, webhookSecret: "s" }), o.channelActive ?? true]);
    await q("INSERT INTO beauty_conversations (id, tenant_id, channel_id, client_id, external_chat_id, last_message_at) VALUES ($1,$2,$3,$4,$5, now())", [conv, tenantId, channel, client, CHAT]);
  }
  if (o.extraTelegramConv && channelId) { // a newer conversation with another chat id: the preference must win over "latest"
    await q("INSERT INTO beauty_conversations (id, tenant_id, channel_id, client_id, external_chat_id, last_message_at) VALUES ($1,$2,$3,$4,'999000999', now() + interval '1 hour')", [randomUUID(), tenantId, channelId, client]);
  }
  if (o.pref) {
    await q(`INSERT INTO beauty_client_contact_preferences (tenant_id, client_id, method, offsets_minutes, telegram_conversation_id, source)
             VALUES ($1,$2,$3,$4,$5,'chat')`, [tenantId, client, o.pref, o.pref === "none" ? [] : [120], o.pref === "telegram" ? convId : null]);
  }
  if (o.settings) {
    await q("INSERT INTO beauty_reminder_settings (tenant_id, call_enabled, quiet_start, quiet_end) VALUES ($1,$2,$3,$4)",
      [tenantId, o.settings.callEnabled ?? true, o.settings.quietStart ?? "22:00", o.settings.quietEnd ?? "08:00"]);
  }
  if (o.reminder !== false) {
    const offset = o.offsetMinutes === undefined ? 120 : o.offsetMinutes;
    const planned = o.planned === "same" || o.planned === undefined ? (offset === null ? null : o.starts) : o.planned;
    await q(`INSERT INTO beauty_reminders (id, tenant_id, appointment_id, scheduled_at, status, offset_minutes, method, planned_starts_at)
             VALUES ($1,$2,$3,$4,'scheduled',$5,$6,$7)`,
      [reminderId, tenantId, appt, o.scheduledAt ?? new Date(o.starts.getTime() - 2 * HOUR), offset, offset === null ? null : (o.method === undefined ? "auto" : o.method), planned]);
  }
  return { tenantId, apptId: appt, clientId: client, locId: loc, reminderId, convId, channelId };
}

interface CtxOpts { now: Date; fetch?: typeof fetch; enabled?: boolean; minLead?: number }
function makeCtx(tenantIds: string[], o: CtxOpts) {
  const lines: string[] = [];
  const logger: Logger = {
    info: (m, f) => { lines.push(JSON.stringify({ m, ...f })); },
    error: (m, f) => { lines.push(JSON.stringify({ m, ...f })); },
  };
  const entries: NotificationLogEntry[] = [];
  const sent: StoredMessage[] = [];
  const now = () => o.now;
  const real = o.fetch ? new HttpChannelSender(db, KEY, o.fetch, now) : null;
  const mock = new MockChannelSender(logger);
  const deps: BeautyDeps = {
    now, data: new PgBeautyData(db, now),
    sender: { send: async (m) => { sent.push(m); await (real ?? mock).send(m); } },
    idempotency: new PgAdvisoryIdempotency(lockPool),
    log: { append: async (e) => { entries.push(e); await new LoggerNotificationLog(logger).append(e); } },
    queue: new PgQueuePort(),
  };
  const ctx: PollContext = {
    deps, store: new PgPollStore(db), tenants: new StaticTenantDirectory(tenantIds), logger, backoffMs: 0,
    remindersEnabled: o.enabled ?? true, reminderMinLeadMinutes: o.minLead,
  };
  return { ctx, sent, lines, entries };
}

const rows = async (sql: string, p: unknown[] = []) => (await admin.query(sql, p)).rows;
const reminder = async (s: Seed) => (await rows("SELECT status, error, skip_reason, scheduled_at, message_id FROM beauty_reminders WHERE id=$1", [s.reminderId]))[0];
const messages = (s: Seed) => rows("SELECT id, body, status, idempotency_key, last_error, conversation_id FROM beauty_messages WHERE tenant_id=$1", [s.tenantId]);
const tasks = (s: Seed) => rows("SELECT reason, status, offset_minutes, due_at, reminder_id, client_id, appointment_id, location_id FROM beauty_call_tasks WHERE tenant_id=$1", [s.tenantId]);
const respond = (status: number, body = "{}"): typeof fetch => (async () => new Response(body, { status })) as unknown as typeof fetch;

// 2026-03-10: Kyiv = UTC+2. 12:00Z = 14:00 local (outside quiet hours).
const DAY = T("2026-03-10T12:00:00Z");

describe("pollReminders: WORKER_REMINDERS_ENABLED (§22.2)", () => {
  it("disabled_flag_sends-nothing_and-leaves-rows-scheduled", async () => {
    const s = await seed({ starts: new Date(DAY.getTime() + 2 * HOUR), scheduledAt: DAY });
    const { ctx } = makeCtx([s.tenantId], { now: DAY, enabled: false });
    expect(await pollReminders(ctx)).toEqual({ processed: 0, errors: 0 });
    expect((await reminder(s)).status).toBe("scheduled");
    expect(await messages(s)).toHaveLength(0);
  });
});

describe("test 6: venue-local time in the text, never UTC", () => {
  it("text_has-local-time-of-the-venue_kyiv-and-new-york-differ_for-the-same-instant", async () => {
    // Kyiv (EET, UTC+2): 12:30Z = 14:30. New York (EDT, UTC-4): 20:30Z = 16:30. Each run is in daytime of its own zone;
    // the same-instant comparison of the two zones is in reminder-time.test.ts.
    const starts = T("2026-10-30T12:30:00Z");
    const kyiv = await seed({ starts, scheduledAt: T("2026-10-30T10:30:00Z") });
    const ny = await seed({ tz: "America/New_York", starts: T("2026-10-30T20:30:00Z"), scheduledAt: T("2026-10-30T18:30:00Z") });
    const a = makeCtx([kyiv.tenantId], { now: T("2026-10-30T10:30:00Z") });
    const b = makeCtx([ny.tenantId], { now: T("2026-10-30T18:30:00Z") });
    await pollReminders(a.ctx); await pollReminders(b.ctx);
    const [km] = await messages(kyiv), [nm] = await messages(ny);
    expect(km.body).toContain("пт, 30.10 о 14:30");
    expect(nm.body).toContain("пт, 30.10 о 16:30"); // 20:30Z = 16:30 EDT
    for (const body of [km.body, nm.body]) {
      expect(body).not.toMatch(/\d{4}-\d{2}-\d{2}|T\d{2}:\d{2}|\dZ\b/);
      expect(body).toContain("Щоб не отримувати нагадування, напишіть «Стоп».");
      expect(body).toContain(`Нагадуємо про ваш візит: ${SERVICE}`);
      expect(body).toContain("Заклад: Салон Лілія, вул. Хрещатик, 1.");
    }
  });

  it("dst_visit-on-spring-forward-day_shows-post-switch-local-time", async () => {
    // 29.03.2026 Kyiv: 03:00 -> 04:00. 02:00Z = 05:00 EEST. Reminder 24 h earlier is exactly 24 h before (planner), here due 28.03 02:00Z (04:00 local, quiet) ->
    // worker is run at 28.03 08:00Z (10:00 local): the text must say 05:00.
    const s = await seed({ starts: T("2026-03-29T02:00:00Z"), offsetMinutes: 1440, scheduledAt: T("2026-03-28T02:00:00Z") });
    const { ctx } = makeCtx([s.tenantId], { now: T("2026-03-28T08:00:00Z") });
    await pollReminders(ctx);
    expect((await messages(s))[0].body).toContain("нд, 29.03 о 05:00");
  });

  it("dst_visit-on-fall-back-day_shows-post-switch-local-time", async () => {
    // 25.10.2026 Kyiv: 04:00 -> 03:00. 08:00Z = 10:00 EET.
    const s = await seed({ starts: T("2026-10-25T08:00:00Z"), offsetMinutes: 1440, scheduledAt: T("2026-10-24T08:00:00Z") });
    const { ctx } = makeCtx([s.tenantId], { now: T("2026-10-24T09:00:00Z") });
    await pollReminders(ctx);
    expect((await messages(s))[0].body).toContain("нд, 25.10 о 10:00");
  });
});

describe("test 3: quiet hours are re-checked by the worker", () => {
  it("overdue-row-at-23-10_is-moved-to-08-00-local_and-sent-after-it", async () => {
    // visit tomorrow 09:30 local (07:30Z), {1440} due today 09:30 local; the worker only starts at 23:10 local (21:10Z)
    const s = await seed({ starts: T("2026-03-11T07:30:00Z"), offsetMinutes: 1440, scheduledAt: T("2026-03-10T07:30:00Z") });
    const { ctx, sent } = makeCtx([s.tenantId], { now: T("2026-03-10T21:10:00Z") });
    await pollReminders(ctx); await pollOutbox(ctx);
    const r = await reminder(s);
    expect(r.status).toBe("scheduled");
    expect(r.scheduled_at.toISOString()).toBe("2026-03-11T06:00:00.000Z"); // 08:00 local
    expect(sent).toHaveLength(0);
    expect(await messages(s)).toHaveLength(0);

    const morning = makeCtx([s.tenantId], { now: T("2026-03-11T06:00:00Z") });
    await pollReminders(morning.ctx); await pollOutbox(morning.ctx);
    expect(morning.sent).toHaveLength(1);
    expect((await reminder(s)).status).toBe("sent");
  });

  it("overdue-row_is-skipped-quiet-hours_when-less-than-60-min-would-remain-after-08-00", async () => {
    // visit 08:40 local (06:40Z), now 07:00 local (05:00Z) -> shifted 08:00, only 40 min left
    const s = await seed({ starts: T("2026-03-11T06:40:00Z"), scheduledAt: T("2026-03-11T04:40:00Z") });
    const { ctx, sent } = makeCtx([s.tenantId], { now: T("2026-03-11T05:00:00Z") });
    await pollReminders(ctx); await pollOutbox(ctx);
    expect(await reminder(s)).toMatchObject({ status: "skipped", skip_reason: "quiet_hours", error: "quiet_hours" });
    expect(sent).toHaveLength(0);
  });

  it("exactly-60-min-after-08-00_is-still-deferred_not-skipped", async () => {
    const s = await seed({ starts: T("2026-03-11T07:00:00Z"), scheduledAt: T("2026-03-11T05:00:00Z") }); // visit 09:00 local
    const { ctx } = makeCtx([s.tenantId], { now: T("2026-03-11T05:00:00Z") }); // 07:00 local
    await pollReminders(ctx);
    const r = await reminder(s);
    expect(r.status).toBe("scheduled");
    expect(r.scheduled_at.toISOString()).toBe("2026-03-11T06:00:00.000Z");
  });

  it("boundaries_21-59-sends_22-00-defers_07-59-defers_08-00-sends", async () => {
    const at = async (iso: string, startsIso: string) => {
      const s = await seed({ starts: T(startsIso), scheduledAt: T("2026-03-09T00:00:00Z") });
      const c = makeCtx([s.tenantId], { now: T(iso) });
      await pollReminders(c.ctx);
      return (await reminder(s)).status === "scheduled" ? ((await messages(s)).length === 0 ? "deferred" : "sent") : "sent";
    };
    expect(await at("2026-03-10T19:59:00Z", "2026-03-11T08:00:00Z")).toBe("sent"); // 21:59 local
    expect(await at("2026-03-10T20:00:00Z", "2026-03-11T08:00:00Z")).toBe("deferred"); // 22:00
    expect(await at("2026-03-11T05:59:00Z", "2026-03-11T08:00:00Z")).toBe("deferred"); // 07:59
    expect(await at("2026-03-11T06:00:00Z", "2026-03-11T08:00:00Z")).toBe("sent"); // 08:00
  });

  it("tenant-quiet-settings_are-honoured", async () => {
    // quiet 12:00-16:00 local; now 14:00 local -> deferred to 16:00 local (14:00Z)
    const s = await seed({ starts: T("2026-03-10T20:00:00Z"), scheduledAt: DAY, settings: { quietStart: "12:00", quietEnd: "16:00" } });
    const { ctx } = makeCtx([s.tenantId], { now: DAY });
    await pollReminders(ctx);
    expect((await reminder(s)).scheduled_at.toISOString()).toBe("2026-03-10T14:00:00.000Z");
  });

  it("night-of-spring-forward_defers-to-08-00-on-the-new-offset", async () => {
    // 29.03.2026 00:30Z = 02:30 local; quiet; 08:00 local = 05:00Z. Visit at 12:00 local (09:00Z).
    const s = await seed({ starts: T("2026-03-29T09:00:00Z"), scheduledAt: T("2026-03-28T20:00:00Z") });
    const { ctx } = makeCtx([s.tenantId], { now: T("2026-03-29T00:30:00Z") });
    await pollReminders(ctx);
    expect((await reminder(s)).scheduled_at.toISOString()).toBe("2026-03-29T05:00:00.000Z");
  });
});

describe("§22.5 checks 1-6", () => {
  const starts = new Date(DAY.getTime() + 3 * HOUR);
  it("inactive-appointment_completed-no-show-cancelled_are-skipped-appointment_inactive", async () => {
    for (const st of ["completed", "no_show", "cancelled"]) {
      const s = await seed({ starts, scheduledAt: DAY, apptStatus: st });
      const { ctx, sent } = makeCtx([s.tenantId], { now: DAY });
      await pollReminders(ctx); await pollOutbox(ctx);
      expect(await reminder(s)).toMatchObject({ status: "skipped", skip_reason: "appointment_inactive" });
      expect(sent).toHaveLength(0);
    }
  });
  it("visit-moved_planned-starts-at-mismatch_is-skipped-replanned", async () => {
    const s = await seed({ starts, scheduledAt: DAY, planned: new Date(starts.getTime() - HOUR) });
    const { ctx } = makeCtx([s.tenantId], { now: DAY });
    await pollReminders(ctx);
    expect(await reminder(s)).toMatchObject({ status: "skipped", skip_reason: "replanned" });
  });
  it("less-than-min-lead_is-skipped-too_late_and-min-lead-is-configurable", async () => {
    const s = await seed({ starts: new Date(DAY.getTime() + 10 * 60_000), scheduledAt: DAY });
    await pollReminders(makeCtx([s.tenantId], { now: DAY }).ctx);
    expect(await reminder(s)).toMatchObject({ status: "skipped", skip_reason: "too_late" });
    const ok = await seed({ starts: new Date(DAY.getTime() + 10 * 60_000), scheduledAt: DAY });
    await pollReminders(makeCtx([ok.tenantId], { now: DAY, minLead: 5 }).ctx);
    expect((await reminder(ok)).message_id).not.toBeNull();
  });
  it("stop-preference-none_is-skipped-stopped_without-message", async () => {
    const s = await seed({ starts, scheduledAt: DAY, pref: "none" });
    const { ctx, sent } = makeCtx([s.tenantId], { now: DAY });
    await pollReminders(ctx); await pollOutbox(ctx);
    expect(await reminder(s)).toMatchObject({ status: "skipped", skip_reason: "stopped" });
    expect(sent).toHaveLength(0);
    expect(await messages(s)).toHaveLength(0);
  });
  it("preference-conversation_beats-latest-conversation", async () => {
    const s = await seed({ starts, scheduledAt: DAY, pref: "telegram", extraTelegramConv: true });
    const { ctx, sent } = makeCtx([s.tenantId], { now: DAY });
    await pollReminders(ctx); await pollOutbox(ctx);
    expect(sent).toHaveLength(1);
    expect(sent[0].address).toBe(CHAT); // not the newer 999000999
    expect((await messages(s))[0].conversation_id).toBe(s.convId);
  });
  it("without-preference_latest-telegram-conversation-is-used", async () => {
    const s = await seed({ starts, scheduledAt: DAY, extraTelegramConv: true });
    const { ctx, sent } = makeCtx([s.tenantId], { now: DAY });
    await pollReminders(ctx); await pollOutbox(ctx);
    expect(sent[0].address).toBe("999000999");
  });
  it("only-telegram-auto-rows_are-processed", async () => {
    const s = await seed({ starts, scheduledAt: DAY, method: "telegram" });
    const { ctx, sent } = makeCtx([s.tenantId], { now: DAY });
    await pollReminders(ctx); await pollOutbox(ctx);
    expect(sent).toHaveLength(1);
  });
});

describe("test 10: unreachable client", () => {
  const starts = new Date(DAY.getTime() + 3 * HOUR);
  it.each([
    ["web-client-without-conversation", { channel: "none" as const }],
    ["inactive-telegram-channel", { channelActive: false }],
    ["instagram-only", { channel: "instagram" as const }],
  ])("%s_is-skipped-no_channel", async (_n, extra) => {
    const s = await seed({ starts, scheduledAt: DAY, ...extra });
    const { ctx, sent } = makeCtx([s.tenantId], { now: DAY });
    await pollReminders(ctx); await pollOutbox(ctx);
    expect(await reminder(s)).toMatchObject({ status: "skipped", skip_reason: "no_channel" });
    expect(sent).toHaveLength(0);
    expect(await tasks(s)).toHaveLength(0);
  });
});

describe("test 8: duplicates", () => {
  const starts = new Date(DAY.getTime() + 3 * HOUR);
  it("two-pollers-and-reruns_create-one-message-and-one-send", async () => {
    const s = await seed({ starts, scheduledAt: DAY });
    const a = makeCtx([s.tenantId], { now: DAY });
    const b = makeCtx([s.tenantId], { now: DAY });
    await Promise.all([pollReminders(a.ctx), pollReminders(b.ctx), pollReminders(a.ctx)]);
    await Promise.all([pollOutbox(a.ctx), pollOutbox(b.ctx)]);
    await pollReminders(a.ctx); await pollOutbox(a.ctx);
    expect(a.sent.length + b.sent.length).toBe(1);
    const msgs = await messages(s);
    expect(msgs).toHaveLength(1);
    expect(msgs[0].idempotency_key).toBe(`reminder:${s.reminderId}`);
    const r = await reminder(s);
    expect(r.status).toBe("sent");
    expect(r.message_id).toBe(msgs[0].id);
  });
  it("one-scheduled-point-per-offset_is-enforced-by-unique-index_and-a-closed-row-frees-the-slot", async () => {
    const s = await seed({ starts, scheduledAt: DAY });
    const dup = () => admin.query(
      `INSERT INTO beauty_reminders (tenant_id, appointment_id, scheduled_at, status, offset_minutes, method, planned_starts_at)
       VALUES ($1,$2,$3,'scheduled',120,'auto',$4)`, [s.tenantId, s.apptId, DAY, starts]);
    await expect(dup()).rejects.toMatchObject({ code: "23505" });
    await admin.query("UPDATE beauty_reminders SET status='cancelled', skip_reason='replanned' WHERE id=$1", [s.reminderId]);
    await dup(); // replanned point is allowed
    const { ctx } = makeCtx([s.tenantId], { now: DAY });
    await pollReminders(ctx);
    expect(await messages(s)).toHaveLength(1);
  });
  it("closed-or-sent-rows_are-never-picked-up-again", async () => {
    const s = await seed({ starts, scheduledAt: DAY });
    const { ctx } = makeCtx([s.tenantId], { now: DAY });
    await pollReminders(ctx);
    expect(await pollReminders(ctx)).toEqual({ processed: 0, errors: 0 });
  });
});

describe("test 9: Telegram errors", () => {
  const starts = new Date(DAY.getTime() + 3 * HOUR);
  const run = async (s: Seed, f: typeof fetch) => {
    const c = makeCtx([s.tenantId], { now: DAY, fetch: f });
    await pollReminders(c.ctx);
    const results = [];
    for (let i = 0; i < 4; i++) results.push(await pollOutbox(c.ctx));
    return { ...c, results };
  };
  it("403_marks-failed-bot_blocked_creates-one-delivery_failed-task_keeps-preference_and-is-not-retried", async () => {
    const s = await seed({ starts, scheduledAt: DAY, pref: "telegram" });
    const { results } = await run(s, respond(403, '{"ok":false,"error_code":403,"description":"Forbidden: bot was blocked by the user"}'));
    expect(await reminder(s)).toMatchObject({ status: "failed", error: "bot_blocked", skip_reason: "bot_blocked" });
    const [m] = await messages(s);
    expect(m).toMatchObject({ status: "failed", last_error: "bot_blocked" });
    expect(results.map((r) => r.processed + r.errors)).toEqual([1, 0, 0, 0]); // permanent: a single attempt
    const t = await tasks(s);
    expect(t).toHaveLength(1);
    expect(t[0]).toMatchObject({ reason: "delivery_failed", status: "open", offset_minutes: 120, reminder_id: s.reminderId, client_id: s.clientId, appointment_id: s.apptId, location_id: s.locId });
    expect(t[0].due_at.toISOString()).toBe(DAY.toISOString());
    expect(await rows("SELECT method, offsets_minutes FROM beauty_client_contact_preferences WHERE client_id=$1", [s.clientId])).toEqual([{ method: "telegram", offsets_minutes: [120] }]);
    // task has no phone column at all
    expect((await rows("SELECT column_name FROM information_schema.columns WHERE table_name='beauty_call_tasks'")).map((c) => c.column_name)).not.toContain("phone");
    // pollers rerun: still one task, no new message
    const again = makeCtx([s.tenantId], { now: DAY, fetch: respond(403) });
    await pollReminders(again.ctx); await pollOutbox(again.ctx);
    expect(await tasks(s)).toHaveLength(1);
    expect(await messages(s)).toHaveLength(1);
  });
  it("400-chat-not-found_marks-failed-chat_not_found_and-creates-task", async () => {
    const s = await seed({ starts, scheduledAt: DAY });
    await run(s, respond(400, '{"ok":false,"error_code":400,"description":"Bad Request: chat not found"}'));
    expect(await reminder(s)).toMatchObject({ status: "failed", error: "chat_not_found", skip_reason: "chat_not_found" });
    expect(await tasks(s)).toHaveLength(1);
  });
  it("other-400_is-permanent-failed-without-code-and-without-task", async () => {
    const s = await seed({ starts, scheduledAt: DAY });
    await run(s, respond(400, '{"description":"Bad Request: message is too long"}'));
    expect((await reminder(s)).status).toBe("failed");
    expect((await reminder(s)).skip_reason).toBeNull();
    expect(await tasks(s)).toHaveLength(0);
  });
  it("403-without-phone_no-task", async () => {
    const s = await seed({ starts, scheduledAt: DAY, phone: null });
    await run(s, respond(403));
    expect(await reminder(s)).toMatchObject({ status: "failed", skip_reason: "bot_blocked" });
    expect(await tasks(s)).toHaveLength(0);
  });
  it("403-with-call-disabled_no-task", async () => {
    const s = await seed({ starts, scheduledAt: DAY, settings: { callEnabled: false } });
    await run(s, respond(403));
    expect(await tasks(s)).toHaveLength(0);
  });
  it("429-is-transient_message-stays-pending-for-retry_reminder-stays-scheduled", async () => {
    const s = await seed({ starts, scheduledAt: DAY });
    const c = makeCtx([s.tenantId], { now: DAY, fetch: respond(429) });
    await pollReminders(c.ctx); await pollOutbox(c.ctx);
    expect((await messages(s))[0]).toMatchObject({ status: "pending", last_error: "Telegram sendMessage failed with HTTP 429" });
    expect((await reminder(s)).status).toBe("scheduled");
    expect(await tasks(s)).toHaveLength(0);
  });
  it("inactive-channel-after-queueing_fails-with-channel_inactive-code", async () => {
    const s = await seed({ starts, scheduledAt: DAY });
    const c = makeCtx([s.tenantId], { now: DAY, fetch: respond(200) });
    await pollReminders(c.ctx);
    await admin.query("UPDATE beauty_channels SET is_active=false WHERE id=$1", [s.channelId]);
    await pollOutbox(c.ctx);
    expect(await reminder(s)).toMatchObject({ status: "failed", skip_reason: "channel_inactive" });
    expect(await tasks(s)).toHaveLength(0); // only bot_blocked / chat_not_found create tasks
  });
});

describe("mock channels (Channels__UseMocks)", () => {
  it("mock-sender_is-used-without-network_and-marks-reminder-sent", async () => {
    const s = await seed({ starts: new Date(DAY.getTime() + 3 * HOUR), scheduledAt: DAY });
    const c = makeCtx([s.tenantId], { now: DAY }); // no fetch => MockChannelSender
    await pollReminders(c.ctx); await pollOutbox(c.ctx);
    expect(c.sent).toHaveLength(1);
    expect((await reminder(s)).status).toBe("sent");
  });
});

describe("legacy rows (offset_minutes IS NULL) keep working", () => {
  it("legacy-2h-row_reads-reminder_option_and-text-is-local-with-lead", async () => {
    const starts = new Date(DAY.getTime() + 2 * HOUR);
    const s = await seed({ starts, scheduledAt: DAY, offsetMinutes: null, legacyOption: "2h" });
    const c = makeCtx([s.tenantId], { now: DAY });
    await pollReminders(c.ctx); await pollOutbox(c.ctx);
    const [m] = await messages(s);
    expect(m.body).toContain("(через 2 години)");
    expect(m.body).toContain("о 16:00"); // 14:00Z = 16:00 Kyiv (UTC+2)
  });
});

describe("test 19: no personal data in worker logs", () => {
  it("logs-and-notification-entries-contain-only-ids-and-codes", async () => {
    const starts = new Date(DAY.getTime() + 3 * HOUR);
    const all: string[] = [];
    const scenarios: [SeedOpts, typeof fetch | undefined][] = [
      [{ starts, scheduledAt: DAY }, undefined], // mock sender
      [{ starts, scheduledAt: DAY }, respond(403, '{"description":"Forbidden: bot was blocked by the user"}')],
      [{ starts, scheduledAt: DAY }, respond(400, '{"description":"Bad Request: chat not found"}')],
      [{ starts, scheduledAt: DAY, pref: "none" }, undefined],
      [{ starts, scheduledAt: DAY, channel: "none" }, undefined],
      [{ starts, scheduledAt: DAY, apptStatus: "cancelled" }, undefined],
      [{ starts: T("2026-03-11T07:30:00Z"), scheduledAt: T("2026-03-10T07:30:00Z") }, undefined], // quiet-hours deferral
    ];
    for (const [i, [o, f]] of scenarios.entries()) {
      const s = await seed(i === scenarios.length - 1 ? { ...o, offsetMinutes: 1440 } : o);
      const c = makeCtx([s.tenantId], { now: i === scenarios.length - 1 ? T("2026-03-10T21:10:00Z") : DAY, fetch: f });
      await pollReminders(c.ctx); await pollOutbox(c.ctx); await pollOutbox(c.ctx);
      all.push(...c.lines, ...c.entries.map((e) => JSON.stringify(e)));
    }
    expect(all.length).toBeGreaterThan(5);
    const blob = all.join("\n");
    for (const secret of [PHONE, CHAT, "999000999", NAME, "Іванна", SERVICE, "Салон Лілія", "Хрещатик", "Нагадуємо", "Стоп", TOKEN, "SECRET-TOKEN", "blocked by the user", "chat not found"]) {
      expect(blob, `log leaks "${secret}"`).not.toContain(secret);
    }
  });
});
