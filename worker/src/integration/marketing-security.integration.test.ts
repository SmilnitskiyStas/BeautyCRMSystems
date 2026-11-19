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
import { campaignOptions, greetingName, renderCampaignText } from "../jobs/beauty-campaign";
import { sanitizeLine } from "../jobs/reminder-time";
import type { BeautyDeps, StoredMessage } from "../ports";
import { pollCampaigns, pollOutbox, pollWinback, type PollContext } from "../scheduler/pollers";

/**
 * TASK-791 (security review of marketing): M1 code masking, M3 least-privilege grants (taken from scripts/dev/setup-db.ps1 itself),
 * M4 kill switch, M6/L1/L5/L9/L11. The worker runs as a role that has ONLY the GRANTs of the dev script, so a missing GRANT shows up as 42501.
 */
const NOON = new Date("2026-11-10T10:00:00Z"); // Kyiv 12:00
const NIGHT = new Date("2026-11-10T22:30:00Z"); // Kyiv 00:30
const KEY = randomBytes(32);
const WORKER = "beauty_worker_min";
const APP = "beauty_app_min";
const BIDI = "‮​⁦";

let container: StartedPostgreSqlContainer;
let admin: pg.Pool;
let db: TenantDb;
let lockPool: pg.Pool;
let appPool: pg.Pool;

/** The GRANT/REVOKE section of setup-db.ps1 for one role (role names substituted), so the test checks the real script. */
function scriptGrants(role: "worker" | "app"): string {
  const text = readFileSync(new URL("../../../scripts/dev/setup-db.ps1", import.meta.url), "utf8");
  const body = /\$grants = @"\r?\n([\s\S]*?)\r?\n"@/.exec(text)![1]!;
  const [appPart, workerPart] = body.split(/-- Роль воркера[^\n]*\n/);
  return (role === "app" ? appPart! : workerPart!).replace(/beautycrm_app/g, APP).replace(/beautycrm_worker/g, WORKER);
}

beforeAll(async () => {
  container = await new PostgreSqlContainer("postgres:16-alpine").start();
  admin = new pg.Pool({ connectionString: container.getConnectionUri() });
  await admin.query(readFileSync(new URL("../testing/schema.sql", import.meta.url), "utf8"));
  await admin.query(`CREATE ROLE ${WORKER} LOGIN PASSWORD 'pw' NOSUPERUSER NOBYPASSRLS`);
  await admin.query(`CREATE ROLE ${APP} LOGIN PASSWORD 'pw' NOSUPERUSER NOBYPASSRLS`);
  await admin.query(`GRANT USAGE ON SCHEMA public TO ${APP}, ${WORKER}`);
  await admin.query(scriptGrants("app"));
  await admin.query(scriptGrants("worker"));
  const u = new URL(container.getConnectionUri());
  u.username = WORKER; u.password = "pw";
  db = TenantDb.create(u.toString(), 8);
  lockPool = new pg.Pool({ connectionString: u.toString(), max: 8 });
  const a = new URL(container.getConnectionUri());
  a.username = APP; a.password = "pw";
  appPool = new pg.Pool({ connectionString: a.toString(), max: 2 });
}, 180_000);

afterAll(async () => {
  await appPool?.end(); await lockPool?.end(); await db?.close(); await admin?.end(); await container?.stop();
});

interface Seed { tenantId: string; campaignId: string; userId: string; loc: string; svc: string; promo: string | null; spec: string; channelId: string }
interface ClientSeed { clientId: string; recipientId: string; convId: string }
const q = (sql: string, p: unknown[] = []) => admin.query(sql, p);

