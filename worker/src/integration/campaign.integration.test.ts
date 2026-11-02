import { randomBytes, randomUUID } from "node:crypto";
import { readFileSync } from "node:fs";
import pg from "pg";
import { PostgreSqlContainer, type StartedPostgreSqlContainer } from "@testcontainers/postgresql";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { ChannelSendError } from "../adapters/channel-sender";
import { writeCredentials } from "../adapters/channel-crypto";
import { PgBeautyData } from "../adapters/pg-data";
import { PgPollStore } from "../adapters/pg-poll-store";
import { PgAdvisoryIdempotency, PgQueuePort, type Logger } from "../adapters/pg-support";
import { TenantDb, withTenant } from "../db/tenant-db";
import { StaticTenantDirectory } from "../db/tenants";
import { delayUntilWindow, inCampaignWindow, renderCampaignText } from "../jobs/beauty-campaign";
import { codeHint, generatePersonalCode, hashCode, normalizeCode } from "../jobs/coupon-code";
import type { BeautyDeps, NotificationLogEntry, StoredMessage } from "../ports";
import { pollCampaigns, pollOutbox, pollWinback, type PollContext } from "../scheduler/pollers";

/** TASK-780 (§26.8): campaigns in the worker on PostgreSQL 16 with RLS (role without BYPASSRLS). */
const NOON = new Date("2026-11-10T10:00:00Z"); // Kyiv 12:00 (UTC+2)
const NIGHT = new Date("2026-11-10T22:30:00Z"); // Kyiv 00:30
const KEY = randomBytes(32);
const ROLE = "beauty_worker_campaign";
const NAME = "Олена Тестова";
const CHAT = "888000222";
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

interface Seed { tenantId: string; campaignId: string; userId: string; loc: string; svc: string; promo: string; spec: string; channelId: string }
interface ClientSeed { clientId: string; recipientId: string; convId: string }
const q = (sql: string, p: unknown[] = []) => admin.query(sql, p);

async function seed(o: { type?: "promo" | "news"; coupon?: "none" | "personal" | "shared"; status?: string; buttons?: boolean; tz?: string; promoActive?: boolean } = {}): Promise<Seed> {
  const tenantId = randomUUID();
  const [loc, spec, svc, promo, campaignId, userId, channelId] = Array.from({ length: 7 }, () => randomUUID());
  await q("INSERT INTO tenants (id, name, slug, modules) VALUES ($1,'T',$2,'{beauty_marketing}')", [tenantId, `t-${tenantId.slice(0, 8)}`]);
  await q("INSERT INTO users (id, tenant_id, email, full_name, password_hash, role) VALUES ($1,$2,$3,'U','x','owner')", [userId, tenantId, `${userId}@x.test`]);
  await q("INSERT INTO beauty_locations (id, tenant_id, name, timezone, is_active) VALUES ($1,$2,'L',$3,true)", [loc, tenantId, o.tz ?? "Europe/Kyiv"]);
  await q("INSERT INTO beauty_specialists (id, tenant_id, full_name, is_active) VALUES ($1,$2,'S',true)", [spec, tenantId]);
  await q("INSERT INTO beauty_services (id, tenant_id, name, duration_minutes, is_active) VALUES ($1,$2,'Манікюр',60,true)", [svc, tenantId]);
  const type = o.type ?? "promo";
  const coupon = o.coupon ?? (type === "promo" ? "personal" : "none");
  if (type === "promo") {
    await q(`INSERT INTO beauty_promotions (id, tenant_id, name, discount_type, discount_value, starts_at, ends_at, is_active, access)
             VALUES ($1,$2,'P','percent',15,'2026-11-01T00:00:00Z','2026-12-01T00:00:00Z',$3,$4)`, [promo, tenantId, o.promoActive ?? true, coupon === "none" ? "public" : "code_only"]);
    await q("INSERT INTO beauty_promotion_services (promotion_id, service_id, tenant_id) VALUES ($1,$2,$3)", [promo, svc, tenantId]);
  }
  await q("INSERT INTO beauty_channels (id, tenant_id, type, name, credentials_encrypted, is_active, settings) VALUES ($1,$2,'telegram','ch',$3,true,$4::jsonb)",
    [channelId, tenantId, writeCredentials(KEY, { token: "TG-TOKEN", webhookSecret: "s" }), JSON.stringify({ assistant: { enabled: o.buttons ?? true } })]);
  await q(`INSERT INTO beauty_campaigns (id, tenant_id, name, type, promotion_id, segment_kind, template, coupon_mode, coupon_valid_from, coupon_valid_to, status,
             scheduled_at, confirmed_by_user_id, confirmed_at, created_by_user_id, cancelled_at)
           VALUES ($1,$2,'C',$3,$4,'all_consented',$5,$6,$7,$8,$9,'2026-11-10T00:00:00Z',$10,'2026-11-09T00:00:00Z',$10,$11)`,
    [campaignId, tenantId, type, type === "promo" ? promo : null, "{name}, знижка {discount} на {service} до {valid_to}",
      coupon, coupon === "personal" ? "2026-11-01T00:00:00Z" : null, coupon === "personal" ? "2026-12-01T00:00:00Z" : null,
      o.status ?? "sending", userId, o.status === "cancelled" ? "2026-11-10T00:00:00Z" : null]);
  return { tenantId, campaignId, userId, loc, svc, promo, spec, channelId };
}

