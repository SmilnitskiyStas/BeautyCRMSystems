import type { PoolClient } from "pg";
import { currentTenantId, type TenantDb } from "../db/tenant-db";
import type { Logger } from "../adapters/pg-support";
import { ChannelSendError } from "../adapters/channel-sender";
import { readCredentials, unprotect } from "../adapters/channel-crypto";
import { alertText, alertsUrl, digestText, type DigestRow } from "../jobs/inventory-telegram-texts";
import { localParts } from "./alerts";

/** §32.24.5 worker side of the owner's Telegram inventory alerts. Logs carry ids and codes only: never chat_id, tokens, bodies, product names. */
export interface InventoryTelegramConfig {
  /** Local hours 0..23; quiet = [quietStart, quietEnd) wrapping midnight (default 21..08). */
  quietStart: number;
  quietEnd: number;
  /** https base of the admin UI (no trailing slash); absent => no link in texts. */
  adminBaseUrl?: string;
}

export const DEFAULT_TELEGRAM_CONFIG: InventoryTelegramConfig = { quietStart: 21, quietEnd: 8 };

const MIN = 60_000;
const HOUR = 3_600_000;
export const MAX_PER_HOUR = 6;
export const DIGEST_THRESHOLD = 3;
export const MAX_AGE_MS = 24 * HOUR;
export const RETENTION_MS = 30 * 24 * HOUR;
export const SENDING_TIMEOUT_MS = 10 * MIN;
export const MAX_ATTEMPTS = 5;
export const MAX_SENDS_PER_RUN = 100;
const BACKOFF_MS = [1 * MIN, 5 * MIN, 30 * MIN, 2 * HOUR];

/**
 * WORKER_INVENTORY_TELEGRAM_QUIET_START / _QUIET_END (ints 0..23, START != END) and WORKER_ADMIN_BASE_URL (https only; http allowed
 * only for localhost). Invalid => throws (worker does not start).
 */
export function loadInventoryTelegramConfig(env: NodeJS.ProcessEnv): InventoryTelegramConfig {
  const cfg: InventoryTelegramConfig = { ...DEFAULT_TELEGRAM_CONFIG };
  const hour = (name: string, v: string | undefined, d: number): number => {
    const s = v?.trim();
    if (!s) return d;
    const n = /^\d+$/.test(s) ? Number(s) : NaN;
    if (!Number.isInteger(n) || n < 0 || n > 23) throw new Error(`Invalid ${name}: expected integer 0..23`);
    return n;
  };
  cfg.quietStart = hour("WORKER_INVENTORY_TELEGRAM_QUIET_START", env.WORKER_INVENTORY_TELEGRAM_QUIET_START, cfg.quietStart);
  cfg.quietEnd = hour("WORKER_INVENTORY_TELEGRAM_QUIET_END", env.WORKER_INVENTORY_TELEGRAM_QUIET_END, cfg.quietEnd);
  if (cfg.quietStart === cfg.quietEnd) throw new Error("Invalid WORKER_INVENTORY_TELEGRAM_QUIET_START/END: must differ");
  const base = env.WORKER_ADMIN_BASE_URL?.trim();
  if (base) {
    if (!/^https:\/\/[^\s/]+/i.test(base) && !/^http:\/\/(localhost|127\.0\.0\.1)(:\d+)?(\/|$)/i.test(base))
      throw new Error("Invalid WORKER_ADMIN_BASE_URL: expected https:// URL");
    cfg.adminBaseUrl = base.replace(/\/+$/, "");
  }
  return cfg;
}

/** True when `hour` (local) is inside the quiet window [start, end) (wraps midnight when start > end). */
export function isQuietHour(hour: number, start: number, end: number): boolean {
  return start < end ? hour >= start && hour < end : hour >= start || hour < end;
}

/** Transport injected into the store (sendTelegramText in production, a fake in tests). chat_id exists in memory only for the call. */
export type TelegramSend = (token: string, chatId: string, text: string) => Promise<{ externalMessageId: string | null }>;

export interface InventoryTelegramRunResult { built: number; sent: number; failed: number; revoked: number; capped: boolean; errors: number }
export interface InventoryTelegramCleanupResult { expired: number; revoked: number; cancelled: number; deleted: number }

export interface InventoryTelegramStore {
  /** Always runs (even with the flag off or the module off): expired tokens, tenant_inactive, stale pending, 30-day retention. */
  cleanup(now: Date): Promise<InventoryTelegramCleanupResult>;
  /** Build (alerts -> queue) and send for the current tenant (withTenant scope). Never throws for a single bad link/notification. */
  runForTenant(now: Date, cfg: InventoryTelegramConfig): Promise<InventoryTelegramRunResult>;
}