async function seed(o: { type?: "promo" | "news"; coupon?: "none" | "personal"; tz?: string; modules?: string; name?: string | null } = {}): Promise<Seed> {
  const tenantId = randomUUID();
  const [loc, spec, svc, promo, campaignId, userId, channelId] = Array.from({ length: 7 }, () => randomUUID());
  await q("INSERT INTO tenants (id, name, slug, modules) VALUES ($1,'T',$2,$3::text[])", [tenantId, `t-${tenantId.slice(0, 8)}`, o.modules ?? "{beauty_marketing}"]);
  await q("INSERT INTO users (id, tenant_id, email, full_name, password_hash, role) VALUES ($1,$2,$3,'U','x','owner')", [userId, tenantId, `${userId}@x.test`]);
  await q("INSERT INTO beauty_locations (id, tenant_id, name, timezone, is_active) VALUES ($1,$2,'L',$3,true)", [loc, tenantId, o.tz ?? "Europe/Kyiv"]);
  await q("INSERT INTO beauty_specialists (id, tenant_id, full_name, is_active) VALUES ($1,$2,'S',true)", [spec, tenantId]);
  await q("INSERT INTO beauty_services (id, tenant_id, name, duration_minutes, is_active) VALUES ($1,$2,'Манікюр',60,true)", [svc, tenantId]);
  const type = o.type ?? "promo";
  const coupon = o.coupon ?? (type === "promo" ? "personal" : "none");
  if (type === "promo") {
    await q(`INSERT INTO beauty_promotions (id, tenant_id, name, discount_type, discount_value, starts_at, ends_at, is_active, access)
             VALUES ($1,$2,'P','percent',15,'2026-11-01T00:00:00Z','2026-12-01T00:00:00Z',true,'code_only')`, [promo, tenantId]);
    await q("INSERT INTO beauty_promotion_services (promotion_id, service_id, tenant_id) VALUES ($1,$2,$3)", [promo, svc, tenantId]);
  }
  await q("INSERT INTO beauty_channels (id, tenant_id, type, name, credentials_encrypted, is_active, settings) VALUES ($1,$2,'telegram','ch',$3,true,$4::jsonb)",
    [channelId, tenantId, writeCredentials(KEY, { token: "TG-TOKEN", webhookSecret: "s" }), JSON.stringify({ assistant: { enabled: true } })]);
  await insertCampaign(tenantId, campaignId, userId, type, type === "promo" ? promo! : null, coupon);
  return { tenantId, campaignId, userId, loc, svc, promo: type === "promo" ? promo! : null, spec, channelId };
}

async function insertCampaign(tenantId: string, id: string, userId: string, type: "promo" | "news", promo: string | null, coupon: "none" | "personal") {
  await q(`INSERT INTO beauty_campaigns (id, tenant_id, name, type, promotion_id, segment_kind, template, coupon_mode, coupon_valid_from, coupon_valid_to, status,
             scheduled_at, confirmed_by_user_id, confirmed_at, created_by_user_id)
           VALUES ($1,$2,$3,$4,$5,'all_consented',$6,$7,$8,$9,'sending','2026-11-10T00:00:00Z',$10,'2026-11-09T00:00:00Z',$10)`,
    [id, tenantId, `C-${id.slice(0, 6)}`, type, promo, "{name}, знижка {discount} на {service} до {valid_to}",
      coupon, coupon === "personal" ? "2026-11-01T00:00:00Z" : null, coupon === "personal" ? "2026-12-01T00:00:00Z" : null, userId]);
}