async function addClient(s: Seed, o: { level?: "full" | "selective" | "none" | null; visited?: boolean; oldFlag?: boolean; lastVisit?: Date } = {}): Promise<ClientSeed> {
  const [clientId, convId, recipientId] = [randomUUID(), randomUUID(), randomUUID()];
  await q("INSERT INTO beauty_clients (id, tenant_id, full_name, phone, marketing_consent) VALUES ($1,$2,$3,$4,$5)", [clientId, s.tenantId, NAME, PHONE, o.oldFlag ?? false]);
  await q("INSERT INTO beauty_conversations (id, tenant_id, channel_id, client_id, external_chat_id) VALUES ($1,$2,$3,$4,$5)", [convId, s.tenantId, s.channelId, clientId, `${CHAT}${clientId.slice(0, 6)}`]);
  if (o.level !== null) {
    await q("INSERT INTO beauty_marketing_consents (tenant_id, client_id, channel, level, source, text_version) VALUES ($1,$2,'telegram',$3,'chat','mc-v1')", [s.tenantId, clientId, o.level ?? "full"]);
  }
  if (o.visited) {
    await q(`INSERT INTO beauty_appointments (tenant_id, location_id, specialist_id, service_id, client_id, starts_at, duration_minutes, status, source, price_original, price_final, reminder_option)
             VALUES ($1,$2,$3,$4,$5,$6,60,'completed','admin',100,100,'none')`, [s.tenantId, s.loc, s.spec, s.svc, clientId, o.lastVisit ?? new Date("2026-10-01T10:00:00Z")]);
  }
  await q("INSERT INTO beauty_campaign_recipients (id, tenant_id, campaign_id, client_id) VALUES ($1,$2,$3,$4)", [recipientId, s.tenantId, s.campaignId, clientId]);
  return { clientId, recipientId, convId };
}

