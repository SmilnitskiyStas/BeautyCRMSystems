import type { PoolClient } from "pg";
import type {
  AppointmentInfo, AppointmentStatus, BeautyDataPort, CampaignInfo, ClientInfo, LapsedClient, OutboundMessage,
  ReminderOption, StoredMessage,
} from "../ports";
import { currentTenantId, type TenantDb } from "../db/tenant-db";

/** beauty_channels.type -> adapter id (mirrors ChannelTypeMap in the backend). */
export const toAdapterId = (type: string): string => (type.toLowerCase() === "facebook" ? "messenger" : type.toLowerCase());

const REMINDER_KEY = /^reminder:([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$/i;

export function kindFromKey(key: string | null): OutboundMessage["kind"] {
  const prefix = key?.split(":", 1)[0];
  return prefix === "reminder" || prefix === "campaign" || prefix === "winback" || prefix === "review" ? prefix : "message";
}

/** Latest active conversation of a client = where we can reach them (channel + chat id). */
const CLIENT_SQL = `
  SELECT cl.id, cl.tenant_id, cl.full_name, cl.marketing_consent, cl.unsubscribed,
         conv.external_chat_id, conv.type
  FROM beauty_clients cl
  JOIN LATERAL (
    SELECT c.external_chat_id, ch.type FROM beauty_conversations c
    JOIN beauty_channels ch ON ch.id = c.channel_id AND ch.is_active
    WHERE c.client_id = cl.id
    ORDER BY c.last_message_at DESC NULLS LAST, c.created_at DESC LIMIT 1
  ) conv ON true`;

interface ClientRow {
  id: string; tenant_id: string; full_name: string; marketing_consent: boolean; unsubscribed: boolean;
  external_chat_id: string; type: string;
}
const toClient = (r: ClientRow): ClientInfo => ({
  id: r.id, tenantId: r.tenant_id, channel: toAdapterId(r.type), address: r.external_chat_id,
  // Unsubscribed overrides consent. Transactional messages (reminders) do not look at this flag.
  marketingConsent: r.marketing_consent && !r.unsubscribed, name: r.full_name,
});

const toStatus = (s: string): StoredMessage["status"] => (s === "sent" ? "sent" : s === "failed" ? "failed" : "queued");

/** BeautyDataPort over PostgreSQL. All access is through TenantDb (RLS + app.tenant_id per transaction). */
export class PgBeautyData implements BeautyDataPort {
  constructor(private readonly db: TenantDb, private readonly now: () => Date = () => new Date()) {}

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

  /** The schema has no campaigns/segments table yet (open question): campaigns cannot be loaded. */
  async getCampaign(): Promise<CampaignInfo | null> { return null; }
  async listCampaignAudience(): Promise<ClientInfo[]> { return []; }

  async listLapsedClients(now: Date, inactiveDays: number): Promise<LapsedClient[]> {
    const { rows } = await this.db.tx((c) => c.query<ClientRow & { last_visit: Date }>(
      `SELECT x.*, v.last_visit FROM (${CLIENT_SQL}
         WHERE cl.deleted_at IS NULL AND cl.marketing_consent AND NOT cl.unsubscribed) x
       JOIN LATERAL (SELECT max(a.starts_at) AS last_visit FROM beauty_appointments a
                     WHERE a.client_id = x.id AND a.status = 'completed') v ON v.last_visit IS NOT NULL
       WHERE v.last_visit < $1::timestamptz - make_interval(days => $2::int)
         AND NOT EXISTS (SELECT 1 FROM beauty_appointments f WHERE f.client_id = x.id
                         AND f.status IN ('pending', 'confirmed') AND f.starts_at >= $1::timestamptz)
       ORDER BY v.last_visit`, [now, inactiveDays]));
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
      `SELECT m.id, m.tenant_id, m.body, m.status, m.idempotency_key, conv.client_id, conv.external_chat_id, ch.type
       FROM beauty_messages m
       JOIN beauty_conversations conv ON conv.id = m.conversation_id
       JOIN beauty_channels ch ON ch.id = conv.channel_id
       WHERE m.id = $1 AND m.direction = 'outbound'`, [id]);
    const r = rows[0];
    if (!r) return null;
    return {
      id: r.id, tenantId: r.tenant_id, clientId: r.client_id ?? "", channel: toAdapterId(r.type), address: r.external_chat_id,
      text: r.body, idempotencyKey: r.idempotency_key ?? `msg:${r.id}`, kind: kindFromKey(r.idempotency_key), status: toStatus(r.status),
    };
  }

  async setMessageStatus(id: string, status: StoredMessage["status"]): Promise<void> {
    const dbStatus = status === "sent" ? "sent" : status === "failed" ? "failed" : "pending";
    const now = this.now();
    await this.db.tx(async (c) => {
      const { rows } = await c.query<{ idempotency_key: string | null }>(
        `UPDATE beauty_messages SET status = $2::varchar, updated_at = $3,
           last_error = CASE WHEN $2::varchar = 'sent' THEN NULL ELSE last_error END,
           sent_at = CASE WHEN $2::varchar = 'sent' THEN $3::timestamptz ELSE sent_at END
         WHERE id = $1 AND direction = 'outbound' RETURNING idempotency_key`, [id, dbStatus, now]);
      const rid = rows[0]?.idempotency_key ? REMINDER_KEY.exec(rows[0].idempotency_key)?.[1] : undefined;
      if (rid && status !== "queued") {
        await c.query(
          `UPDATE beauty_reminders SET status = $2::varchar, sent_at = CASE WHEN $2::varchar = 'sent' THEN $3::timestamptz END,
             updated_at = $3
           WHERE id = $1 AND status = 'scheduled'`, [rid, status === "sent" ? "sent" : "failed", now]);
      }
    });
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
      const rid = rows[0]?.idempotency_key ? REMINDER_KEY.exec(rows[0].idempotency_key)?.[1] : undefined;
      if (rid && final) {
        await c.query("UPDATE beauty_reminders SET status = 'failed', error = $2, updated_at = $3 WHERE id = $1 AND status = 'scheduled'",
          [rid, err, now]);
      }
    });
  }
}