let visitSlot = 0;
async function addClient(s: Seed, o: { name?: string; lastVisit?: Date; channelType?: string; campaignId?: string; recipient?: boolean } = {}): Promise<ClientSeed> {
  const [clientId, convId, recipientId] = [randomUUID(), randomUUID(), randomUUID()];
  await q("INSERT INTO beauty_clients (id, tenant_id, full_name, phone, marketing_consent) VALUES ($1,$2,$3,'+380501234567',false)", [clientId, s.tenantId, o.name ?? "Олена Тестова"]);
  let channelId = s.channelId;
  if (o.channelType) {
    channelId = randomUUID();
    await q("INSERT INTO beauty_channels (id, tenant_id, type, name, credentials_encrypted, is_active, settings) VALUES ($1,$2,$3,'ch2',$4,true,'{}'::jsonb)",
      [channelId, s.tenantId, o.channelType, writeCredentials(KEY, { token: "T", webhookSecret: "s" })]);
  }
  await q("INSERT INTO beauty_conversations (id, tenant_id, channel_id, client_id, external_chat_id) VALUES ($1,$2,$3,$4,$5)", [convId, s.tenantId, channelId, clientId, `777${clientId.slice(0, 6)}`]);
  await q("INSERT INTO beauty_marketing_consents (tenant_id, client_id, channel, level, source, text_version) VALUES ($1,$2,'telegram','full','chat','mc-v1')", [s.tenantId, clientId]);
  if (o.lastVisit) {
    await q(`INSERT INTO beauty_appointments (tenant_id, location_id, specialist_id, service_id, client_id, starts_at, duration_minutes, status, source, price_original, price_final, reminder_option)
             VALUES ($1,$2,$3,$4,$5,$6,60,'completed','admin',100,100,'none')`, [s.tenantId, s.loc, s.spec, s.svc, clientId, new Date(o.lastVisit.getTime() + (visitSlot++) * 2 * 3_600_000)]); // distinct slots: one specialist
  }
  if (o.recipient !== false) {
    await q("INSERT INTO beauty_campaign_recipients (id, tenant_id, campaign_id, client_id) VALUES ($1,$2,$3,$4)", [recipientId, s.tenantId, o.campaignId ?? s.campaignId, clientId]);
  }
  return { clientId, recipientId, convId };
}

function makeCtx(tenantIds: string[], o: { now?: Date; send?: (m: StoredMessage) => Promise<void>; limit?: number; extra?: Partial<PollContext> } = {}) {
  const sent: StoredMessage[] = [];
  const lines: string[] = [];
  const logger: Logger = { info: (m, f) => { lines.push(JSON.stringify({ m, ...f })); }, error: (m, f) => { lines.push(JSON.stringify({ m, ...f })); } };
  const now = () => o.now ?? NOON;
  const deps: BeautyDeps = {
    now, data: new PgBeautyData(db, now),
    sender: { send: async (m) => { if (o.send) await o.send(m); sent.push(m); } },
    idempotency: new PgAdvisoryIdempotency(lockPool),
    log: { append: async () => {} },
    queue: new PgQueuePort(), marketingDailyLimit: o.limit,
  };
  const ctx: PollContext = { deps, store: new PgPollStore(db), tenants: new StaticTenantDirectory(tenantIds), logger, backoffMs: 0, ...o.extra };
  return { ctx, sent, lines };
}
const recipient = async (id: string) => (await q("SELECT status, skip_reason FROM beauty_campaign_recipients WHERE id=$1", [id])).rows[0];
const bodies = async (t: string) => (await q("SELECT body, status, idempotency_key FROM beauty_messages WHERE tenant_id=$1 ORDER BY created_at", [t])).rows as { body: string; status: string; idempotency_key: string }[];
const CODE = /[0-9A-HJKMNP-TV-Z]{5}-[0-9A-HJKMNP-TV-Z]{5}/;
const OLD = new Date("2026-08-01T10:00:00Z");

