import { randomBytes, randomUUID } from "node:crypto";
import { readFileSync } from "node:fs";
import pg from "pg";
import { PostgreSqlContainer, type StartedPostgreSqlContainer } from "@testcontainers/postgresql";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { writeCredentials } from "../adapters/channel-crypto";
import { MockChannelSender } from "../adapters/mock-channel-sender";
import { PgBeautyData } from "../adapters/pg-data";
import { PgPollStore } from "../adapters/pg-poll-store";
import { PgAdvisoryIdempotency, PgQueuePort, LoggerNotificationLog, type Logger } from "../adapters/pg-support";
import { TenantDb, withTenant } from "../db/tenant-db";
import { StaticTenantDirectory } from "../db/tenants";
import type { BeautyDeps, StoredMessage } from "../ports";
import { pollOutbox, pollReminders, type PollContext } from "../scheduler/pollers";

/**
 * Email-fallback нагадування (мобільний застосунок): клієнт без месенджера, але з email, отримує нагадування через канал
 * `email` (розмова створюється на льоту); з месенджером — Telegram без змін; без обох — `no_channel`.
 * Реальний PostgreSQL, роль без BYPASSRLS; мережі немає (MockChannelSender).
 */
const KEY = randomBytes(32);
const ROLE = "beauty_worker_email_fb";
const HOUR = 3_600_000;
const NOW = new Date("2026-03-10T12:00:00Z"); // Київ 14:00 (поза тихими годинами)
const STARTS = new Date(NOW.getTime() + 24 * HOUR); // 11.03 14:00 за Києвом (UTC+2)
const EMAIL = "client@example.com";

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

interface Opts { email?: string | null; telegram?: boolean; emailChannel?: boolean; emailChannelActive?: boolean; legacy24h?: boolean }

async function seed(o: Opts) {
  const tenantId = randomUUID();
  const [loc, spec, svc, client, appt, reminderId] = Array.from({ length: 6 }, () => randomUUID());
  const q = (sql: string, p: unknown[]) => admin.query(sql, p);
  await q("INSERT INTO beauty_locations (id, tenant_id, name, address, timezone, is_active) VALUES ($1,$2,'Салон Лілія','вул. Хрещатик, 1','Europe/Kyiv',true)", [loc, tenantId]);
  await q("INSERT INTO beauty_specialists (id, tenant_id, full_name, is_active) VALUES ($1,$2,'S',true)", [spec, tenantId]);
  await q("INSERT INTO beauty_services (id, tenant_id, name, duration_minutes, is_active) VALUES ($1,$2,'Манікюр',60,true)", [svc, tenantId]);
  await q("INSERT INTO beauty_clients (id, tenant_id, full_name, email) VALUES ($1,$2,'Іванна',$3)", [client, tenantId, o.email === undefined ? EMAIL : o.email]);
  await q(`INSERT INTO beauty_appointments (id, tenant_id, location_id, specialist_id, service_id, client_id, starts_at, duration_minutes,
             status, source, price_original, price_final, reminder_option)
           VALUES ($1,$2,$3,$4,$5,$6,$7,60,'confirmed','online',100,100,$8)`, [appt, tenantId, loc, spec, svc, client, STARTS, o.legacy24h ? "24h" : "none"]);
  const addChannel = async (type: string, active: boolean) => {
    const id = randomUUID();
    await q("INSERT INTO beauty_channels (id, tenant_id, type, name, credentials_encrypted, is_active) VALUES ($1,$2,$3,'ch',$4,$5)",
      [id, tenantId, type, writeCredentials(KEY, { token: "t", webhookSecret: "s" }), active]);
    return id;
  };
  if (o.telegram) {
    const ch = await addChannel("telegram", true);
    await q("INSERT INTO beauty_conversations (id, tenant_id, channel_id, client_id, external_chat_id, last_message_at) VALUES ($1,$2,$3,$4,'777000111', now())", [randomUUID(), tenantId, ch, client]);
  }
  if (o.emailChannel !== false) await addChannel("email", o.emailChannelActive ?? true);
  if (o.legacy24h) {
    await q("INSERT INTO beauty_reminders (id, tenant_id, appointment_id, scheduled_at, status) VALUES ($1,$2,$3,$4,'scheduled')", [reminderId, tenantId, appt, NOW]);
  } else {
    await q(`INSERT INTO beauty_reminders (id, tenant_id, appointment_id, scheduled_at, status, offset_minutes, method, planned_starts_at)
             VALUES ($1,$2,$3,$4,'scheduled',1440,'auto',$5)`, [reminderId, tenantId, appt, NOW, STARTS]);
  }
  return { tenantId, clientId: client, reminderId };
}