function makeCtx(tenantIds: string[], o: { now?: Date; send?: (m: StoredMessage) => Promise<void>; limit?: number; extra?: Partial<PollContext> } = {}) {
  const sent: StoredMessage[] = [];
  const logs: NotificationLogEntry[] = [];
  const lines: string[] = [];
  const logger: Logger = { info: (m, f) => { lines.push(JSON.stringify({ m, ...f })); }, error: (m, f) => { lines.push(JSON.stringify({ m, ...f })); } };
  const now = () => o.now ?? NOON;
  const deps: BeautyDeps = {
    now, data: new PgBeautyData(db, now),
    sender: { send: async (m) => { if (o.send) await o.send(m); sent.push(m); } },
    idempotency: new PgAdvisoryIdempotency(lockPool),
    log: { append: async (e) => { logs.push(e); } },
    queue: new PgQueuePort(), marketingDailyLimit: o.limit,
  };
  const ctx: PollContext = { deps, store: new PgPollStore(db), tenants: new StaticTenantDirectory(tenantIds), logger, backoffMs: 0, ...o.extra };
  return { ctx, sent, logs, lines };
}
const recipient = async (id: string) => (await q("SELECT status, skip_reason, message_id, coupon_id FROM beauty_campaign_recipients WHERE id=$1", [id])).rows[0];
const campaignStatus = async (id: string) => (await q("SELECT status FROM beauty_campaigns WHERE id=$1", [id])).rows[0].status as string;
const msgCount = async (t: string) => (await q("SELECT count(*)::int AS n FROM beauty_messages WHERE tenant_id=$1", [t])).rows[0].n as number;

describe("window and DST (Europe/Kyiv)", () => {
  it("delayUntilWindow_is-dst-correct_in-summer-and-winter-time", () => {
    expect(inCampaignWindow(new Date("2026-07-10T06:00:00Z"), "Europe/Kyiv")).toBe(true); // 09:00 EEST
    expect(inCampaignWindow(new Date("2026-07-10T05:59:00Z"), "Europe/Kyiv")).toBe(false);
    expect(inCampaignWindow(new Date("2026-01-10T07:00:00Z"), "Europe/Kyiv")).toBe(true); // 09:00 EET
    expect(inCampaignWindow(new Date("2026-01-10T06:59:00Z"), "Europe/Kyiv")).toBe(false);
    expect(inCampaignWindow(new Date("2026-07-10T16:59:00Z"), "Europe/Kyiv")).toBe(true); // 19:59
    expect(inCampaignWindow(new Date("2026-07-10T17:00:00Z"), "Europe/Kyiv")).toBe(false); // 20:00
    expect(delayUntilWindow(new Date("2026-07-10T17:00:00Z"), "Europe/Kyiv")).toBe(13 * 3_600_000); // -> 09:00 EEST next day
    // night before the spring-forward day: 22:00 UTC = 00:00 EET -> opens 09:00 EEST = 06:00 UTC next day
    expect(delayUntilWindow(new Date("2026-03-28T22:00:00Z"), "Europe/Kyiv")).toBe(8 * 3_600_000);
    // fall-back (2026-10-25): 21:00 UTC on the 24th (00:00 EEST) -> 09:00 EET = 07:00 UTC -> 10 h
    expect(delayUntilWindow(new Date("2026-10-24T21:00:00Z"), "Europe/Kyiv")).toBe(10 * 3_600_000);
    expect(delayUntilWindow(new Date("2026-01-10T10:00:00Z"), "Europe/Kyiv")).toBe(0);
  });

  it("recipient_outside-window_stays-pending_and-is-queued-next-morning", async () => {
    const s = await seed();
    const c = await addClient(s);
    await pollCampaigns(makeCtx([s.tenantId], { now: NIGHT }).ctx);
    expect(await recipient(c.recipientId)).toMatchObject({ status: "pending" });
    expect(await msgCount(s.tenantId)).toBe(0);
    await pollCampaigns(makeCtx([s.tenantId], { now: new Date("2026-11-11T07:30:00Z") }).ctx); // 09:30 Kyiv
    expect(await recipient(c.recipientId)).toMatchObject({ status: "queued" });
  });
});