describe("M3 grants (setup-db.ps1)", () => {
  it("app-role_consent-events-are-append-only_other-tables-stay-writable", async () => {
    const t = randomUUID(); const client = randomUUID();
    await q("INSERT INTO tenants (id, name, slug) VALUES ($1,'T',$2)", [t, `t-${t.slice(0, 8)}`]);
    await q("INSERT INTO beauty_clients (id, tenant_id, full_name) VALUES ($1,$2,'A')", [client, t]);
    const c = await appPool.connect();
    try {
      await c.query("BEGIN"); await c.query("SELECT set_config('app.tenant_id', $1, true)", [t]);
      await c.query(`INSERT INTO beauty_marketing_consent_events (tenant_id, client_id, channel, level_from, level_to, source, text_version)
                     VALUES ($1,$2,'telegram','none','full','chat','mc-v1')`, [t, client]);
      await expect(c.query("SAVEPOINT s1")).resolves.toBeDefined();
      await expect(c.query("UPDATE beauty_marketing_consent_events SET source = 'staff'")).rejects.toMatchObject({ code: "42501" });
      await c.query("ROLLBACK TO s1");
      await expect(c.query("DELETE FROM beauty_marketing_consent_events")).rejects.toMatchObject({ code: "42501" });
      await c.query("ROLLBACK TO s1");
      await expect(c.query("UPDATE beauty_clients SET full_name = 'B'")).resolves.toBeDefined();
    } finally { await c.query("ROLLBACK").catch(() => {}); c.release(); }
  });

  it("worker-role_full-campaign-and-winback-cycle-has-no-42501_and-cannot-write-consents", async () => {
    const s = await seed();
    const c = await addClient(s, { lastVisit: OLD });
    const { ctx, sent, lines } = makeCtx([s.tenantId]);
    await pollCampaigns(ctx); await pollOutbox(ctx);
    expect(sent).toHaveLength(1);
    expect(await recipient(c.recipientId)).toMatchObject({ status: "sent" });
    expect(lines.filter((l) => l.includes("42501"))).toEqual([]);
    for (const sql of ["UPDATE beauty_marketing_consents SET level = 'none'", "INSERT INTO beauty_marketing_consent_events (tenant_id, client_id, channel, level_from, level_to, source, text_version) VALUES (gen_random_uuid(), gen_random_uuid(), 'telegram', 'none', 'full', 'chat', 'v')",
      "UPDATE beauty_promotions SET is_active = false", "DELETE FROM beauty_messages", "UPDATE beauty_promotion_services SET service_id = service_id", "UPDATE tenants SET modules = '{}'"]) {
      await expect(db.pool.query(sql), sql).rejects.toMatchObject({ code: "42501" });
    }
    // winback on the same least-privilege role
    const w = await seed({ type: "news" });
    await addClient(w, { lastVisit: OLD, recipient: false });
    expect(await pollWinback(makeCtx([w.tenantId]).ctx)).toEqual({ processed: 1, errors: 0 });
  });
});

describe("M1 coupon code is masked in beauty_messages.body after delivery", () => {
  it("personal-code_present-while-pending_masked-after-sent_with-last3-hint", async () => {
    const s = await seed();
    const c = await addClient(s);
    const { ctx, sent } = makeCtx([s.tenantId]);
    await pollCampaigns(ctx);
    const [pending] = await bodies(s.tenantId);
    const code = CODE.exec(pending!.body)![0];
    const [{ code_hint }] = (await q("SELECT code_hint FROM beauty_coupons WHERE client_id=$1", [c.clientId])).rows;
    await pollOutbox(ctx);
    expect(sent[0]!.text).toContain(code); // the client got the full text
    const [after] = await bodies(s.tenantId);
    expect(after!.status).toBe("sent");
    expect(after!.body).not.toMatch(CODE);
    expect(after!.body).not.toContain(code);
    expect(after!.body).toContain(`[код купона …${code_hint}]`);
    expect(after!.body).toContain("Умови: знижка 15%"); // the rest of the text stays readable
  });

  it("final-failure_masks-code_non-final-retry-keeps-it-for-the-next-attempt", async () => {
    const s = await seed();
    await addClient(s);
    let calls = 0;
    const { ctx } = makeCtx([s.tenantId], { send: async () => { calls++; throw new Error("network down"); } });
    await pollCampaigns(ctx);
    await pollOutbox(ctx); // attempt 1: not final
    let [m] = await bodies(s.tenantId);
    expect(m!.status).toBe("pending");
    expect(m!.body).toMatch(CODE);
    await pollOutbox(ctx); await pollOutbox(ctx); // attempts 2 and 3 -> failed
    [m] = await bodies(s.tenantId);
    expect(calls).toBe(3);
    expect(m!.status).toBe("failed");
    expect(m!.body).not.toMatch(CODE);

    const s2 = await seed(); await addClient(s2);
    const t2 = makeCtx([s2.tenantId], { send: async () => { throw new ChannelSendError("403", true, "bot_blocked"); } });
    await pollCampaigns(t2.ctx); await pollOutbox(t2.ctx);
    expect((await bodies(s2.tenantId))[0]!.body).not.toMatch(CODE);
  });
});