function makeCtx(tenantId: string) {
  const logger: Logger = { info: () => {}, error: () => {} };
  const sent: StoredMessage[] = [];
  const mock = new MockChannelSender(logger);
  const now = () => NOW;
  const deps: BeautyDeps = {
    now, data: new PgBeautyData(db, now),
    sender: { send: async (m) => { sent.push(m); await mock.send(m); } },
    idempotency: new PgAdvisoryIdempotency(lockPool),
    log: { append: async (e) => { await new LoggerNotificationLog(logger).append(e); } },
    queue: new PgQueuePort(),
  };
  const ctx: PollContext = {
    deps, store: new PgPollStore(db), tenants: new StaticTenantDirectory([tenantId]), logger, backoffMs: 0, remindersEnabled: true,
  };
  return { ctx, sent };
}

const rows = async (sql: string, p: unknown[] = []) => (await admin.query(sql, p)).rows;
const reminder = async (id: string) => (await rows("SELECT status, skip_reason, message_id FROM beauty_reminders WHERE id=$1", [id]))[0];

describe("reminder email fallback", () => {
  it("client-without-messenger-but-with-email_gets-email-reminder_and-email-conversation-is-created", async () => {
    const s = await seed({});
    const { ctx, sent } = makeCtx(s.tenantId);
    await pollReminders(ctx); await pollOutbox(ctx);
    expect((await reminder(s.reminderId)).status).toBe("sent");
    expect(sent).toHaveLength(1);
    expect(sent[0]).toMatchObject({ channel: "email", address: EMAIL, kind: "reminder" });
    expect(sent[0]!.text).toContain("Дата й час: 11 березня 2026 р. о 14:00");
    expect(sent[0]!.text).not.toContain("напишіть сюди");
    expect(sent[0]!.text).not.toContain("Стоп");
    const convs = await rows("SELECT external_chat_id FROM beauty_conversations WHERE tenant_id=$1", [s.tenantId]);
    expect(convs).toEqual([{ external_chat_id: EMAIL }]);
  });

  it("repeated-lookup-and-reruns_reuse-the-email-conversation_and-send-once", async () => {
    const s = await seed({});
    const { ctx, sent } = makeCtx(s.tenantId);
    const first = await withTenant(s.tenantId, () => ctx.store.findEmailConversation(s.clientId, NOW));
    const second = await withTenant(s.tenantId, () => ctx.store.findEmailConversation(s.clientId, NOW));
    expect(second).toBe(first);
    await pollReminders(ctx); await pollReminders(ctx); await pollOutbox(ctx);
    expect(sent).toHaveLength(1);
    expect(await rows("SELECT id FROM beauty_conversations WHERE tenant_id=$1", [s.tenantId])).toHaveLength(1);
  });

  it("legacy-24h-row_email-text-says-tomorrow", async () => {
    const s = await seed({ legacy24h: true });
    const { ctx, sent } = makeCtx(s.tenantId);
    await pollReminders(ctx); await pollOutbox(ctx);
    expect(sent).toHaveLength(1);
    expect(sent[0]).toMatchObject({ channel: "email" });
    expect(sent[0]!.text).toContain("(завтра)");
  });

  it("client-with-messenger_keeps-telegram-text-and-channel", async () => {
    const s = await seed({ telegram: true });
    const { ctx, sent } = makeCtx(s.tenantId);
    await pollReminders(ctx); await pollOutbox(ctx);
    expect(sent).toHaveLength(1);
    expect(sent[0]).toMatchObject({ channel: "telegram", address: "777000111" });
    expect(sent[0]!.text).toContain("напишіть сюди");
    expect(sent[0]!.text).toContain("Стоп");
    expect(await rows("SELECT id FROM beauty_conversations WHERE tenant_id=$1 AND external_chat_id=$2", [s.tenantId, EMAIL])).toHaveLength(0);
  });

  it.each([
    ["no-email-and-no-messenger", { email: null }],
    ["blank-email", { email: "   " }],
    ["email-but-email-channel-inactive", { emailChannelActive: false }],
    ["email-but-no-email-channel", { emailChannel: false }],
  ])("%s_is-skipped-no_channel", async (_n, extra) => {
    const s = await seed(extra);
    const { ctx, sent } = makeCtx(s.tenantId);
    await pollReminders(ctx); await pollOutbox(ctx);
    expect(await reminder(s.reminderId)).toMatchObject({ status: "skipped", skip_reason: "no_channel" });
    expect(sent).toHaveLength(0);
    expect(await rows("SELECT id FROM beauty_conversations WHERE tenant_id=$1", [s.tenantId])).toHaveLength(0);
  });
});