export interface TelegramStoreOptions {
  sleep?: (ms: number) => Promise<void>;
  /** Pause between two sends to the same chat (<= 1 msg/s per chat). */
  gapMs?: number;
}

type RevokeReason = "expired" | "bot_blocked" | "chat_not_found" | "user_deactivated" | "role_changed" | "tenant_inactive";

function errCode(err: unknown): string {
  const e = err as { code?: unknown; name?: unknown } | null;
  return typeof e?.code === "string" ? e.code : typeof e?.name === "string" ? e.name : "error";
}

interface AlertRow {
  id: string; kind: "low_stock" | "expiring" | "shortage"; location_id: string; quantity: string; threshold: string | null;
  expires_on: string | null; days_bucket: number | null; loc_name: string; timezone: string; product: string; unit: string; lot: string | null;
}

export class PgInventoryTelegram implements InventoryTelegramStore {
  private readonly sleep: (ms: number) => Promise<void>;
  private readonly gapMs: number;

  constructor(
    private readonly db: TenantDb,
    private readonly key: Buffer,
    private readonly send: TelegramSend,
    private readonly logger?: Logger,
    opts: TelegramStoreOptions = {},
  ) {
    this.sleep = opts.sleep ?? ((ms) => new Promise((r) => setTimeout(r, ms)));
    this.gapMs = opts.gapMs ?? 1_100;
  }

  // ---------- revoke (shared) ----------

  /** Closes a link: chat data erased in the same UPDATE, pending notifications cancelled, append-only event (no chat_id). */
  private async revokeLink(c: PoolClient, linkId: string, userId: string, reason: RevokeReason, now: Date): Promise<boolean> {
    const r = await c.query(
      `UPDATE beauty_staff_telegram_links SET status = 'revoked', token_hash = NULL, chat_id_encrypted = NULL, chat_id_hash = NULL,
              revoked_at = $2, revoke_reason = $3, updated_at = $2
       WHERE id = $1 AND status IN ('issued', 'claimed', 'active')`, [linkId, now, reason]);
    if (!r.rowCount) return false;
    await c.query(
      `UPDATE beauty_staff_notifications SET status = 'cancelled', updated_at = $2 WHERE link_id = $1 AND status IN ('pending', 'sending')`, [linkId, now]);
    await c.query(
      `INSERT INTO beauty_staff_telegram_link_events (tenant_id, link_id, user_id, event, reason, actor, created_at)
       VALUES (NULLIF(current_setting('app.tenant_id', true), '')::uuid, $1, $2, 'revoked', $3, 'system', $4)`, [linkId, userId, reason, now]);
    this.logger?.info(`inventory_telegram_link_revoked tenant=${currentTenantId()} link=${linkId} reason=${reason}`);
    return true;
  }

  // ---------- cleanup ----------

  async cleanup(now: Date): Promise<InventoryTelegramCleanupResult> {
    return this.db.tx(async (c) => {
      const res: InventoryTelegramCleanupResult = { expired: 0, revoked: 0, cancelled: 0, deleted: 0 };
      const inactive = (await c.query<{ ok: boolean }>(
        `SELECT NOT EXISTS (SELECT 1 FROM tenants t WHERE t.id = NULLIF(current_setting('app.tenant_id', true), '')::uuid AND t.status = 'active') AS ok`)).rows[0]?.ok === true;
      if (inactive) {
        const open = await c.query<{ id: string; user_id: string }>(`SELECT id, user_id FROM beauty_staff_telegram_links WHERE status IN ('issued', 'claimed', 'active')`);
        for (const l of open.rows) if (await this.revokeLink(c, l.id, l.user_id, "tenant_inactive", now)) res.revoked++;
      }
      const stale = await c.query<{ id: string; user_id: string }>(
        `SELECT id, user_id FROM beauty_staff_telegram_links WHERE status IN ('issued', 'claimed') AND token_expires_at < $1`, [now]);
      for (const l of stale.rows) if (await this.revokeLink(c, l.id, l.user_id, "expired", now)) res.expired++;
      res.cancelled = (await c.query(
        `UPDATE beauty_staff_notifications SET status = 'cancelled', updated_at = $1 WHERE status IN ('pending', 'sending') AND created_at < $2`,
        [now, new Date(now.getTime() - MAX_AGE_MS)])).rowCount ?? 0;
      res.deleted = (await c.query(
        `DELETE FROM beauty_staff_notifications WHERE status IN ('sent', 'failed', 'cancelled') AND updated_at < $1`,
        [new Date(now.getTime() - RETENTION_MS)])).rowCount ?? 0;
      return res;
    });
  }