describe("M4 kill switch (flags and beauty_marketing module)", () => {
  it("outbox_flags-off_campaign-and-winback-stay-pending_other-kinds-are-sent", async () => {
    const s = await seed();
    await addClient(s);
    const w = await seed({ type: "news" });
    await addClient(w, { lastVisit: OLD, recipient: false });
    const on = makeCtx([s.tenantId, w.tenantId]);
    await pollCampaigns(on.ctx); await pollWinback(on.ctx);
    // a reminder-like message that is not marketing
    const [conv] = (await q("SELECT id FROM beauty_conversations WHERE tenant_id=$1", [s.tenantId])).rows;
    await q("INSERT INTO beauty_messages (tenant_id, conversation_id, direction, sender_type, body, status, idempotency_key) VALUES ($1,$2,'outbound','system','hi','pending','reminder:'||gen_random_uuid())", [s.tenantId, conv.id]);
    const off = makeCtx([s.tenantId, w.tenantId], { extra: { campaignsEnabled: false, winbackEnabled: false } });
    await pollOutbox(off.ctx);
    expect(off.sent.map((m) => m.kind)).toEqual(["reminder"]);
    expect((await bodies(s.tenantId)).filter((b) => b.idempotency_key.startsWith("campaign:"))[0]!.status).toBe("pending");
    expect((await bodies(w.tenantId))[0]!.status).toBe("pending");
    // only campaigns off -> winback goes out
    const half = makeCtx([s.tenantId, w.tenantId], { extra: { campaignsEnabled: false } });
    await pollOutbox(half.ctx);
    expect(half.sent.map((m) => m.kind)).toEqual(["winback"]);
    // flags on again -> the campaign message is delivered (nothing was lost)
    await pollOutbox(on.ctx);
    expect(on.sent.map((m) => m.kind)).toEqual(["campaign"]);
  });

  it("module-off_pollers-idle_enqueue-defers_outbox-keeps-pending_module-on-resumes", async () => {
    const s = await seed({ modules: "{beauty_booking}" });
    const c = await addClient(s, { lastVisit: OLD });
    const { ctx, sent, lines } = makeCtx([s.tenantId]);
    expect(await pollCampaigns(ctx)).toEqual({ processed: 0, errors: 0 });
    expect(await pollWinback(ctx)).toEqual({ processed: 0, errors: 0 });
    expect(await recipient(c.recipientId)).toMatchObject({ status: "pending" });
    // the data port itself refuses too (defence in depth)
    expect(await withTenant(s.tenantId, () => ctx.deps.data.enqueueCampaignMessage(c.recipientId, NOON, campaignOptions(ctx.deps)))).toBe("deferred_disabled");
    expect(await recipient(c.recipientId)).toMatchObject({ status: "pending" });
    // a message queued earlier stays pending while the module is off
    await q("UPDATE tenants SET modules = '{beauty_marketing}' WHERE id=$1", [s.tenantId]);
    await pollCampaigns(ctx);
    await q("UPDATE tenants SET modules = '{beauty_booking}' WHERE id=$1", [s.tenantId]);
    await pollOutbox(ctx);
    expect(sent).toHaveLength(0);
    expect((await bodies(s.tenantId))[0]!.status).toBe("pending");
    expect(lines).toEqual([]);
    await q("UPDATE tenants SET modules = '{beauty_marketing}' WHERE id=$1", [s.tenantId]);
    await pollOutbox(ctx);
    expect(sent).toHaveLength(1);
  });
});