describe("consent levels", () => {
  it("consent_checks-table-not-old-flag_and-levels-selective-vs-news", async () => {
    const promo = await seed();
    const full = await addClient(promo, { level: "full" });
    const selOverlap = await addClient(promo, { level: "selective", visited: true });
    const selNoOverlap = await addClient(promo, { level: "selective" });
    const none = await addClient(promo, { level: "none" });
    const noRow = await addClient(promo, { level: null, oldFlag: true });
    await pollCampaigns(makeCtx([promo.tenantId]).ctx);
    expect(await recipient(full.recipientId)).toMatchObject({ status: "queued" });
    expect(await recipient(selOverlap.recipientId)).toMatchObject({ status: "queued" });
    expect(await recipient(selNoOverlap.recipientId)).toMatchObject({ status: "skipped", skip_reason: "level_mismatch" });
    expect(await recipient(none.recipientId)).toMatchObject({ status: "skipped", skip_reason: "unsubscribed" });
    expect(await recipient(noRow.recipientId)).toMatchObject({ status: "skipped", skip_reason: "no_consent" });

    const news = await seed({ type: "news" });
    const nFull = await addClient(news, { level: "full" });
    const nSel = await addClient(news, { level: "selective", visited: true });
    await pollCampaigns(makeCtx([news.tenantId]).ctx);
    expect(await recipient(nFull.recipientId)).toMatchObject({ status: "queued" });
    expect(await recipient(nSel.recipientId)).toMatchObject({ status: "skipped", skip_reason: "level_mismatch" });
  });

  it("stop-between-enqueue-and-send_is-not-delivered_recipient-skipped_coupon-revoked", async () => {
    const s = await seed();
    const c = await addClient(s);
    const { ctx, sent } = makeCtx([s.tenantId]);
    await pollCampaigns(ctx);
    expect(await recipient(c.recipientId)).toMatchObject({ status: "queued" });
    await q("UPDATE beauty_marketing_consents SET level='none', source='stop' WHERE client_id=$1", [c.clientId]);
    await pollOutbox(ctx);
    expect(sent).toHaveLength(0);
    expect(await recipient(c.recipientId)).toMatchObject({ status: "skipped", skip_reason: "consent_withdrawn" });
    expect((await q("SELECT status FROM beauty_coupons WHERE client_id=$1", [c.clientId])).rows).toEqual([{ status: "revoked" }]);
    expect((await q("SELECT status, last_error FROM beauty_messages WHERE tenant_id=$1", [s.tenantId])).rows).toEqual([{ status: "failed", last_error: "consent_withdrawn" }]);
  });

  it("selective-after-enqueue-on-news_is-withdrawn-before-send", async () => {
    const s = await seed({ type: "news" });
    const c = await addClient(s);
    const { ctx, sent } = makeCtx([s.tenantId]);
    await pollCampaigns(ctx);
    await q("UPDATE beauty_marketing_consents SET level='selective' WHERE client_id=$1", [c.clientId]);
    await pollOutbox(ctx);
    expect(sent).toHaveLength(0);
    expect(await recipient(c.recipientId)).toMatchObject({ skip_reason: "consent_withdrawn" });
  });
});