  // ---------- build + send ----------

  async runForTenant(now: Date, cfg: InventoryTelegramConfig): Promise<InventoryTelegramRunResult> {
    const res: InventoryTelegramRunResult = { built: 0, sent: 0, failed: 0, revoked: 0, capped: false, errors: 0 };
    const tenant = currentTenantId();

    const links = await this.db.tx(async (c) => (await c.query<{
      id: string; user_id: string; activated_at: Date; role: string; is_active: boolean; state: string; ch_active: boolean; ch_type: string;
    }>(
      `SELECT l.id, l.user_id, l.activated_at, u.role, u.is_active, u.state, ch.is_active AS ch_active, ch.type AS ch_type
       FROM beauty_staff_telegram_links l
       JOIN users u ON u.tenant_id = l.tenant_id AND u.id = l.user_id
       JOIN beauty_channels ch ON ch.tenant_id = l.tenant_id AND ch.id = l.channel_id
       WHERE l.status = 'active' ORDER BY l.id`)).rows);

    for (const l of links) {
      try {
        const reason: RevokeReason | null = !l.is_active || l.state !== "active" ? "user_deactivated" : l.role.toLowerCase() !== "owner" ? "role_changed" : null;
        if (reason) {
          if (await this.db.tx((c) => this.revokeLink(c, l.id, l.user_id, reason, now))) res.revoked++;
          continue;
        }
        if (!l.ch_active || l.ch_type.toLowerCase() !== "telegram") continue; // channel problem, not the user's: skip without unlinking (2b-Q9)
        const b = await this.db.tx((c) => this.buildForLink(c, l, now, cfg));
        res.built += b.built;
        if (b.capped) res.capped = true;
      } catch (err) {
        if ((err as { code?: unknown } | null)?.code === "23505") { // concurrent build: whole link batch rolled back, rebuilt next run
          this.logger?.info(`inventory_telegram_build_conflict tenant=${tenant} link=${l.id}`);
          continue;
        }
        res.errors++;
        this.logger?.error(`inventory_telegram_link_failed tenant=${tenant} link=${l.id} errorCode=${errCode(err)}`);
      }
    }

    // reset stuck 'sending'
    await this.db.tx((c) => c.query(
      `UPDATE beauty_staff_notifications SET status = 'pending', updated_at = $1 WHERE status = 'sending' AND updated_at < $2`,
      [now, new Date(now.getTime() - SENDING_TIMEOUT_MS)]));

    const due = await this.db.tx(async (c) => (await c.query<{ id: string; link_id: string }>(
      `SELECT id, link_id FROM beauty_staff_notifications WHERE status = 'pending' AND next_attempt_at <= $1
       ORDER BY next_attempt_at, id LIMIT ${MAX_SENDS_PER_RUN}`, [now])).rows);
    let prevLink: string | null = null;
    for (const n of due) {
      try {
        if (prevLink === n.link_id) await this.sleep(this.gapMs);
        const r = await this.sendOne(n.id, now);
        if (r.attempted) prevLink = n.link_id;
        if (r.outcome === "sent") res.sent++;
        else if (r.outcome === "failed") res.failed++;
        else if (r.outcome === "revoked") { res.failed++; res.revoked++; }
      } catch (err) {
        res.errors++;
        this.logger?.error(`inventory_telegram_send_failed tenant=${tenant} notification=${n.id} errorCode=${errCode(err)}`);
      }
    }
    return res;
  }