describe("M6 winback", () => {
  it("telegram-only_name-sanitized_window-like-campaigns", async () => {
    const s = await seed({ type: "news" });
    const tg = await addClient(s, { lastVisit: OLD, recipient: false, name: `${BIDI}Іринка Петрівна` });
    const ig = await addClient(s, { lastVisit: OLD, recipient: false, channelType: "instagram" });
    const nameless = await addClient(s, { lastVisit: OLD, recipient: false, name: "Клієнт Telegram" });
    // night: nothing is created
    expect(await pollWinback(makeCtx([s.tenantId], { now: NIGHT }).ctx)).toEqual({ processed: 0, errors: 0 });
    expect(await bodies(s.tenantId)).toHaveLength(0);
    // day: Telegram clients only
    const { ctx, sent } = makeCtx([s.tenantId]);
    expect(await pollWinback(ctx)).toEqual({ processed: 2, errors: 0 });
    await pollOutbox(ctx);
    expect(sent.map((m) => m.clientId).sort()).toEqual([tg.clientId, nameless.clientId].sort());
    expect(sent.map((m) => m.clientId)).not.toContain(ig.clientId);
    const texts = sent.map((m) => m.text);
    expect(texts.find((t) => t.startsWith("Іринка, ми давно"))).toBeDefined();
    expect(texts.find((t) => t.startsWith("Вітаємо, ми давно"))).toBeDefined();
    for (const t of texts) expect(t).not.toMatch(/[‮​⁦]/);
  });

  it("daily-limit-and-frequency_hold-under-parallel-passes_one-message-per-slot", async () => {
    const s = await seed({ type: "news" });
    for (let i = 0; i < 4; i++) await addClient(s, { lastVisit: OLD, recipient: false });
    const a = makeCtx([s.tenantId], { limit: 2 }); const b = makeCtx([s.tenantId], { limit: 2 });
    await Promise.all([pollWinback(a.ctx), pollWinback(b.ctx)]);
    expect(await bodies(s.tenantId)).toHaveLength(2);
    // frequency: a campaign recipient of an already-nudged client is skipped
    const [done] = (await q("SELECT conversation_id FROM beauty_messages WHERE tenant_id=$1 LIMIT 1", [s.tenantId])).rows;
    const [{ client_id }] = (await q("SELECT client_id FROM beauty_conversations WHERE id=$1", [done.conversation_id])).rows;
    const rid = randomUUID();
    await q("INSERT INTO beauty_campaign_recipients (id, tenant_id, campaign_id, client_id) VALUES ($1,$2,$3,$4)", [rid, s.tenantId, s.campaignId, client_id]);
    await pollCampaigns(makeCtx([s.tenantId], { limit: 50 }).ctx);
    expect(await recipient(rid)).toMatchObject({ status: "skipped", skip_reason: "frequency" });
  });
});