describe("frequency, daily limit, duplicates, cancel, flags", () => {
  it("frequency_one-marketing-message-per-7-days_across-campaigns", async () => {
    const a = await seed();
    const c = await addClient(a);
    await pollCampaigns(makeCtx([a.tenantId]).ctx);
    expect(await recipient(c.recipientId)).toMatchObject({ status: "queued" });
    const mk = async (): Promise<string> => {
      const id = randomUUID();
      await q(`INSERT INTO beauty_campaigns (id, tenant_id, name, type, segment_kind, template, coupon_mode, status, scheduled_at, confirmed_by_user_id, confirmed_at, created_by_user_id)
               VALUES ($1,$2,'C2','news','all_consented','Hi','none','sending','2026-11-10T00:00:00Z',$3,'2026-11-09T00:00:00Z',$3)`, [id, a.tenantId, a.userId]);
      const rid = randomUUID();
      await q("INSERT INTO beauty_campaign_recipients (id, tenant_id, campaign_id, client_id) VALUES ($1,$2,$3,$4)", [rid, a.tenantId, id, c.clientId]);
      return rid;
    };
    const r2 = await mk();
    await pollCampaigns(makeCtx([a.tenantId], { now: new Date("2026-11-13T10:00:00Z") }).ctx);
    expect(await recipient(r2)).toMatchObject({ status: "skipped", skip_reason: "frequency" });
    const r3 = await mk();
    await pollCampaigns(makeCtx([a.tenantId], { now: new Date("2026-11-18T10:00:00Z") }).ctx);
    expect(await recipient(r3)).toMatchObject({ status: "queued" });
  });

  it("winback_uses-consent-table_and-respects-frequency", async () => {
    const lastVisit = new Date("2026-08-01T10:00:00Z");
    const s = await seed({ type: "news" });
    await addClient(s, { visited: true, lastVisit });
    expect(await pollWinback(makeCtx([s.tenantId]).ctx)).toEqual({ processed: 1, errors: 0 });
    // a campaign message queued within 7 days blocks winback for the same client
    const s2 = await seed({ type: "news" });
    const c2 = await addClient(s2, { visited: true, lastVisit });
    await pollCampaigns(makeCtx([s2.tenantId]).ctx);
    expect(await recipient(c2.recipientId)).toMatchObject({ status: "queued" });
    expect(await pollWinback(makeCtx([s2.tenantId]).ctx)).toEqual({ processed: 0, errors: 0 });
    // old flag only (no consent row) -> no winback
    const s3 = await seed({ type: "news" });
    await addClient(s3, { level: null, oldFlag: true, visited: true, lastVisit });
    expect(await pollWinback(makeCtx([s3.tenantId]).ctx)).toEqual({ processed: 0, errors: 0 });
  });

  it("daily-limit_defers-excess-recipient-and-keeps-it-pending", async () => {
    const s = await seed({ type: "news" });
    const a = await addClient(s); const b = await addClient(s);
    await pollCampaigns(makeCtx([s.tenantId], { limit: 1 }).ctx);
    expect((await Promise.all([recipient(a.recipientId), recipient(b.recipientId)])).map((r) => r.status).sort()).toEqual(["pending", "queued"]);
    expect(await campaignStatus(s.campaignId)).toBe("sending");
    await pollCampaigns(makeCtx([s.tenantId], { limit: 1, now: new Date(NOON.getTime() + 25 * 3_600_000) }).ctx); // earlier message left the 24 h window
    expect((await Promise.all([recipient(a.recipientId), recipient(b.recipientId)])).map((r) => r.status)).toEqual(["queued", "queued"]);
  });

  it("two-pollers-in-parallel_create-one-message-per-client", async () => {
    const s = await seed({ type: "news" });
    const clients = await Promise.all(Array.from({ length: 6 }, () => addClient(s)));
    await Promise.all([pollCampaigns(makeCtx([s.tenantId]).ctx), pollCampaigns(makeCtx([s.tenantId]).ctx)]);
    expect(await msgCount(s.tenantId)).toBe(6);
    expect((await q("SELECT count(DISTINCT idempotency_key)::int AS n FROM beauty_messages WHERE tenant_id=$1", [s.tenantId])).rows[0].n).toBe(6);
    for (const c of clients) expect((await recipient(c.recipientId)).status).toBe("queued");
  });

  it("cancelled-campaign_queued-message-is-not-sent_and-pending-is-skipped", async () => {
    const s = await seed({ type: "news" });
    const a = await addClient(s); const b = await addClient(s);
    const { ctx, sent } = makeCtx([s.tenantId], { limit: 1 });
    await pollCampaigns(ctx); // one queued, one pending (limit)
    await q("UPDATE beauty_campaigns SET status='cancelled', cancelled_at=now() WHERE id=$1", [s.campaignId]);
    await pollOutbox(ctx);
    expect(sent).toHaveLength(0);
    const rows = await Promise.all([recipient(a.recipientId), recipient(b.recipientId)]);
    expect(rows.map((r) => `${r.status}:${r.skip_reason}`).sort()).toEqual(["pending:null", "skipped:campaign_cancelled"]);
    const pendingId = rows[0]!.status === "pending" ? a.recipientId : b.recipientId;
    const opts = { dailyLimit: 200, clientIntervalDays: 7, favoriteServicesDays: 365, defaultTimezone: "Europe/Kyiv", currencyLabel: "грн" };
    expect(await withTenant(s.tenantId, () => ctx.deps.data.enqueueCampaignMessage(pendingId, NOON, opts))).toBe("skipped:campaign_cancelled");
    expect(await msgCount(s.tenantId)).toBe(1);
  });

  it("flags-off_campaigns-and-winback-do-nothing", async () => {
    const s = await seed({ type: "news" });
    const c = await addClient(s, { visited: true, lastVisit: new Date("2026-08-01T10:00:00Z") });
    const { ctx } = makeCtx([s.tenantId], { extra: { campaignsEnabled: false, winbackEnabled: false } });
    expect(await pollCampaigns(ctx)).toEqual({ processed: 0, errors: 0 });
    expect(await pollWinback(ctx)).toEqual({ processed: 0, errors: 0 });
    expect(await recipient(c.recipientId)).toMatchObject({ status: "pending" });
    expect(await msgCount(s.tenantId)).toBe(0);
  });
});

