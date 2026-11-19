import { randomBytes, randomUUID } from "node:crypto";
import { readFileSync } from "node:fs";
import pg from "pg";
import { PostgreSqlContainer, type StartedPostgreSqlContainer } from "@testcontainers/postgresql";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { ChannelSendError, TELEGRAM_API, sendTelegramText } from "../adapters/channel-sender";
import { protect, writeCredentials } from "../adapters/channel-crypto";
import { PgBeautyData } from "../adapters/pg-data";
import { TenantDb, withTenant } from "../db/tenant-db";
import { StaticTenantDirectory } from "../db/tenants";
import { DEFAULT_TELEGRAM_CONFIG, PgInventoryTelegram, type InventoryTelegramConfig, type TelegramSend } from "../inventory/telegram";
import type { BeautyDeps } from "../ports";
import { pollInventoryTelegram, type PollContext } from "../scheduler/pollers";

/** TASK-896: pollInventoryTelegram against real PostgreSQL as a role with ONLY the worker GRANTs of scripts/dev/setup-db.ps1. */
const WORKER = "beauty_worker_tg";
const APP = "beauty_app_tg";
const KEY = Buffer.alloc(32, 9);
const MIN = 60_000, HOUR = 3_600_000;
// Kyiv is UTC+2 in November: 10:00Z = 12:00 local (allowed hours)
const NOW = new Date("2026-11-10T10:00:00Z");
const ago = (ms: number, from = NOW) => new Date(from.getTime() - ms);
const CHAT = "777000111";
const BOT = "BOTTOKEN123";

let container: StartedPostgreSqlContainer;
let admin: pg.Pool;
let db: TenantDb;

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
}, 180_000);

afterAll(async () => { await db?.close(); await admin?.end(); await container?.stop(); });

const q = (sql: string, p: unknown[] = []) => admin.query(sql, p);

interface T { id: string; user: string; loc: string; channel: string; token: string }
async function tenant(o: { modules?: string; tz?: string; chActive?: boolean } = {}): Promise<T> {
  const id = randomUUID(), user = randomUUID(), loc = randomUUID(), channel = randomUUID(), token = `${BOT}-${id.slice(0, 6)}`;
  await q("INSERT INTO tenants (id, name, slug, modules) VALUES ($1,'T',$2,$3::text[])", [id, `t-${id.slice(0, 8)}`, o.modules ?? "{beauty_inventory}"]);
  await q("INSERT INTO users (id, tenant_id, email, full_name, password_hash, role) VALUES ($1,$2,$3,'U','x','owner')", [user, id, `${user}@x.test`]);
  await q("INSERT INTO beauty_locations (id, tenant_id, name, timezone, is_active) VALUES ($1,$2,'Салон',$3,true)", [loc, id, o.tz ?? "Europe/Kyiv"]);
  await q("INSERT INTO beauty_channels (id, tenant_id, type, name, credentials_encrypted, is_active) VALUES ($1,$2,'telegram','bot',$3,$4)",
    [channel, id, writeCredentials(KEY, { token }), o.chActive ?? true]);
  return { id, user, loc, channel, token };
}
async function location(t: T, tz: string, name = "L2"): Promise<string> {
  const id = randomUUID();
  await q("INSERT INTO beauty_locations (id, tenant_id, name, timezone, is_active) VALUES ($1,$2,$3,$4,true)", [id, t.id, name, tz]);
  return id;
}
async function user(t: T, o: { role?: string; active?: boolean } = {}): Promise<string> {
  const id = randomUUID();
  await q("INSERT INTO users (id, tenant_id, email, full_name, password_hash, role, is_active) VALUES ($1,$2,$3,'U','x',$4,$5)", [id, t.id, `${id}@x.test`, o.role ?? "owner", o.active ?? true]);
  return id;
}
/** Active link activated `activatedAgo` before NOW. */
async function link(t: T, o: { userId?: string; activatedAgo?: number; chat?: string } = {}): Promise<string> {
  const id = randomUUID(), at = ago(o.activatedAgo ?? 6 * HOUR);
  await q(`INSERT INTO beauty_staff_telegram_links (id, tenant_id, user_id, channel_id, status, chat_id_encrypted, chat_id_hash, consent_version, consent_at, claimed_at, bot_confirmed_at, activated_at)
           VALUES ($1,$2,$3,$4,'active',$5,$6,'inv-tg-v1',$7,$7,$7,$7)`, [id, t.id, o.userId ?? t.user, t.channel, protect(KEY, o.chat ?? CHAT), randomBytes(32), at]);
  return id;
}
let prodCache = new Map<string, string>();
async function product(t: T, name = "SecretGel"): Promise<string> {
  const k = `${t.id}:${name}`;
  if (prodCache.has(k)) return prodCache.get(k)!;
  const id = randomUUID();
  await q("INSERT INTO beauty_products (id, tenant_id, name, unit, purpose, min_stock, is_active, updated_by_user_id) VALUES ($1,$2,$3,'pcs','both',10,true,$4)", [id, t.id, name, t.user]);
  prodCache.set(k, id);
  return id;
}
async function alert(t: T, o: { created?: Date; status?: string; loc?: string; kind?: "low_stock" | "expiring" | "shortage"; product?: string } = {}): Promise<string> {
  const id = randomUUID(), loc = o.loc ?? t.loc, kind = o.kind ?? "low_stock", p = await product(t, o.product);
  let batch: string | null = null, op: string | null = null;
  if (kind !== "low_stock") {
    op = randomUUID();
    await q("INSERT INTO beauty_stock_operations (id, tenant_id, kind, location_id) VALUES ($1,$2,'receipt',$3)", [op, t.id, loc]);
  }
  if (kind === "expiring") {
    batch = randomUUID();
    await q(`INSERT INTO beauty_stock_batches (id, tenant_id, location_id, product_id, operation_id, received_at, expires_on, initial_quantity, quantity, lot_number)
             VALUES ($1,$2,$3,$4,$5,now(),'2026-12-05',10,4,'LOT-7')`, [batch, t.id, loc, p, op]);
    op = null;
  }
  const status = o.status ?? "open";
  await q(`INSERT INTO beauty_staff_alerts (id, tenant_id, kind, location_id, product_id, batch_id, operation_id, quantity, threshold, expires_on, days_bucket, dedupe_key, status, read_at, resolved_at, created_at, updated_at)
           VALUES ($1,$2,$3,$4,$5,$6,$7,3,$8,$9,$10,$11,$12,$13,$14,$15,$15)`,
    [id, t.id, kind, loc, p, batch, op, kind === "low_stock" ? 10 : null, kind === "expiring" ? "2026-12-05" : null, kind === "expiring" ? 30 : null, `k:${id}`, status,
      status === "read" ? NOW : null, status === "resolved" ? NOW : null, o.created ?? ago(HOUR)]);
  return id;
}
const notes = async (t: T) => (await q("SELECT * FROM beauty_staff_notifications WHERE tenant_id = $1 ORDER BY created_at, id", [t.id])).rows;
const linkRow = async (id: string) => (await q("SELECT * FROM beauty_staff_telegram_links WHERE id = $1", [id])).rows[0];