describe("L1/L5 atomic frequency and daily limit", () => {
  it("two-campaigns-same-client-in-parallel_exactly-one-is-queued", async () => {
    const s = await seed({ type: "news" });
    const b = randomUUID();
    await insertCampaign(s.tenantId, b, s.userId, "news", null, "none");
    const c = await addClient(s);
    const r2 = randomUUID();
    await q("INSERT INTO beauty_campaign_recipients (id, tenant_id, campaign_id, client_id) VALUES ($1,$2,$3,$4)", [r2, s.tenantId, b, c.clientId]);
    const { ctx } = makeCtx([s.tenantId]);
    const opts = campaignOptions(ctx.deps);
    const res = await withTenant(s.tenantId, () => Promise.all([
      ctx.deps.data.enqueueCampaignMessage(c.recipientId, NOON, opts), ctx.deps.data.enqueueCampaignMessage(r2, NOON, opts),
    ]));
    expect(res.filter((x) => x === "queued")).toHaveLength(1);
    expect(res.filter((x) => x === "skipped:frequency")).toHaveLength(1);
    expect(await bodies(s.tenantId)).toHaveLength(1);
  });

  it("daily-limit_parallel-enqueue-and-parallel-send_never-exceed-the-limit", async () => {
    const s = await seed({ type: "news" });
    const cs = await Promise.all([1, 2, 3, 4, 5].map(() => addClient(s)));
    const { ctx } = makeCtx([s.tenantId], { limit: 2 });
    const opts = campaignOptions(ctx.deps, "Europe/Kyiv");
    const res = await withTenant(s.tenantId, () => Promise.all(cs.map((c) => ctx.deps.data.enqueueCampaignMessage(c.recipientId, NOON, { ...opts, dailyLimit: 2 }))));
    expect(res.filter((x) => x === "queued")).toHaveLength(2);
    expect(res.filter((x) => x === "deferred_limit")).toHaveLength(3);
    // send side: 5 pending campaign messages, limit 2, several outbox passes at once
    const s2 = await seed({ type: "news" });
    const c2 = await Promise.all([1, 2, 3, 4, 5].map(() => addClient(s2)));
    const loose = makeCtx([s2.tenantId], { limit: 100 });
    await pollCampaigns(loose.ctx);
    expect(await bodies(s2.tenantId)).toHaveLength(5);
    expect(c2).toHaveLength(5);
    const strict = Array.from({ length: 4 }, () => makeCtx([s2.tenantId], { limit: 2 }));
    await Promise.all(strict.map((x) => pollOutbox(x.ctx)));
    expect(strict.reduce((n, x) => n + x.sent.length, 0)).toBe(2);
    expect((await bodies(s2.tenantId)).filter((b) => b.status === "pending")).toHaveLength(3);
  });
});

describe("L11 invalid venue time zone", () => {
  it("recipient_is-skipped-with-reason_no-error-logs_winback-skips-too", async () => {
    const s = await seed({ type: "news" });
    await q("UPDATE beauty_locations SET timezone = 'Not/AZone' WHERE tenant_id=$1", [s.tenantId]).catch(() => undefined);
    const tzNow = (await q("SELECT timezone FROM beauty_locations WHERE tenant_id=$1", [s.tenantId])).rows[0].timezone;
    expect(tzNow).toBe("Not/AZone");
    const c = await addClient(s, { lastVisit: OLD });
    const w = await addClient(s, { lastVisit: OLD, recipient: false });
    const { ctx, lines } = makeCtx([s.tenantId]);
    expect(await pollCampaigns(ctx)).toEqual({ processed: 0, errors: 0 });
    expect(await recipient(c.recipientId)).toMatchObject({ status: "skipped", skip_reason: "invalid_timezone" });
    expect(await pollWinback(ctx)).toEqual({ processed: 0, errors: 0 });
    expect(await pollWinback(ctx)).toEqual({ processed: 0, errors: 0 });
    await pollOutbox(ctx);
    expect(lines).toEqual([]);
    expect(await bodies(s.tenantId)).toHaveLength(0);
    expect(w.clientId).toBeDefined();
  });
});

describe("L9 invisible format characters", () => {
  it("sanitizeLine_strips-bidi-and-zero-width_but-keeps-text", () => {
    expect(sanitizeLine(`${BIDI}Оле‍на﻿  Тест­`)).toBe("Олена Тест");
    expect(greetingName(`‮evil Name`)).toBe("evil");
  });
  it("renderCampaignText_strips-cf-from-name-and-template", () => {
    const text = renderCampaignText({
      template: "{name}‮, акція​!", type: "news", clientName: `${BIDI}Олена`, currencyLabel: "грн", serviceNames: [],
      validFrom: null, validTo: null, timeZone: "Europe/Kyiv", buttons: true,
    });
    expect(text).not.toMatch(/\p{Cf}/u);
    expect(text.startsWith("Олена, акція!")).toBe(true);
  });
});