describe("delivery, keyboard, privacy", () => {
  it("personal-coupon_message-has-code-keyboard-and-actions_sent-marks-recipient-and-campaign-done", async () => {
    const s = await seed();
    const c = await addClient(s);
    const { ctx, sent } = makeCtx([s.tenantId]);
    await pollCampaigns(ctx);
    const [m] = (await q("SELECT id, body, actions, idempotency_key FROM beauty_messages WHERE tenant_id=$1", [s.tenantId])).rows;
    expect(m.idempotency_key).toBe(`campaign:${s.campaignId}:${c.clientId}`);
    expect(m.body).toContain("Олена, знижка 15% на Манікюр до 01.12.2026");
    expect(m.body).toContain("Умови: знижка 15% на Манікюр");
    const code = /Ваш код: ([0-9A-Z]{5}-[0-9A-Z]{5})/.exec(m.body)![1]!;
    const [cp] = (await q("SELECT code, code_hash, code_hint, kind, max_uses FROM beauty_coupons WHERE client_id=$1", [c.clientId])).rows;
    expect(cp).toMatchObject({ code: null, kind: "personal", max_uses: 1, code_hash: hashCode(code), code_hint: codeHint(code) });
    expect(m.actions.rows[0][0].data).toMatch(/^co:[A-Za-z0-9_-]{22}$/);
    expect(m.actions.rows[1][0]).toEqual({ label: "🔕 Не надсилати акції", data: "i:mkoff" });
    const [kb] = (await q("SELECT kind, token, campaign_recipient_id, status, conversation_id, expires_at FROM beauty_chat_keyboards WHERE message_id=$1", [m.id])).rows;
    expect(kb).toMatchObject({ kind: "campaign_offer", campaign_recipient_id: c.recipientId, status: "active", conversation_id: c.convId });
    expect(kb.token).toHaveLength(22);
    expect(m.actions.rows[0][0].data).toBe(`co:${kb.token}`);
    expect(kb.expires_at.toISOString()).toBe("2026-12-01T00:00:00.000Z");
    expect(await recipient(c.recipientId)).toMatchObject({ status: "queued", message_id: m.id, coupon_id: expect.any(String) });
    await pollCampaigns(ctx); // rerun: no duplicate
    expect(await msgCount(s.tenantId)).toBe(1);
    await pollOutbox(ctx);
    expect(sent).toHaveLength(1);
    expect(await recipient(c.recipientId)).toMatchObject({ status: "sent" });
    await pollCampaigns(ctx);
    expect(await campaignStatus(s.campaignId)).toBe("done");
  });

  it("channel-without-buttons_text-has-code-instruction-and-no-keyboard", async () => {
    const s = await seed({ buttons: false });
    const c = await addClient(s);
    await pollCampaigns(makeCtx([s.tenantId]).ctx);
    const [m] = (await q("SELECT body, actions FROM beauty_messages WHERE tenant_id=$1", [s.tenantId])).rows;
    expect(m.actions).toBeNull();
    expect(m.body).toContain("напишіть сюди код");
    expect(m.body).toContain("«не надсилати акції»");
    expect((await q("SELECT 1 FROM beauty_chat_keyboards WHERE tenant_id=$1", [s.tenantId])).rowCount).toBe(0);
    expect(await recipient(c.recipientId)).toMatchObject({ status: "queued" });
  });

  it("telegram-403_marks-recipient-failed-bot_blocked_without-retries_and-revokes-coupon", async () => {
    const s = await seed();
    const c = await addClient(s);
    let calls = 0;
    const { ctx } = makeCtx([s.tenantId], { send: async () => { calls++; throw new ChannelSendError("Telegram sendMessage failed with HTTP 403", true, "bot_blocked"); } });
    await pollCampaigns(ctx);
    await pollOutbox(ctx); await pollOutbox(ctx);
    expect(calls).toBe(1);
    expect(await recipient(c.recipientId)).toMatchObject({ status: "failed", skip_reason: "bot_blocked" });
    expect((await q("SELECT status FROM beauty_coupons WHERE client_id=$1", [c.clientId])).rows).toEqual([{ status: "revoked" }]);
    expect((await q("SELECT status, last_error FROM beauty_messages WHERE tenant_id=$1", [s.tenantId])).rows).toEqual([{ status: "failed", last_error: "bot_blocked" }]);
  });

  it("logs_carry-no-names-texts-codes-or-chat-ids", async () => {
    const s = await seed();
    await addClient(s);
    const { ctx, logs, lines } = makeCtx([s.tenantId]);
    await pollCampaigns(ctx);
    // TASK-791 (M1): the code is only readable before delivery (the body is masked once the message is sent), so read it between the passes
    const [m] = (await q("SELECT body FROM beauty_messages WHERE tenant_id=$1", [s.tenantId])).rows;
    const code = /Ваш код: ([0-9A-Z]{5}-[0-9A-Z]{5})/.exec(m.body)![1]!;
    await pollOutbox(ctx);
    const all = JSON.stringify(logs) + lines.join("\n");
    for (const bad of [NAME, "Олена", "Манікюр", code, normalizeCode(code), hashCode(code), CHAT, PHONE, "знижка"]) expect(all).not.toContain(bad);
    expect(all).toContain("campaign_recipient");
  });
});