interface Call { token: string; chatId: string; text: string }
function fake(behaviour: (c: Call, n: number) => Promise<{ externalMessageId: string | null }> | { externalMessageId: string | null } | void = () => undefined) {
  const calls: Call[] = [];
  const send: TelegramSend = async (token, chatId, text) => {
    const c = { token, chatId, text }; calls.push(c);
    return (await behaviour(c, calls.length)) ?? { externalMessageId: String(1000 + calls.length) };
  };
  return { calls, send };
}

function ctx(tenants: T[], now: Date, send: TelegramSend, over: Partial<PollContext> = {}, cfg: InventoryTelegramConfig = DEFAULT_TELEGRAM_CONFIG, sleeps: number[] = [], logs: string[] = []): PollContext {
  const deps = { now: () => now, data: new PgBeautyData(db, () => now) } as unknown as BeautyDeps;
  return {
    deps, store: {} as never, tenants: new StaticTenantDirectory(tenants.map((t) => t.id)),
    logger: { info: (m) => { logs.push(m); }, error: (m) => { logs.push(m); } },
    inventoryTelegram: new PgInventoryTelegram(db, KEY, send, { info: (m) => { logs.push(m); }, error: (m) => { logs.push(m); } }, { sleep: async (ms) => { sleeps.push(ms); }, gapMs: 1_100 }),
    inventoryTelegramConfig: cfg, inventoryTelegramEnabled: true, ...over,
  };
}
const run = (tenants: T[], now: Date, send: TelegramSend, over: Partial<PollContext> = {}, cfg?: InventoryTelegramConfig, sleeps?: number[], logs?: string[]) =>
  pollInventoryTelegram(ctx(tenants, now, send, over, cfg, sleeps, logs));

