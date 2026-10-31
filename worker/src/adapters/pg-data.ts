import type { PoolClient } from "pg";
import type {
  AppointmentInfo, AppointmentStatus, ChatActions, BeautyDataPort, CampaignEnqueueOptions, CampaignEnqueueResult, CampaignInfo, CampaignRecipientRef, ClientInfo, LapsedClient, OutboundMessage,
  ReminderOption, StoredMessage,
} from "../ports";
import { currentTenantId, type TenantDb } from "../db/tenant-db";
import { CALL_TASK_CODES, DEFAULT_CLIENT_INTERVAL_DAYS, isChannelErrorCode } from "../jobs/beauty-common";
import { PgCampaigns } from "./pg-campaign";

/** beauty_channels.type -> adapter id (mirrors ChannelTypeMap in the backend). */
export const toAdapterId = (type: string): string => (type.toLowerCase() === "facebook" ? "messenger" : type.toLowerCase());

const REMINDER_KEY = /^reminder:([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$/i;

export function kindFromKey(key: string | null): OutboundMessage["kind"] {
  const prefix = key?.split(":", 1)[0];
  return prefix === "reminder" || prefix === "campaign" || prefix === "winback" || prefix === "review" ? prefix : "message";
}

/** Latest active conversation of a client = where we can reach them (channel + chat id). */
const CLIENT_SQL = `
  SELECT cl.id, cl.tenant_id, cl.full_name, coalesce(mc.level <> 'none', false) AS marketing_consent, cl.unsubscribed,
         coalesce(pref.method = 'none', false) AS pref_none,
         conv.external_chat_id, conv.type
  FROM beauty_clients cl
  LEFT JOIN beauty_marketing_consents mc ON mc.tenant_id = cl.tenant_id AND mc.client_id = cl.id AND mc.channel = 'telegram'
  LEFT JOIN beauty_client_contact_preferences pref ON pref.tenant_id = cl.tenant_id AND pref.client_id = cl.id
  JOIN LATERAL (
    SELECT c.external_chat_id, ch.type FROM beauty_conversations c
    JOIN beauty_channels ch ON ch.id = c.channel_id AND ch.is_active
    WHERE c.client_id = cl.id
    ORDER BY c.last_message_at DESC NULLS LAST, c.created_at DESC LIMIT 1
  ) conv ON true`;

interface ClientRow {
  id: string; tenant_id: string; full_name: string; marketing_consent: boolean; unsubscribed: boolean; pref_none: boolean;
  external_chat_id: string; type: string;
}
const toClient = (r: ClientRow): ClientInfo => ({
  id: r.id, tenantId: r.tenant_id, channel: toAdapterId(r.type), address: r.external_chat_id,
  // Unsubscribed or preference `none` (STOP) overrides consent. Reminders do not look at these flags (they have their own checks).
  marketingConsent: r.marketing_consent && !r.unsubscribed && !r.pref_none, optedOut: r.unsubscribed || r.pref_none, name: r.full_name,
});

/** beauty_messages.actions -> layout; tolerant to key casing (.NET `Rows/Label/Data`), anything malformed = no keyboard. */
export function parseActions(raw: unknown): ChatActions | null {
  if (!raw || typeof raw !== "object") return null;
  const o = raw as Record<string, unknown>;
  const rowsRaw = o.rows ?? o.Rows;
  if (!Array.isArray(rowsRaw)) return null;
  const rows: ChatActions["rows"] = [];
  for (const row of rowsRaw) {
    if (!Array.isArray(row)) return null;
    const out: { label: string; data: string }[] = [];
    for (const b of row) {
      const x = b as Record<string, unknown> | null;
      const label = x?.label ?? x?.Label, data = x?.data ?? x?.Data;
      if (typeof label !== "string" || typeof data !== "string") return null;
      out.push({ label, data });
    }
    if (out.length) rows.push(out);
  }
  return rows.length ? { rows } : null;
}

const toStatus = (s: string): StoredMessage["status"] => (s === "sent" ? "sent" : s === "failed" ? "failed" : "queued");

/** BeautyDataPort over PostgreSQL. All access is through TenantDb (RLS + app.tenant_id per transaction). */
export class PgBeautyData implements BeautyDataPort {
  private readonly campaigns: PgCampaigns;
  constructor(private readonly db: TenantDb, private readonly now: () => Date = () => new Date()) { this.campaigns = new PgCampaigns(db); }

  async getAppointment(id: string): Promise<AppointmentInfo | null> {
    const { rows } = await this.db.tx((c) => c.query(
      "SELECT id, tenant_id, client_id, starts_at, status, reminder_option FROM beauty_appointments WHERE id = $1", [id]));
    const r = rows[0];
    return r ? {
      id: r.id, tenantId: r.tenant_id, clientId: r.client_id, startsAt: r.starts_at,
      status: r.status as AppointmentStatus, reminderOption: r.reminder_option as ReminderOption,
    } : null;
  }

  async getClient(id: string): Promise<ClientInfo | null> {
    const { rows } = await this.db.tx((c) => c.query<ClientRow>(`${CLIENT_SQL} WHERE cl.id = $1 AND cl.deleted_at IS NULL`, [id]));
    return rows[0] ? toClient(rows[0]) : null;
  }

  getCampaign(id: string): Promise<CampaignInfo | null> { return this.campaigns.getCampaign(id); }
  listCampaignAudience(campaignId: string, limit: number, afterId?: string): Promise<CampaignRecipientRef[]> { return this.campaigns.listCampaignAudience(campaignId, limit, afterId); }
  listDueCampaigns(now: Date): Promise<string[]> { return this.campaigns.listDueCampaigns(now); }
  startCampaign(id: string, now: Date): Promise<boolean> { return this.campaigns.startCampaign(id, now); }
  finishCampaignIfDrained(id: string, now: Date): Promise<boolean> { return this.campaigns.finishCampaignIfDrained(id, now); }
  enqueueCampaignMessage(recipientId: string, now: Date, opts: CampaignEnqueueOptions): Promise<CampaignEnqueueResult> { return this.campaigns.enqueueCampaignMessage(recipientId, now, opts); }
  campaignSendGuard(messageId: string) { return this.campaigns.campaignSendGuard(messageId); }

  async listLapsedClients(now: Date, inactiveDays: number, minIntervalDays = DEFAULT_CLIENT_INTERVAL_DAYS): Promise<LapsedClient[]> {
    const { rows } = await this.db.tx((c) => c.query<ClientRow & { last_visit: Date }>(
      `SELECT x.*, v.last_visit FROM (${CLIENT_SQL}
         WHERE cl.deleted_at IS NULL AND coalesce(mc.level IN ('full', 'selective'), false) AND NOT cl.unsubscribed
               AND NOT coalesce(pref.method = 'none', false)) x
       JOIN LATERAL (SELECT max(a.starts_at) AS last_visit FROM beauty_appointments a
                     WHERE a.client_id = x.id AND a.status = 'completed') v ON v.last_visit IS NOT NULL
       WHERE v.last_visit < $1::timestamptz - make_interval(days => $2::int)
         AND NOT EXISTS (SELECT 1 FROM beauty_appointments f WHERE f.client_id = x.id
                         AND f.status IN ('pending', 'confirmed') AND f.starts_at >= $1::timestamptz)
         AND NOT EXISTS (SELECT 1 FROM beauty_campaign_recipients r WHERE r.client_id = x.id AND r.status IN ('queued', 'sent')
                         AND r.queued_at > $1::timestamptz - make_interval(days => $3::int))
         AND NOT EXISTS (SELECT 1 FROM beauty_messages m WHERE m.idempotency_key LIKE 'winback:' || x.id::text || ':%'
                         AND m.created_at > $1::timestamptz - make_interval(days => $3::int))
       ORDER BY v.last_visit`, [now, inactiveDays, minIntervalDays]));
    return rows.map((r) => ({ ...toClient(r), lastVisitAt: r.last_visit }));
  }

  async upsertMessage(msg: OutboundMessage): Promise<{ message: StoredMessage; created: boolean }> {
    if (msg.tenantId !== currentTenantId()) throw new Error("Message tenant does not match the tenant in scope");
    return this.db.tx(async (c) => {
      const conv = await c.query<{ id: string }>(
        `SELECT id FROM beauty_conversations WHERE client_id = $1 AND external_chat_id = $2
         ORDER BY last_message_at DESC NULLS LAST LIMIT 1`, [msg.clientId, msg.address]);
      if (!conv.rows[0]) throw new Error(`No conversation for client ${msg.clientId} / ${msg.address}`);
      const ins = await c.query<{ id: string }>(
        `INSERT INTO beauty_messages (tenant_id, conversation_id, direction, sender_type, body, status, idempotency_key, sent_at)
         VALUES ($1, $2, 'outbound', 'system', $3, 'pending', $4, $5)
         ON CONFLICT (tenant_id, idempotency_key) WHERE idempotency_key IS NOT NULL DO NOTHING
         RETURNING id`, [msg.tenantId, conv.rows[0].id, msg.text, msg.idempotencyKey, this.now()]);
      const created = ins.rows.length > 0;
      const id = created ? ins.rows[0]!.id
        : (await c.query<{ id: string }>("SELECT id FROM beauty_messages WHERE idempotency_key = $1", [msg.idempotencyKey])).rows[0]!.id;
      return { message: (await this.loadMessage(c, id))!, created };
    });
  }

  async getMessage(id: string): Promise<StoredMessage | null> {
    return this.db.tx((c) => this.loadMessage(c, id));
  }

  private async loadMessage(c: PoolClient, id: string): Promise<StoredMessage | null> {
    const { rows } = await c.query(
      `SELECT m.id, m.tenant_id, m.body, m.status, m.idempotency_key, m.actions, conv.client_id, conv.external_chat_id, ch.type
       FROM beauty_messages m
       JOIN beauty_conversations conv ON conv.id = m.conversation_id
       JOIN beauty_channels ch ON ch.id = conv.channel_id
       WHERE m.id = $1 AND m.direction = 'outbound'`, [id]);
    const r = rows[0];
    if (!r) return null;
    return {
      id: r.id, tenantId: r.tenant_id, clientId: r.client_id ?? "", channel: toAdapterId(r.type), address: r.external_chat_id,
      text: r.body, idempotencyKey: r.idempotency_key ?? `msg:${r.id}`, kind: kindFromKey(r.idempotency_key), status: toStatus(r.status),
      actions: parseActions(r.actions),
    };
  }

  async setMessageStatus(id: string, status: StoredMessage["status"], externalMessageId?: string | null): Promise<void> {
    const ext = externalMessageId ? externalMessageId.slice(0, 256) : null;
    const dbStatus = status === "sent" ? "sent" : status === "failed" ? "failed" : "pending";
    const now = this.now();
    await this.db.tx(async (c) => {
      const { rows } = await c.query<{ idempotency_key: string | null }>(
        `UPDATE beauty_messages SET status = $2::varchar, updated_at = $3,
           external_message_id = coalesce($4::varchar, external_message_id),
           last_error = CASE WHEN $2::varchar = 'sent' THEN NULL ELSE last_error END,
           sent_at = CASE WHEN $2::varchar = 'sent' THEN $3::timestamptz ELSE sent_at END
         WHERE id = $1 AND direction = 'outbound' RETURNING idempotency_key`, [id, dbStatus, now, ext]);
      if (status === "sent") await PgCampaigns.applyDelivery(c, rows[0]?.idempotency_key ?? null, { sent: true }, now);
      const rid = rows[0]?.idempotency_key ? REMINDER_KEY.exec(rows[0].idempotency_key)?.[1] : undefined;
      if (rid && status !== "queued") {
        await c.query(
          `UPDATE beauty_reminders SET status = $2::varchar, sent_at = CASE WHEN $2::varchar = 'sent' THEN $3::timestamptz END,
             updated_at = $3
           WHERE id = $1 AND status = 'scheduled'`, [rid, status === "sent" ? "sent" : "failed", now]);
      }
    });
  }

  async claimForSend(id: string): Promise<boolean> {
    const { rows } = await this.db.tx((c) => c.query<{ id: string }>(
      `UPDATE beauty_messages SET status = 'sending', updated_at = $2
       WHERE id = $1 AND direction = 'outbound' AND status = 'pending' RETURNING id`, [id, this.now()]));
    return rows.length === 1;
  }

  async countMarketingSentSince(since: Date): Promise<number> {
    const { rows } = await this.db.tx((c) => c.query<{ n: number }>(
      `SELECT count(*)::int AS n FROM beauty_messages
       WHERE direction = 'outbound' AND status = 'sent' AND sent_at >= $1::timestamptz
         AND idempotency_key ~ '^(campaign|winback):'`, [since]));
    return rows[0]?.n ?? 0;
  }

  async recordSendFailure(id: string, attempt: number, error: string, final: boolean): Promise<void> {
    const now = this.now();
    const err = error.length > 1000 ? error.slice(0, 1000) : error;
    await this.db.tx(async (c) => {
      const { rows } = await c.query<{ idempotency_key: string | null }>(
        `UPDATE beauty_messages SET attempts = $2, last_error = $3, status = $4, updated_at = $5
         WHERE id = $1 AND direction = 'outbound' RETURNING idempotency_key`, [id, attempt, err, final ? "failed" : "pending", now]);
      if (final) await PgCampaigns.applyDelivery(c, rows[0]?.idempotency_key ?? null, { code: err }, now);
      const rid = rows[0]?.idempotency_key ? REMINDER_KEY.exec(rows[0].idempotency_key)?.[1] : undefined;
      if (rid && final) {
        const code = isChannelErrorCode(err) ? err : null;
        const upd = await c.query(
          `UPDATE beauty_reminders SET status = 'failed', error = $2, skip_reason = $4, updated_at = $3
           WHERE id = $1 AND status = 'scheduled'`, [rid, err, now, code]);
        // §22.5: Telegram cannot reach the client -> a call task for the administrator (only with a phone and call_enabled;
        // the phone itself is not copied). Preferences stay unchanged. UNIQUE (tenant_id, reminder_id) = one task per reminder.
        if (upd.rowCount === 1 && code && CALL_TASK_CODES.has(code)) {
          await c.query(
            `INSERT INTO beauty_call_tasks (tenant_id, appointment_id, client_id, location_id, reminder_id, reason, offset_minutes, due_at, status)
             SELECT r.tenant_id, r.appointment_id, a.client_id, a.location_id, r.id, 'delivery_failed', r.offset_minutes, $2, 'open'
             FROM beauty_reminders r
             JOIN beauty_appointments a ON a.tenant_id = r.tenant_id AND a.id = r.appointment_id
             JOIN beauty_clients cl ON cl.tenant_id = a.tenant_id AND cl.id = a.client_id
             LEFT JOIN beauty_reminder_settings st ON st.tenant_id = r.tenant_id
             WHERE r.id = $1 AND btrim(coalesce(cl.phone, '')) <> '' AND coalesce(st.call_enabled, true)
             ON CONFLICT (tenant_id, reminder_id) WHERE reminder_id IS NOT NULL DO NOTHING`, [rid, now]);
        }
      }
    });
  }
}