describe("pure helpers", () => {
  it("coupon-code_format-normalize-hash", () => {
    expect(generatePersonalCode()).toMatch(/^[0-9ABCDEFGHJKMNPQRSTVWXYZ]{10}$/);
    expect(normalizeCode("abcde-0Oil1")).toBe("ABCDE00111");
    expect(hashCode("abcde-fghjk")).toBe(hashCode("ABCDEFGHJK"));
    expect(hashCode("ABCDEFGHJK")).toMatch(/^[0-9a-f]{64}$/);
    expect(codeHint("ABCDE-FGHJK")).toBe("HJK");
  });

  it("renderCampaignText_substitutes_and-caps-length", () => {
    const text = renderCampaignText({
      template: "{name} {code} " + "x".repeat(5000), type: "promo", clientName: "Клієнт Telegram", discountType: "fixed", discountValue: 100, currencyLabel: "грн",
      serviceNames: ["A", "B", "C", "D"], validFrom: new Date("2026-11-01T00:00:00Z"), validTo: null, timeZone: "Europe/Kyiv", code: "ABCDEFGHJK", buttons: true,
    });
    expect([...text].length).toBeLessThanOrEqual(3500);
    expect(text).toContain("Вітаємо ABCDE-FGHJK");
    expect(text).toContain("100 грн на A, B, C та ще 1");
    expect(text).toContain("без обмеження строку");
    expect(text).toContain("«Стоп»");
  });
});