describe("pollInventoryTelegram (PostgreSQL)", () => {
  it("gates: flag off (any value but true) and module off build and send nothing", async () => {
    const on = await tenant(), off = await tenant({ modules: "{beauty_marketing}" });
    for (const t of [on, off]) { await link(t); await alert(t); }
    const f = fake();
    await run([on, off], NOW, f.send, { inventoryTelegramEnabled: false });
    await run([on, off], NOW, f.send, { inventoryTelegramEnabled: undefined });
    expect(f.calls).toHaveLength(0);
    expect((await notes(on)).length + (await notes(off)).length).toBe(0);
    await run([on, off], NOW, f.send);
    expect(await notes(on)).toHaveLength(1);
    expect(await notes(off)).toHaveLength(0);
    expect(f.calls).toHaveLength(1);
    expect(f.calls[0]).toMatchObject({ token: on.token, chatId: CHAT });
  });

  it("selects only open alerts created after activation and not older than 24h", async () => {
    const t = await tenant();
    await link(t, { activatedAgo: 48 * HOUR });
    const old = await alert(t, { created: ago(25 * HOUR) });
    const fresh = await alert(t, { created: ago(23 * HOUR) });
    await alert(t, { status: "read" }); await alert(t, { status: "resolved" });
    const f = fake();
    await run([t], NOW, f.send);
    const n = await notes(t);
    expect(n).toHaveLength(1);
    const used = (await q("SELECT alert_id FROM beauty_staff_notification_alerts WHERE tenant_id = $1", [t.id])).rows.map((r) => r.alert_id);
    expect(used).toEqual([fresh]); expect(used).not.toContain(old);

    const t2 = await tenant();
    await link(t2, { activatedAgo: 2 * HOUR });
    await alert(t2, { created: ago(3 * HOUR) }); // before activation
    const ok = await alert(t2, { created: ago(HOUR) });
    await run([t2], NOW, f.send);
    expect((await q("SELECT alert_id FROM beauty_staff_notification_alerts WHERE tenant_id = $1", [t2.id])).rows.map((r) => r.alert_id)).toEqual([ok]);
  });

  it("M1: pre-L3 allowlist processes only the listed tenants", async () => {
    const a = await tenant(), b = await tenant();
    for (const t of [a, b]) { await link(t); await alert(t); }
    const f = fake();
    await run([a, b], NOW, f.send, { inventoryTelegramTenantAllowlist: new Set([a.id.toLowerCase()]) });
    expect(await notes(a)).toHaveLength(1);
    expect(await notes(b)).toHaveLength(0);
    expect(f.calls).toHaveLength(1);
  });

  it("L5/I7: a link revoked during the send does not get last_delivery_at written back", async () => {
    const t = await tenant();
    const l = await link(t);
    await alert(t);
    const f = fake(async () => {
      await q(`UPDATE beauty_staff_telegram_links SET status = 'revoked', chat_id_encrypted = NULL, chat_id_hash = NULL, token_hash = NULL, revoked_at = now(), revoke_reason = 'user_deactivated' WHERE id = $1`, [l]);
      return { externalMessageId: "1" };
    });
    await run([t], NOW, f.send);
    const row = await linkRow(l);
    expect(row.status).toBe("revoked");
    expect(row.last_delivery_at).toBeNull();
  });

  it("sends once: state sent, link.last_delivery_at, no duplicates across runs, text has no PII/ids", async () => {
    const t = await tenant();
    const l = await link(t);
    const a = await alert(t, { kind: "shortage" }), e = await alert(t, { kind: "expiring" });
    const f = fake(); const logs: string[] = [];
    await run([t], NOW, f.send, {}, undefined, [], logs);
    await run([t], new Date(NOW.getTime() + 5 * MIN), f.send, {}, undefined, [], logs);
    await run([t], new Date(NOW.getTime() + 10 * MIN), f.send, {}, undefined, [], logs);
    const n = await notes(t);
    expect(n).toHaveLength(2);
    expect(n.every((r) => r.status === "sent" && r.sent_at && r.attempts === 1 && r.external_message_id)).toBe(true);
    expect(f.calls).toHaveLength(2);
    expect((await linkRow(l)).last_delivery_at).not.toBeNull();
    const bodies = f.calls.map((c) => c.text).join("\n");
    expect(bodies).toContain("Нестача при списанні на послугу: SecretGel — бракує 3 pcs.");
    expect(bodies).toContain("Закінчується термін: SecretGel, партія LOT-7 — 3 pcs, до 05.12.2026.");
    const forbidden = [a, e, t.user, t.id, "price", "appointment"];
    for (const x of forbidden) expect(bodies).not.toContain(x);
    // logs: no chat id, token, body, product name
    const all = logs.join("\n");
    for (const x of [CHAT, t.token, "SecretGel", "Нестача"]) expect(all).not.toContain(x);
    expect(all).toMatch(new RegExp(`inventory_telegram tenant=${t.id} built=2 sent=2 failed=0 revoked=0 capped=false`));
    expect(JSON.stringify(n)).not.toContain(CHAT);
  });

  it("includes the admin link only when WORKER_ADMIN_BASE_URL is configured", async () => {
    const t = await tenant(); await link(t); await alert(t);
    const f = fake();
    await run([t], NOW, f.send, {}, { ...DEFAULT_TELEGRAM_CONFIG, adminBaseUrl: "https://crm.example.com" });
    expect(f.calls[0]!.text.endsWith(`\nhttps://crm.example.com/beauty/inventory/alerts?locationId=${t.loc}`)).toBe(true);
  });

  it("quiet hours 21:00-08:00 local: waits, sent in the morning; read-in-between is never sent; DST-safe; per-venue zone", async () => {
    const t = await tenant({ tz: "Europe/Kyiv" });
    await link(t, { activatedAgo: 20 * HOUR });
    const a = await alert(t, { created: ago(HOUR) });
    const f = fake();
    // 19:30Z = 21:30 Kyiv (winter): quiet
    await run([t], new Date("2026-11-10T19:30:00Z"), f.send);
    expect(await notes(t)).toHaveLength(0);
    await run([t], new Date("2026-11-10T20:00:00Z"), f.send);
    expect(await notes(t)).toHaveLength(0);
    // 06:00Z next day = 08:00 Kyiv: allowed (alert is 20h old: still within 24h)
    const morning = new Date("2026-11-11T06:00:00Z");
    await q("UPDATE beauty_staff_alerts SET created_at = $2 WHERE id = $1", [a, ago(8 * HOUR, morning)]);
    await run([t], morning, f.send);
    expect(await notes(t)).toHaveLength(1);

    // alert turned read before morning: not sent
    const t2 = await tenant(); await link(t2, { activatedAgo: 20 * HOUR });
    const b = await alert(t2, { created: new Date("2026-11-10T19:00:00Z") });
    await run([t2], new Date("2026-11-10T21:00:00Z"), f.send);
    await q("UPDATE beauty_staff_alerts SET status = 'read', read_at = now() WHERE id = $1", [b]);
    await run([t2], new Date("2026-11-11T06:00:00Z"), f.send);
    expect(await notes(t2)).toHaveLength(0);
  });

  it("quiet hours across the DST change and with two zones", async () => {
    const t = await tenant({ tz: "Europe/Kyiv" });
    const ny = await location(t, "America/New_York");
    const day = new Date("2026-03-29T12:00:00Z"); // Kyiv DST day (UTC+3 after 01:00Z)
    await link(t, { activatedAgo: 0 }); // activated_at = NOW (2026-11) -> override below
    await q("UPDATE beauty_staff_telegram_links SET activated_at = $2 WHERE tenant_id = $1", [t.id, ago(48 * HOUR, day)]);
    const kyiv = await alert(t, { created: ago(HOUR, day) });
    const nyc = await alert(t, { created: ago(HOUR, day), loc: ny });
    const f = fake();
    // 18:30Z: Kyiv 21:30 (UTC+3; naive +2 would say 20:30 = allowed) -> quiet; New York 14:30 EDT -> allowed
    await run([t], new Date("2026-03-29T18:30:00Z"), f.send);
    const first = (await q("SELECT alert_id FROM beauty_staff_notification_alerts WHERE tenant_id = $1", [t.id])).rows.map((r) => r.alert_id);
    expect(first).toEqual([nyc]);
    // 05:30Z next day is 08:30 Kyiv (UTC+3) -> allowed (naive +2 = 07:30 would still be quiet)
    const next = new Date("2026-03-30T05:30:00Z");
    await q("UPDATE beauty_staff_alerts SET created_at = $2 WHERE id = $1", [kyiv, ago(9 * HOUR, next)]);
    await run([t], next, f.send);
    const all = (await q("SELECT alert_id FROM beauty_staff_notification_alerts WHERE tenant_id = $1", [t.id])).rows.map((r) => r.alert_id).sort();
    expect(all).toEqual([kyiv, nyc].sort());
  });

  it("more than 3 alerts per run -> a single digest covering all of them; <= 3 -> one message each; rerun adds nothing", async () => {
    const t = await tenant(); await link(t);
    const loc2 = await location(t, "Europe/Kyiv", "Інший");
    for (let i = 0; i < 3; i++) await alert(t);
    await alert(t, { kind: "expiring", loc: loc2 });
    const f = fake();
    await run([t], NOW, f.send);
    await run([t], new Date(NOW.getTime() + 5 * MIN), f.send);
    const n = await notes(t);
    expect(n).toHaveLength(1);
    expect(n[0]).toMatchObject({ kind: "digest", status: "sent" });
    expect(n[0].dedupe_key).toMatch(/^digest:[0-9a-f-]{36}:\d{12}$/);
    expect(f.calls[0]!.text).toContain("Склад: 4 нових сповіщень");
    expect(f.calls[0]!.text).toContain("• Салон: мало — 3, термін — 0, нестача — 0");
    expect(f.calls[0]!.text).toContain("• Інший: мало — 0, термін — 1, нестача — 0");
    expect(Number((await q("SELECT count(*) FROM beauty_staff_notification_alerts WHERE tenant_id = $1", [t.id])).rows[0].count)).toBe(4);

    const t2 = await tenant(); await link(t2);
    for (let i = 0; i < 3; i++) await alert(t2);
    await run([t2], NOW, f.send);
    expect((await notes(t2)).map((r) => r.kind)).toEqual(["alert", "alert", "alert"]);
  });

  it("each alert goes to each recipient at most once (two owners, separate chats)", async () => {
    const t = await tenant(); await link(t);
    const u2 = await user(t); await link(t, { userId: u2, chat: "555" });
    await alert(t);
    const f = fake();
    await run([t], NOW, f.send); await run([t], new Date(NOW.getTime() + 5 * MIN), f.send);
    expect(f.calls.map((c) => c.chatId).sort()).toEqual(["555", CHAT].sort());
    await expect(q(`INSERT INTO beauty_staff_notification_alerts (tenant_id, notification_id, alert_id, link_id)
      SELECT tenant_id, notification_id, alert_id, link_id FROM beauty_staff_notification_alerts WHERE tenant_id = $1 LIMIT 1`, [t.id])).rejects.toThrow();
  });

  it("limits to 6 messages per link per rolling hour; the excess waits and goes in the next window", async () => {
    const t = await tenant(); const l = await link(t, { activatedAgo: 30 * HOUR });
    for (let i = 0; i < 5; i++) {
      await q(`INSERT INTO beauty_staff_notifications (tenant_id, link_id, kind, body, status, attempts, next_attempt_at, dedupe_key, sent_at, created_at, updated_at)
               VALUES ($1,$2,'alert','x','sent',1,$3,$4,$3,$3,$3)`, [t.id, l, ago(10 * MIN), `old:${i}`]);
    }
    await alert(t); await alert(t); await alert(t);
    const f = fake(); const logs: string[] = [];
    await run([t], NOW, f.send, {}, undefined, [], logs);
    expect((await notes(t)).filter((r) => r.dedupe_key.startsWith("alert:"))).toHaveLength(1);
    expect(logs.join("\n")).toContain("capped=true");
    // an hour later the window frees: the remaining two (now <= 3) go individually
    await q("UPDATE beauty_staff_notifications SET created_at = $2 WHERE tenant_id = $1 AND dedupe_key LIKE 'old:%'", [t.id, ago(2 * HOUR)]);
    await run([t], new Date(NOW.getTime() + 5 * MIN), f.send);
    expect((await notes(t)).filter((r) => r.dedupe_key.startsWith("alert:"))).toHaveLength(3);
  });

  it("retries with 1 min / 5 min / 30 min / 2 h backoff and fails after 5 attempts; no duplicate bodies", async () => {
    const t = await tenant(); await link(t); await alert(t);
    const f = fake(() => { throw new ChannelSendError("Telegram sendMessage network error", false); });
    let now = NOW;
    await run([t], now, f.send);
    let n = (await notes(t))[0]!;
    expect(n).toMatchObject({ status: "pending", attempts: 1, last_error: "network" });
    expect(new Date(n.next_attempt_at).getTime() - now.getTime()).toBe(1 * MIN);
    // not due yet
    await run([t], new Date(now.getTime() + 30_000), f.send);
    expect(f.calls).toHaveLength(1);
    const expected = [5 * MIN, 30 * MIN, 2 * HOUR];
    for (let i = 0; i < 3; i++) {
      now = new Date(new Date(n.next_attempt_at).getTime());
      await run([t], now, f.send);
      n = (await notes(t))[0]!;
      expect(n).toMatchObject({ status: "pending", attempts: i + 2 });
      expect(new Date(n.next_attempt_at).getTime() - now.getTime()).toBe(expected[i]);
    }
    now = new Date(n.next_attempt_at);
    await run([t], now, f.send);
    n = (await notes(t))[0]!;
    expect(n).toMatchObject({ status: "failed", attempts: 5, last_error: "network" });
    expect(f.calls).toHaveLength(5);
    expect(await notes(t)).toHaveLength(1);
    expect((await linkRow((await q("SELECT id FROM beauty_staff_telegram_links WHERE tenant_id = $1", [t.id])).rows[0].id)).status).toBe("active"); // network errors do not unlink
  });

  it("429 pauses for retry_after; 5xx backs off; 4xx other fails at once (real transport mapping)", async () => {
    const t = await tenant(); const l = await link(t); await alert(t);
    let mode: "429" | "502" | "400" = "429";
    const http = (async () => mode === "429" ? new Response('{"parameters":{"retry_after":120}}', { status: 429 }) : new Response("x", { status: mode === "502" ? 502 : 400 })) as unknown as typeof fetch;
    const send: TelegramSend = (tok, chat, text) => sendTelegramText(http, TELEGRAM_API, tok, chat, text, { disablePreview: true });
    await run([t], NOW, send);
    let n = (await notes(t))[0]!;
    expect(n).toMatchObject({ status: "pending", attempts: 1, last_error: "rate_limited" });
    expect(new Date(n.next_attempt_at).getTime() - NOW.getTime()).toBe(120_000);
    mode = "502";
    const t1 = new Date(NOW.getTime() + 121_000);
    await run([t], t1, send);
    n = (await notes(t))[0]!;
    expect(n).toMatchObject({ status: "pending", attempts: 2, last_error: "http_5xx" });
    expect(new Date(n.next_attempt_at).getTime() - t1.getTime()).toBe(5 * MIN);
    mode = "400";
    await run([t], new Date(t1.getTime() + 6 * MIN), send);
    n = (await notes(t))[0]!;
    expect(n).toMatchObject({ status: "failed", last_error: "http_4xx" });
    expect((await linkRow(l)).status).toBe("active");
  });

  it("403 and 'chat not found' unlink automatically: chat data erased, other pending cancelled, event without chat id", async () => {
    for (const [status, body, reason] of [[403, "{}", "bot_blocked"], [400, '{"description":"Bad Request: chat not found"}', "chat_not_found"]] as const) {
      const t = await tenant(); const l = await link(t);
      await alert(t); await alert(t);
      const http = (async () => new Response(body, { status })) as unknown as typeof fetch;
      const send: TelegramSend = (tok, chat, text) => sendTelegramText(http, TELEGRAM_API, tok, chat, text);
      const logs: string[] = [];
      await run([t], NOW, send, {}, undefined, [], logs);
      const row = await linkRow(l);
      expect(row).toMatchObject({ status: "revoked", chat_id_encrypted: null, chat_id_hash: null, token_hash: null, revoke_reason: reason });
      expect(row.revoked_at).not.toBeNull();
      const n = await notes(t);
      expect(n.map((r) => r.status).sort()).toEqual(["cancelled", "failed"]);
      const ev = (await q("SELECT * FROM beauty_staff_telegram_link_events WHERE link_id = $1", [l])).rows;
      expect(ev).toHaveLength(1);
      expect(ev[0]).toMatchObject({ event: "revoked", reason, actor: "system", user_id: t.user });
      expect(JSON.stringify(ev)).not.toContain(CHAT);
      expect(logs.join("\n")).toContain(`inventory_telegram_link_revoked tenant=${t.id} link=${l} reason=${reason}`);
      // nothing more is built for a revoked link
      await alert(t);
      const f = fake(); await run([t], new Date(NOW.getTime() + 5 * MIN), f.send);
      expect(f.calls).toHaveLength(0);
    }
  });

  it("channel problems (inactive / token missing / undecryptable chat) fail or skip without unlinking", async () => {
    const inactive = await tenant({ chActive: false }); const l1 = await link(inactive); await alert(inactive);
    const f = fake();
    await run([inactive], NOW, f.send);
    expect(await notes(inactive)).toHaveLength(0); // skipped: no build for an inactive channel
    expect((await linkRow(l1)).status).toBe("active");

    const nt = await tenant(); const l2 = await link(nt); await alert(nt);
    await q("UPDATE beauty_channels SET credentials_encrypted = $2 WHERE id = $1", [nt.channel, writeCredentials(KEY, {})]);
    await run([nt], NOW, f.send);
    expect((await notes(nt))[0]).toMatchObject({ status: "failed", last_error: "token_missing" });
    expect((await linkRow(l2)).status).toBe("active");

    const bad = await tenant(); const l3 = await link(bad); await alert(bad);
    await q("UPDATE beauty_staff_telegram_links SET chat_id_encrypted = 'AAAA' WHERE id = $1", [l3]);
    await run([bad], NOW, f.send);
    expect((await notes(bad))[0]).toMatchObject({ status: "failed", last_error: "decrypt_failed" });
    expect((await linkRow(l3)).status).toBe("active");
    expect(f.calls).toHaveLength(0);
  });

  it("cancels pending whose alert is no longer open, whose link is gone, or older than 24h", async () => {
    const t = await tenant(); const l = await link(t);
    const a = await alert(t);
    const f = fake((_c, n) => { throw new ChannelSendError("net", false); });
    await run([t], NOW, f.send); // built + 1 failed attempt -> pending with next +1 min
    await q("UPDATE beauty_staff_alerts SET status = 'resolved', resolved_at = now() WHERE id = $1", [a]);
    const g = fake();
    await run([t], new Date(NOW.getTime() + 2 * MIN), g.send);
    expect((await notes(t))[0]!.status).toBe("cancelled");
    expect(g.calls).toHaveLength(0);

    // older than 24h: cleaned up as cancelled even with the flag off
    const t2 = await tenant(); const l2 = await link(t2, { activatedAgo: 40 * HOUR }); await alert(t2, { created: ago(HOUR) });
    await run([t2], NOW, fake(() => { throw new ChannelSendError("net", false); }).send);
    expect((await notes(t2))[0]!.status).toBe("pending");
    await run([t2], new Date(NOW.getTime() + 25 * HOUR), g.send, { inventoryTelegramEnabled: false });
    expect((await notes(t2))[0]!.status).toBe("cancelled");
    void l; void l2;
  });

  it("paces sends to the same chat (>= 1.1 s apart)", async () => {
    const t = await tenant(); await link(t);
    for (let i = 0; i < 3; i++) await alert(t);
    const sleeps: number[] = []; const f = fake();
    await run([t], NOW, f.send, {}, undefined, sleeps);
    expect(f.calls).toHaveLength(3);
    expect(sleeps).toEqual([1_100, 1_100]);
  });

  it("deactivated user / role change / inactive tenant revoke the link (safety net)", async () => {
    const t = await tenant(); const u1 = await user(t, { active: false }); const l1 = await link(t, { userId: u1, chat: "1" });
    const u2 = await user(t, { role: "admin" }); const l2 = await link(t, { userId: u2, chat: "2" });
    await alert(t);
    const f = fake();
    await run([t], NOW, f.send);
    expect(await linkRow(l1)).toMatchObject({ status: "revoked", revoke_reason: "user_deactivated", chat_id_encrypted: null });
    expect(await linkRow(l2)).toMatchObject({ status: "revoked", revoke_reason: "role_changed", chat_id_hash: null });
    expect(f.calls).toHaveLength(0);

    const t2 = await tenant(); const l3 = await link(t2);
    await q("UPDATE tenants SET status = 'suspended' WHERE id = $1", [t2.id]);
    await run([t2], NOW, f.send);
    expect(await linkRow(l3)).toMatchObject({ status: "revoked", revoke_reason: "tenant_inactive" });
  });

  it("cleanup always runs (flag off): expired issued/claimed tokens -> revoked(expired) + event; 30-day retention cascades to alert links", async () => {
    const t = await tenant();
    const u2 = await user(t);
    const issued = randomUUID();
    await q(`INSERT INTO beauty_staff_telegram_links (id, tenant_id, user_id, channel_id, status, token_hash, token_expires_at, consent_version, consent_at)
             VALUES ($1,$2,$3,$4,'issued',$5,$6,'inv-tg-v1',$6)`, [issued, t.id, u2, t.channel, randomBytes(32), ago(MIN)]);
    const fresh = randomUUID(), u3 = await user(t);
    await q(`INSERT INTO beauty_staff_telegram_links (id, tenant_id, user_id, channel_id, status, token_hash, token_expires_at, consent_version, consent_at)
             VALUES ($1,$2,$3,$4,'issued',$5,$6,'inv-tg-v1',$7)`, [fresh, t.id, u3, t.channel, randomBytes(32), new Date(NOW.getTime() + 10 * MIN), NOW]);
    const l = await link(t); const a = await alert(t);
    const nid = randomUUID(), young = randomUUID();
    await q(`INSERT INTO beauty_staff_notifications (id, tenant_id, link_id, kind, body, status, attempts, next_attempt_at, dedupe_key, sent_at, created_at, updated_at)
             VALUES ($1,$2,$3,'alert','x','sent',1,$4,'d1',$4,$4,$4), ($5,$2,$3,'alert','y','sent',1,$6,'d2',$6,$6,$6)`, [nid, t.id, l, ago(31 * 24 * HOUR), young, ago(29 * 24 * HOUR)]);
    await q("INSERT INTO beauty_staff_notification_alerts (tenant_id, notification_id, alert_id, link_id) VALUES ($1,$2,$3,$4)", [t.id, nid, a, l]);
    await run([t], NOW, fake().send, { inventoryTelegramEnabled: false });
    expect(await linkRow(issued)).toMatchObject({ status: "revoked", revoke_reason: "expired", token_hash: null });
    expect((await linkRow(fresh)).status).toBe("issued");
    expect((await q("SELECT event, reason, actor FROM beauty_staff_telegram_link_events WHERE link_id = $1", [issued])).rows).toEqual([{ event: "revoked", reason: "expired", actor: "system" }]);
    expect((await notes(t)).map((r) => r.id)).toEqual([young]);
    expect(Number((await q("SELECT count(*) FROM beauty_staff_notification_alerts WHERE tenant_id = $1", [t.id])).rows[0].count)).toBe(0);
  });

  it("tenant isolation and failure isolation: one tenant failing does not stop another; chats never cross tenants", async () => {
    const a = await tenant(), b = await tenant();
    await link(a, { chat: "AAA" }); await link(b, { chat: "BBB" });
    await alert(a); await alert(b);
    const f = fake((c) => { if (c.token === a.token) throw new ChannelSendError("net", false); });
    const r = await run([a, b], NOW, f.send);
    expect(r.errors).toBe(0);
    expect((await notes(a))[0]).toMatchObject({ status: "pending", attempts: 1 });
    expect((await notes(b))[0]).toMatchObject({ status: "sent" });
    expect(f.calls.map((c) => [c.token, c.chatId]).sort()).toEqual([[a.token, "AAA"], [b.token, "BBB"]].sort());
    // a broken tenant id source: a tenant whose store throws is counted and the next one still runs
    const c = await tenant(); await link(c); await alert(c);
    const broken = { ...ctx([c], NOW, f.send), tenants: new StaticTenantDirectory([randomUUID(), c.id]) };
    await pollInventoryTelegram(broken);
    expect((await notes(c))).toHaveLength(1);
  });

  it("one failing link does not stop the others in the same tenant", async () => {
    const t = await tenant(); await link(t, { chat: "BAD" });
    const u2 = await user(t); await link(t, { userId: u2, chat: "GOOD" });
    await alert(t);
    const f = fake((c) => { if (c.chatId === "BAD") throw new ChannelSendError("net", false); });
    await run([t], NOW, f.send);
    expect((await notes(t)).map((r) => r.status).sort()).toEqual(["pending", "sent"]);
  });

  it("worker role has no extra privileges (least privilege)", async () => {
    const t = await tenant(); const l = await link(t); await alert(t);
    const as = <R>(fn: (c: pg.PoolClient) => Promise<R>) => withTenant(t.id, () => db.tx(fn));
    await expect(as((c) => c.query("SELECT token_hash FROM beauty_staff_telegram_links"))).rejects.toThrow(/permission denied/);
    await expect(as((c) => c.query("SELECT chat_id_hash FROM beauty_staff_telegram_links"))).rejects.toThrow(/permission denied/);
    await expect(as((c) => c.query("SELECT consent_at FROM beauty_staff_telegram_links"))).rejects.toThrow(/permission denied/);
    await expect(as((c) => c.query("DELETE FROM beauty_staff_telegram_links"))).rejects.toThrow(/permission denied/);
    await expect(as((c) => c.query("UPDATE beauty_staff_telegram_links SET consent_version = 'inv-tg-v2' WHERE id = $1", [l]))).rejects.toThrow(/permission denied/);
    await expect(as((c) => c.query(`INSERT INTO beauty_staff_telegram_links (tenant_id, user_id, channel_id, consent_version, consent_at) VALUES ($1,$2,$3,'inv-tg-v1',now())`, [t.id, t.user, t.channel]))).rejects.toThrow(/permission denied/);
    await expect(as((c) => c.query("SELECT * FROM beauty_staff_telegram_link_events"))).rejects.toThrow(/permission denied/);
    await expect(as((c) => c.query("SELECT purchase_price FROM beauty_products"))).rejects.toThrow(/permission denied/);
    await expect(as((c) => c.query("SELECT password_hash FROM users"))).rejects.toThrow(/permission denied/);
    await expect(as((c) => c.query("DELETE FROM beauty_staff_notification_alerts"))).rejects.toThrow(/permission denied/);
    await expect(as((c) => c.query("UPDATE beauty_staff_notification_alerts SET link_id = link_id"))).rejects.toThrow(/permission denied/);
    // allowed: event INSERT (append-only) but never UPDATE/DELETE
    await as((c) => c.query(`INSERT INTO beauty_staff_telegram_link_events (tenant_id, link_id, user_id, event, reason, actor) VALUES ($1,$2,$3,'revoked','expired','system')`, [t.id, l, t.user]));
    await expect(as((c) => c.query("UPDATE beauty_staff_telegram_link_events SET actor = 'user'"))).rejects.toThrow(/permission denied/);
    await expect(as((c) => c.query("DELETE FROM beauty_staff_telegram_link_events"))).rejects.toThrow(/permission denied/);
  });

  it("TASK-897 cross-stack: a link whose chat_id was encrypted by the .NET protector is delivered to that chat; 403 then erases it", async () => {
    const vec = JSON.parse(readFileSync(new URL("../../../.ai/artifacts/contracts/ADR-025-chat-id-secret-vector.json", import.meta.url), "utf8")) as
      { keyBase64: string; cases: { chatId: string; protectedValue: string }[] };
    const vkey = Buffer.from(vec.keyBase64, "base64"), vc = vec.cases[1]!; // negative id
    const t = await tenant();
    await q("UPDATE beauty_channels SET credentials_encrypted = $2 WHERE id = $1", [t.channel, writeCredentials(vkey, { token: t.token })]);
    const l = randomUUID();
    await q(`INSERT INTO beauty_staff_telegram_links (id, tenant_id, user_id, channel_id, status, chat_id_encrypted, chat_id_hash, consent_version, consent_at, claimed_at, bot_confirmed_at, activated_at)
             VALUES ($1,$2,$3,$4,'active',$5,$6,'inv-tg-v1',$7,$7,$7,$7)`, [l, t.id, t.user, t.channel, vc.protectedValue, randomBytes(32), ago(6 * HOUR)]);
    await alert(t);
    const f = fake();
    const mk = (send: TelegramSend, now: Date) => ctx([t], now, send, { inventoryTelegram: new PgInventoryTelegram(db, vkey, send, undefined, { sleep: async () => {}, gapMs: 0 }) });
    await pollInventoryTelegram(mk(f.send, NOW));
    expect(f.calls).toHaveLength(1);
    expect(f.calls[0]!.chatId).toBe(vc.chatId);
    expect(f.calls[0]!.token).toBe(t.token);
    await alert(t, { created: ago(30 * MIN) });
    const blocked = fake(() => { throw new ChannelSendError("blocked", true, "bot_blocked", { httpStatus: 403 }); });
    await pollInventoryTelegram(mk(blocked.send, new Date(NOW.getTime() + 5 * MIN)));
    const row = await linkRow(l);
    expect(row.status).toBe("revoked");
    expect(row.chat_id_encrypted).toBeNull();
    expect(row.chat_id_hash).toBeNull();
    expect(row.token_hash).toBeNull();
    const ev = (await q("SELECT to_jsonb(e)::text AS j FROM beauty_staff_telegram_link_events e WHERE link_id = $1", [l])).rows.map((r) => r.j).join(" ");
    expect(ev).not.toContain(vc.chatId);
    expect(ev).toContain("bot_blocked");
  });
});
