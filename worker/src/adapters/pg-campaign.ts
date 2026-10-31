import { randomBytes } from "node:crypto";
import type { PoolClient } from "pg";
import type { CampaignEnqueueOptions, CampaignEnqueueResult, CampaignInfo, CampaignRecipientRef, CampaignStatus } from "../ports";
import { currentTenantId, type TenantDb } from "../db/tenant-db";
import {
  COUPON_FALLBACK_DAYS, DEFAULT_FAVORITE_SERVICES_DAYS, buildCampaignKeyboard, inCampaignWindow, levelAllows, renderCampaignText,
} from "../jobs/beauty-campaign";
import { codeHint, generatePersonalCode, hashCode } from "../jobs/coupon-code";
import { channelButtonsOn } from "./pg-poll-store";

/** Idempotency key of a campaign message; the recipient is found by it on delivery. */
export const CAMPAIGN_KEY = /^campaign:([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}):([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$/i;

const DAY = 86_400_000;
const FINAL_CODES = new Set(["bot_blocked", "chat_not_found", "channel_inactive", "token_missing"]);

/** True when the client may be addressed by a campaign of this type; the single place where consent is read (table, not clients flag). */
async function consentVerdict(c: PoolClient, clientId: string, type: "promo" | "news", promotionId: string | null, now: Date, favoriteDays: number)
  : Promise<"ok" | "no_consent" | "unsubscribed" | "level_mismatch"> {
  const { rows } = await c.query<{ level: string | null; unsub: boolean }>(
    `SELECT mc.level, (cl.unsubscribed OR coalesce(pref.method = 'none', false)) AS unsub
     FROM beauty_clients cl
     LEFT JOIN beauty_marketing_consents mc ON mc.tenant_id = cl.tenant_id AND mc.client_id = cl.id AND mc.channel = 'telegram'
     LEFT JOIN beauty_client_contact_preferences pref ON pref.tenant_id = cl.tenant_id AND pref.client_id = cl.id
     WHERE cl.id = $1`, [clientId]);
  const r = rows[0];
  if (!r || r.level === null) return "no_consent";
  if (r.level === "none" || r.unsub) return "unsubscribed";
  let overlap = true;
  if (r.level === "selective" && type === "promo" && promotionId) {
    overlap = (await c.query<{ ok: boolean }>(
      `SELECT (NOT EXISTS (SELECT 1 FROM beauty_promotion_services ps WHERE ps.promotion_id = $2))
           OR EXISTS (SELECT 1 FROM beauty_appointments a JOIN beauty_promotion_services ps ON ps.service_id = a.service_id AND ps.promotion_id = $2
                      WHERE a.client_id = $1 AND a.status = 'completed' AND a.starts_at > $3::timestamptz - make_interval(days => $4::int)) AS ok`,
      [clientId, promotionId, now, favoriteDays])).rows[0]!.ok;
  }
  return levelAllows(r.level, type, overlap) ? "ok" : "level_mismatch";
}

/** Campaign part of the BeautyDataPort over PostgreSQL (RLS through TenantDb). */
export class PgCampaigns {
  constructor(private readonly db: TenantDb) {}

  async getCampaign(id: string): Promise<CampaignInfo | null> {
    const { r, names } = await this.db.tx(async (c) => {
      const row = (await c.query(
        `SELECT c.id, c.tenant_id, c.status, c.type, c.promotion_id, c.coupon_mode, c.coupon_valid_from, c.coupon_valid_to, c.template, c.location_id, c.scheduled_at,
                p.discount_type, p.discount_value, p.starts_at AS p_starts, p.ends_at AS p_ends, p.is_active AS p_active, sc.code AS shared_code
         FROM beauty_campaigns c
         LEFT JOIN beauty_promotions p ON p.tenant_id = c.tenant_id AND p.id = c.promotion_id
         LEFT JOIN beauty_coupons sc ON sc.tenant_id = c.tenant_id AND sc.id = c.shared_coupon_id
         WHERE c.id = $1`, [id])).rows[0];
      const list = row?.promotion_id ? (await c.query<{ name: string }>(
        `SELECT s.name FROM beauty_promotion_services ps JOIN beauty_services s ON s.tenant_id = ps.tenant_id AND s.id = ps.service_id
         WHERE ps.promotion_id = $1 ORDER BY s.name`, [row.promotion_id])).rows.map((x) => x.name) : [];
      return { r: row, names: list };
    });
    if (!r) return null;
    return {
      id: r.id, tenantId: r.tenant_id, status: r.status as CampaignStatus, type: r.type, couponMode: r.coupon_mode,
      ...(r.discount_type ? { promotion: {
        discountType: r.discount_type, discountValue: Number(r.discount_value), isActive: r.p_active,
        ...(r.p_starts ? { startsAt: r.p_starts } : {}), ...(r.p_ends ? { endsAt: r.p_ends } : {}), serviceNames: names,
      } } : {}),
      ...(r.shared_code ? { sharedCode: r.shared_code } : {}),
      ...(r.coupon_valid_from ? { couponValidFrom: r.coupon_valid_from } : {}), ...(r.coupon_valid_to ? { couponValidTo: r.coupon_valid_to } : {}),
      template: r.template, ...(r.location_id ? { locationId: r.location_id } : {}), scheduledAt: r.scheduled_at ?? null,
    };
  }

  async listCampaignAudience(campaignId: string, limit: number, afterId?: string): Promise<CampaignRecipientRef[]> {
    const { rows } = await this.db.tx((c) => c.query<{ id: string; client_id: string }>(
      `SELECT id, client_id FROM beauty_campaign_recipients
       WHERE campaign_id = $1 AND status = 'pending' AND ($3::uuid IS NULL OR id > $3::uuid) ORDER BY id LIMIT $2`, [campaignId, limit, afterId ?? null]));
    return rows.map((r) => ({ recipientId: r.id, clientId: r.client_id }));
  }

  async listDueCampaigns(now: Date): Promise<string[]> {
    const { rows } = await this.db.tx((c) => c.query<{ id: string }>(
      `SELECT id FROM beauty_campaigns WHERE (status = 'scheduled' AND scheduled_at <= $1::timestamptz) OR status = 'sending'
       ORDER BY scheduled_at, id`, [now]));
    return rows.map((r) => r.id);
  }

  async startCampaign(id: string, now: Date): Promise<boolean> {
    const { rowCount } = await this.db.tx((c) => c.query(
      `UPDATE beauty_campaigns SET status = 'sending', started_at = $2, updated_at = $2
       WHERE id = $1 AND status = 'scheduled' AND scheduled_at <= $2::timestamptz`, [id, now]));
    return rowCount === 1;
  }

  async finishCampaignIfDrained(id: string, now: Date): Promise<boolean> {
    const { rowCount } = await this.db.tx((c) => c.query(
      `UPDATE beauty_campaigns SET status = 'done', finished_at = $2, updated_at = $2
       WHERE id = $1 AND status = 'sending'
         AND NOT EXISTS (SELECT 1 FROM beauty_campaign_recipients r WHERE r.campaign_id = $1 AND r.status IN ('pending', 'queued'))`, [id, now]));
    return rowCount === 1;
  }

  /** Why a queued campaign message must not go out now (§26.8 outbox), else null. */
  async campaignSendGuard(messageId: string): Promise<"campaign_cancelled" | "consent_withdrawn" | null> {
    return this.db.tx(async (c) => {
      const { rows } = await c.query<{ client_id: string; status: string; type: "promo" | "news"; promotion_id: string | null }>(
        `SELECT r.client_id, k.status, k.type, k.promotion_id FROM beauty_campaign_recipients r
         JOIN beauty_campaigns k ON k.tenant_id = r.tenant_id AND k.id = r.campaign_id WHERE r.message_id = $1`, [messageId]);
      const r = rows[0];
      if (!r) return null;
      if (r.status === "cancelled") return "campaign_cancelled";
      return (await consentVerdict(c, r.client_id, r.type, r.promotion_id, new Date(), DEFAULT_FAVORITE_SERVICES_DAYS)) === "ok" ? null : "consent_withdrawn";
    });
  }

  /** Delivery outcome -> recipient (and revoke the undelivered personal coupon). Called from the message status writers. */
  static async applyDelivery(c: PoolClient, idempotencyKey: string | null, outcome: { sent: true } | { code: string }, now: Date): Promise<void> {
    const m = idempotencyKey ? CAMPAIGN_KEY.exec(idempotencyKey) : null;
    if (!m) return;
    if ("sent" in outcome) {
      await c.query(
        `UPDATE beauty_campaign_recipients SET status = 'sent', sent_at = $3, updated_at = $3
         WHERE campaign_id = $1 AND client_id = $2 AND status = 'queued'`, [m[1], m[2], now]);
      return;
    }
    const withdrawn = outcome.code === "campaign_cancelled" || outcome.code === "consent_withdrawn";
    const code = withdrawn || FINAL_CODES.has(outcome.code) ? outcome.code : "send_failed";
    const { rows } = await c.query<{ coupon_id: string | null }>(
      `UPDATE beauty_campaign_recipients SET status = $3, skip_reason = $4, updated_at = $5
       WHERE campaign_id = $1 AND client_id = $2 AND status = 'queued' RETURNING coupon_id`,
      [m[1], m[2], withdrawn ? "skipped" : "failed", code, now]);
    if (rows[0]?.coupon_id) {
      await c.query(`UPDATE beauty_coupons SET status = 'revoked', updated_at = $2 WHERE id = $1 AND kind = 'personal' AND status = 'active'`, [rows[0].coupon_id, now]);
    }
  }

  /** §26.8 `enqueueCampaignMessage`: one transaction, checks in contract order, first failure = `skipped` + reason (no personal data). */
  async enqueueCampaignMessage(recipientId: string, now: Date, opts: CampaignEnqueueOptions): Promise<CampaignEnqueueResult> {
    const tenantId = currentTenantId();
    return this.db.tx(async (c): Promise<CampaignEnqueueResult> => {
      const rec = (await c.query<{ campaign_id: string; client_id: string }>(
        `SELECT campaign_id, client_id FROM beauty_campaign_recipients WHERE id = $1 AND status = 'pending' FOR UPDATE SKIP LOCKED`, [recipientId])).rows[0];
      if (!rec) return "not_pending";
      const skip = async (code: string): Promise<CampaignEnqueueResult> => {
        await c.query(`UPDATE beauty_campaign_recipients SET status = 'skipped', skip_reason = $2, updated_at = $3 WHERE id = $1`, [recipientId, code, now]);
        return `skipped:${code}`;
      };
      const camp = (await c.query(
        `SELECT k.status, k.type, k.promotion_id, k.coupon_mode, k.shared_coupon_id, k.coupon_valid_from, k.coupon_valid_to, k.template, k.location_id,
                p.discount_type, p.discount_value, p.starts_at AS p_starts, p.ends_at AS p_ends, p.is_active AS p_active
         FROM beauty_campaigns k LEFT JOIN beauty_promotions p ON p.tenant_id = k.tenant_id AND p.id = k.promotion_id WHERE k.id = $1`, [rec.campaign_id])).rows[0];
      // 1. campaign
      if (!camp || camp.status !== "sending") return skip("campaign_cancelled");
      // 2. client
      const cl = (await c.query<{ full_name: string | null; deleted_at: Date | null; pref_conv: string | null }>(
        `SELECT cl.full_name, cl.deleted_at, p.telegram_conversation_id AS pref_conv FROM beauty_clients cl
         LEFT JOIN beauty_client_contact_preferences p ON p.tenant_id = cl.tenant_id AND p.client_id = cl.id WHERE cl.id = $1`, [rec.client_id])).rows[0];
      if (!cl || cl.deleted_at) return skip("client_deleted");
      // 3. telegram conversation
      const convSql = `SELECT c.id, ch.settings FROM beauty_conversations c JOIN beauty_channels ch ON ch.tenant_id = c.tenant_id AND ch.id = c.channel_id
                       WHERE c.client_id = $1 AND ch.is_active AND lower(ch.type) = 'telegram'`;
      let conv = cl.pref_conv ? (await c.query<{ id: string; settings: unknown }>(`${convSql} AND c.id = $2 LIMIT 1`, [rec.client_id, cl.pref_conv])).rows[0] : undefined;
      conv ??= (await c.query<{ id: string; settings: unknown }>(`${convSql} ORDER BY c.last_message_at DESC NULLS LAST, c.created_at DESC LIMIT 1`, [rec.client_id])).rows[0];
      if (!conv) return skip("no_channel");
      // 4. consent (table beauty_marketing_consents)
      const verdict = await consentVerdict(c, rec.client_id, camp.type, camp.promotion_id, now, opts.favoriteServicesDays);
      if (verdict !== "ok") return skip(verdict);
      // 5. offer still valid
      const coupon = camp.coupon_mode === "shared" ? (await c.query<{ code: string; valid_from: Date; valid_to: Date; status: string; max_uses: number | null; uses_count: number }>(
        "SELECT code, valid_from, valid_to, status, max_uses, uses_count FROM beauty_coupons WHERE id = $1", [camp.shared_coupon_id])).rows[0] : undefined;
      if (camp.type === "promo" && (!camp.p_active || (camp.p_ends && camp.p_ends <= now) || (camp.coupon_valid_to && camp.coupon_valid_to <= now))) return skip("offer_expired");
      if (camp.coupon_mode === "shared" && (!coupon || coupon.status !== "active" || coupon.valid_to <= now || (coupon.max_uses !== null && coupon.uses_count >= coupon.max_uses))) return skip("offer_expired");
      // 6. frequency: one marketing message per client per N days (campaigns and winback)
      const freq = await c.query(
        `SELECT 1 WHERE EXISTS (SELECT 1 FROM beauty_campaign_recipients x WHERE x.client_id = $1 AND x.id <> $2 AND x.status IN ('queued', 'sent')
                                AND x.queued_at > $3::timestamptz - make_interval(days => $4::int))
                     OR EXISTS (SELECT 1 FROM beauty_messages m WHERE m.idempotency_key LIKE 'winback:' || $5::text || ':%'
                                AND m.created_at > $3::timestamptz - make_interval(days => $4::int))`, [rec.client_id, recipientId, now, opts.clientIntervalDays, rec.client_id]);
      if (freq.rowCount) return skip("frequency");
      // 7. window in the home venue's zone (last appointment, else campaign venue, else first active venue, else default)
      const tz = (await c.query<{ timezone: string }>(
        `SELECT timezone FROM (
           SELECT 1 AS pr, l.timezone, a.starts_at AS ord, l.name FROM beauty_appointments a JOIN beauty_locations l ON l.tenant_id = a.tenant_id AND l.id = a.location_id WHERE a.client_id = $1
           UNION ALL SELECT 2, l.timezone, NULL, l.name FROM beauty_locations l WHERE l.id = $2
           UNION ALL SELECT 3, l.timezone, NULL, l.name FROM beauty_locations l WHERE l.is_active) t
         ORDER BY pr, ord DESC NULLS LAST, name LIMIT 1`, [rec.client_id, camp.location_id])).rows[0]?.timezone ?? opts.defaultTimezone;
      if (!inCampaignWindow(now, tz)) return "deferred_window";
      // 8. daily limit per tenant (rolling 24 h of created marketing messages)
      await c.query("SELECT pg_advisory_xact_lock(hashtextextended($1, 0))", [`marketing-daily:${tenantId}`]);
      const used = (await c.query<{ n: number }>(
        `SELECT count(*)::int AS n FROM beauty_messages WHERE direction = 'outbound' AND idempotency_key ~ '^(campaign|winback):'
           AND created_at > $1::timestamptz - interval '24 hours' AND status <> 'failed'`, [now])).rows[0]!.n;
      if (used >= opts.dailyLimit) return "deferred_limit";
      // 9. message (the unique key makes a repeated run reuse the message instead of creating a second one)
      const key = `campaign:${rec.campaign_id}:${rec.client_id}`;
      const existing = (await c.query<{ id: string; conversation_id: string }>("SELECT id, conversation_id FROM beauty_messages WHERE idempotency_key = $1", [key])).rows[0];
      if (existing) {
        await c.query(`UPDATE beauty_campaign_recipients SET status = 'queued', message_id = $2, conversation_id = $3, queued_at = $4, updated_at = $4 WHERE id = $1`,
          [recipientId, existing.id, existing.conversation_id, now]);
        return "queued";
      }
      // 10. coupon: personal = generated here (the full code exists only in the message; DB keeps hash + hint), shared = campaign code
      let couponId: string | null = null;
      let code: string | undefined;
      let validFrom: Date | null = camp.p_starts ?? null;
      let validTo: Date | null = camp.p_ends ?? null;
      if (camp.coupon_mode === "personal") {
        const from: Date = camp.coupon_valid_from ?? now;
        const to: Date = camp.coupon_valid_to ?? new Date(now.getTime() + COUPON_FALLBACK_DAYS * DAY);
        if (to.getTime() <= from.getTime()) return skip("offer_expired");
        validFrom = from; validTo = to;
        for (let attempt = 0; attempt < 3 && !couponId; attempt++) {
          const candidate = generatePersonalCode();
          const ins = await c.query<{ id: string }>(
            `INSERT INTO beauty_coupons (tenant_id, kind, promotion_id, campaign_id, client_id, code_hash, code, code_hint, valid_from, valid_to, max_uses, uses_count, status, created_at, updated_at)
             VALUES ($1, 'personal', $2, $3, $4, $5, NULL, $6, $7, $8, 1, 0, 'active', $9, $9)
             ON CONFLICT (tenant_id, code_hash) DO NOTHING RETURNING id`,
            [tenantId, camp.promotion_id, rec.campaign_id, rec.client_id, hashCode(candidate), codeHint(candidate), validFrom, validTo, now]);
          if (ins.rows[0]) { couponId = ins.rows[0].id; code = candidate; }
        }
        if (!couponId) throw new Error("coupon_code_collision");
      } else if (coupon) {
        code = coupon.code; validFrom = coupon.valid_from; validTo = coupon.valid_to; couponId = camp.shared_coupon_id;
      }
      const names = camp.promotion_id ? (await c.query<{ name: string }>(
        `SELECT s.name FROM beauty_promotion_services ps JOIN beauty_services s ON s.tenant_id = ps.tenant_id AND s.id = ps.service_id
         WHERE ps.promotion_id = $1 ORDER BY s.name`, [camp.promotion_id])).rows.map((x) => x.name) : [];
      const withButtons = channelButtonsOn(conv.settings);
      const token = randomBytes(16).toString("base64url"); // 128-bit CSPRNG -> 22 chars
      const kb = withButtons ? buildCampaignKeyboard(token, !!code) : null;
      const text = renderCampaignText({
        template: camp.template, type: camp.type, clientName: cl.full_name, currencyLabel: opts.currencyLabel, serviceNames: names, timeZone: tz,
        ...(camp.discount_type ? { discountType: camp.discount_type, discountValue: Number(camp.discount_value) } : {}),
        validFrom, validTo, ...(code ? { code } : {}), buttons: withButtons,
      });
      const msg = await c.query<{ id: string }>(
        `INSERT INTO beauty_messages (tenant_id, conversation_id, direction, sender_type, body, status, idempotency_key, sent_at, created_at, actions)
         VALUES ($1, $2, 'outbound', 'system', $3, 'pending', $4, $5, $5, $6::jsonb) RETURNING id`,
        [tenantId, conv.id, text, key, now, kb ? JSON.stringify(kb.actions) : null]);
      const messageId = msg.rows[0]!.id;
      if (kb) {
        const expires = validTo && camp.coupon_mode !== "none" ? validTo : camp.p_ends ?? new Date(now.getTime() + COUPON_FALLBACK_DAYS * DAY);
        await c.query(
          `INSERT INTO beauty_chat_keyboards (tenant_id, conversation_id, message_id, kind, token, options, campaign_recipient_id, status, expires_at)
           VALUES ($1, $2, $3, 'campaign_offer', $4, $5::jsonb, $6, 'active', $7)`,
          [tenantId, conv.id, messageId, token, JSON.stringify(kb.options), recipientId, expires]);
      }
      // 11. recipient
      await c.query(
        `UPDATE beauty_campaign_recipients SET status = 'queued', message_id = $2, coupon_id = $3, conversation_id = $4, queued_at = $5, updated_at = $5 WHERE id = $1`,
        [recipientId, messageId, camp.coupon_mode === "personal" ? couponId : null, conv.id, now]);
      return "queued";
    });
  }
}