  private async buildForLink(c: PoolClient, l: { id: string; activated_at: Date }, now: Date, cfg: InventoryTelegramConfig): Promise<{ built: number; capped: boolean }> {
    const used = Number((await c.query<{ n: string }>(
      `SELECT count(*) AS n FROM beauty_staff_notifications WHERE link_id = $1 AND status <> 'cancelled' AND created_at > $2`,
      [l.id, new Date(now.getTime() - HOUR)])).rows[0]!.n);
    let remaining = MAX_PER_HOUR - used;
    // scope: the recipient is an owner => all venues (inventory.view scope of non-owner roles is a schema-neutral extension, §32.24.1)
    const rows = (await c.query<AlertRow>(
      `SELECT a.id, a.kind, a.location_id, a.quantity, a.threshold, to_char(a.expires_on, 'YYYY-MM-DD') AS expires_on, a.days_bucket,
              loc.name AS loc_name, loc.timezone, p.name AS product, p.unit, b.lot_number AS lot
       FROM beauty_staff_alerts a
       JOIN beauty_locations loc ON loc.tenant_id = a.tenant_id AND loc.id = a.location_id
       JOIN beauty_products p ON p.tenant_id = a.tenant_id AND p.id = a.product_id
       LEFT JOIN beauty_stock_batches b ON b.tenant_id = a.tenant_id AND b.id = a.batch_id
       WHERE a.status = 'open' AND a.created_at >= $2 AND a.created_at >= $3
         AND NOT EXISTS (SELECT 1 FROM beauty_staff_notification_alerts na WHERE na.tenant_id = a.tenant_id AND na.link_id = $1 AND na.alert_id = a.id)
       ORDER BY a.created_at, a.id LIMIT 500`,
      [l.id, l.activated_at, new Date(now.getTime() - MAX_AGE_MS)])).rows;

    const eligible = rows.filter((a) => {
      try { return !isQuietHour(localParts(now, a.timezone).hour, cfg.quietStart, cfg.quietEnd); }
      catch { return false; } // invalid timezone of one venue: its alerts wait
    });
    if (eligible.length === 0) return { built: 0, capped: false };
    if (remaining <= 0) return { built: 0, capped: true };

    const insert = async (kind: "alert" | "digest", body: string, key: string, alertIds: string[]) => {
      const id = (await c.query<{ id: string }>(
        `INSERT INTO beauty_staff_notifications (tenant_id, link_id, kind, body, status, attempts, next_attempt_at, dedupe_key, created_at, updated_at)
         VALUES (NULLIF(current_setting('app.tenant_id', true), '')::uuid, $1, $2, $3, 'pending', 0, $4, $5, $4, $4) RETURNING id`,
        [l.id, kind, body, now, key])).rows[0]!.id;
      for (const alertId of alertIds) {
        await c.query(
          `INSERT INTO beauty_staff_notification_alerts (tenant_id, notification_id, alert_id, link_id)
           VALUES (NULLIF(current_setting('app.tenant_id', true), '')::uuid, $1, $2, $3)`, [id, alertId, l.id]);
      }
    };

    if (eligible.length <= DIGEST_THRESHOLD) {
      const take = eligible.slice(0, remaining);
      for (const a of take) {
        const body = alertText({ kind: a.kind, location: a.loc_name, product: a.product, unit: a.unit, quantity: a.quantity, threshold: a.threshold, lot: a.lot, expiresOn: a.expires_on, daysBucket: a.days_bucket },
          alertsUrl(cfg.adminBaseUrl, a.location_id));
        await insert("alert", body, `alert:${l.id}:${a.id}`, [a.id]);
      }
      return { built: take.length, capped: take.length < eligible.length };
    }
    const byLoc = new Map<string, DigestRow>();
    for (const a of eligible) {
      const r = byLoc.get(a.location_id) ?? { location: a.loc_name, low: 0, expiring: 0, shortage: 0 };
      if (a.kind === "low_stock") r.low++; else if (a.kind === "expiring") r.expiring++; else r.shortage++;
      byLoc.set(a.location_id, r);
    }
    const slot = new Date(Math.floor(now.getTime() / (5 * MIN)) * 5 * MIN).toISOString().replace(/[-:T]/g, "").slice(0, 12);
    await insert("digest", digestText(eligible.length, [...byLoc.values()], alertsUrl(cfg.adminBaseUrl)), `digest:${l.id}:${slot}`, eligible.map((a) => a.id));
    return { built: 1, capped: false };
  }

  private async finish(notificationId: string, status: "sent" | "failed" | "cancelled" | "pending", now: Date,
    f: { attempts?: number; error?: string | null; externalId?: string | null; nextAt?: Date; linkId?: string } = {}): Promise<void> {
    await this.db.tx(async (c) => {
      await c.query(
        `UPDATE beauty_staff_notifications SET status = $2::varchar, attempts = coalesce($3::smallint, attempts), last_error = $4, external_message_id = coalesce($5, external_message_id),
                next_attempt_at = coalesce($6::timestamptz, next_attempt_at), sent_at = CASE WHEN $2::varchar = 'sent' THEN $7::timestamptz ELSE sent_at END, updated_at = $7::timestamptz
         WHERE id = $1`, [notificationId, status, f.attempts ?? null, f.error ?? null, f.externalId ?? null, f.nextAt ?? null, now]);
      if (status === "sent" && f.linkId)
        await c.query(`UPDATE beauty_staff_telegram_links SET last_delivery_at = $2, updated_at = $2 WHERE id = $1`, [f.linkId, now]);
    });
  }

  private async sendOne(id: string, now: Date): Promise<{ outcome: "sent" | "failed" | "revoked" | "cancelled" | "retry" | "skipped"; attempted: boolean }> {
    const claimed = await this.db.tx(async (c) => (await c.query<{ id: string; link_id: string; kind: string; body: string; attempts: number; created_at: Date }>(
      `UPDATE beauty_staff_notifications SET status = 'sending', updated_at = $2
       WHERE id = $1 AND status = 'pending' AND next_attempt_at <= $2 RETURNING id, link_id, kind, body, attempts, created_at`, [id, now])).rows[0]);
    if (!claimed) return { outcome: "skipped", attempted: false };

    const ctx = await this.db.tx(async (c) => (await c.query<{
      status: string; user_id: string; chat: string | null; type: string; is_active: boolean; cred: string | null; all_open: boolean | null;
    }>(
      `SELECT l.status, l.user_id, l.chat_id_encrypted AS chat, ch.type, ch.is_active, ch.credentials_encrypted AS cred,
              (SELECT bool_and(a.status = 'open') FROM beauty_staff_notification_alerts na
                 JOIN beauty_staff_alerts a ON a.tenant_id = na.tenant_id AND a.id = na.alert_id WHERE na.notification_id = $1) AS all_open
       FROM beauty_staff_telegram_links l JOIN beauty_channels ch ON ch.tenant_id = l.tenant_id AND ch.id = l.channel_id
       WHERE l.id = $2`, [id, claimed.link_id])).rows[0]);

    if (!ctx || ctx.status !== "active" || !ctx.chat) { await this.finish(id, "cancelled", now); return { outcome: "cancelled", attempted: false }; }
    if (now.getTime() - new Date(claimed.created_at).getTime() > MAX_AGE_MS || (claimed.kind === "alert" && ctx.all_open === false)) {
      await this.finish(id, "cancelled", now);
      return { outcome: "cancelled", attempted: false };
    }
    if (!ctx.is_active || ctx.type.toLowerCase() !== "telegram") { await this.finish(id, "failed", now, { error: "channel_inactive" }); return { outcome: "failed", attempted: false }; }
    let token: string | undefined, chatId: string;
    try { token = readCredentials(this.key, ctx.cred).token; chatId = unprotect(this.key, ctx.chat); }
    catch { await this.finish(id, "failed", now, { error: "decrypt_failed" }); return { outcome: "failed", attempted: false }; }
    if (!token) { await this.finish(id, "failed", now, { error: "token_missing" }); return { outcome: "failed", attempted: false }; }

    const attempts = claimed.attempts + 1;
    try {
      const r = await this.send(token, chatId, claimed.body);
      await this.finish(id, "sent", now, { attempts, error: null, externalId: r.externalMessageId?.slice(0, 32) ?? null, linkId: claimed.link_id });
      return { outcome: "sent", attempted: true };
    } catch (err) {
      const e = err instanceof ChannelSendError ? err : null;
      const tenant = currentTenantId();
      const fail = (code: string) => this.logger?.error(`inventory_telegram_send_failed tenant=${tenant} notification=${id} errorCode=${code}`);
      if (e?.code === "bot_blocked" || e?.code === "chat_not_found") {
        fail(e.code);
        await this.db.tx(async (c) => {
          await c.query(`UPDATE beauty_staff_notifications SET status = 'failed', attempts = $2, last_error = $3, updated_at = $4 WHERE id = $1`, [id, attempts, e.code, now]);
          await this.revokeLink(c, claimed.link_id, ctx.user_id, e.code as RevokeReason, now);
        });
        return { outcome: "revoked", attempted: true };
      }
      const status = e?.extra.httpStatus;
      const code = !e ? "send_error" : e.extra.retryAfterSec !== undefined || status === 429 ? "rate_limited" : status && status >= 500 ? "http_5xx" : e.permanent ? "http_4xx" : "network";
      fail(code);
      if ((e && e.permanent) || attempts >= MAX_ATTEMPTS) {
        await this.finish(id, "failed", now, { attempts, error: code });
        return { outcome: "failed", attempted: true };
      }
      const delay = code === "rate_limited" ? Math.min(Math.max(e?.extra.retryAfterSec ?? 60, 1), 3_600) * 1_000 : BACKOFF_MS[Math.min(attempts - 1, BACKOFF_MS.length - 1)]!;
      await this.finish(id, "pending", now, { attempts, error: code, nextAt: new Date(now.getTime() + delay) });
      return { outcome: "retry", attempted: true };
    }
  }
}
