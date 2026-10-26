import { randomBytes } from "node:crypto";
import { currentTenantId, type TenantDb } from "../db/tenant-db";
import { OUTBOX_MAX_ATTEMPTS } from "../jobs/beauty-common";
import type { ReminderOffset } from "../ports";

export interface DueReminder {
  id: string; appointmentId: string; startsAt: Date;
  /** Legacy offset from beauty_appointments.reminder_option (used only when offsetMinutes is null). */
  offset: ReminderOffset | null;
  /** Planned offset (§22.3); null = legacy row. */
  offsetMinutes: number | null;
  plannedStartsAt: Date | null;
}

/** Everything the reminder job needs about one reminder's appointment, read in one tenant-scoped query. */
export interface ReminderContext {
  status: string; startsAt: Date; clientId: string; timeZone: string;
  service: string | null; locationName: string | null; locationAddress: string | null; legacyOption: string | null;
  quietStartMinutes: number; quietEndMinutes: number;
  /** beauty_client_contact_preferences.method (null = no preference). */
  preferenceMethod: string | null; preferenceConversationId: string | null;
}

export interface ReminderKeyboardRequest { appointmentId: string; expiresAt: Date }

/** Channel flag (§23.2): `settings.assistant.enabled === true` and `buttons !== false`. */
export function channelButtonsOn(settings: unknown): boolean {
  const a = (settings && typeof settings === "object" ? (settings as Record<string, unknown>).assistant : null) as Record<string, unknown> | null;
  return !!a && typeof a === "object" && a.enabled === true && a.buttons !== false;
}

/** callback_data of a stateful button: `{verb}:{token}` (<= 28 bytes). `i:stop` is stateless. */
export function buildReminderKeyboard(appointmentId: string) {
  const token = randomBytes(16).toString("base64url"); // 128-bit CSPRNG -> exactly 22 chars of [A-Za-z0-9_-]
  const labels = { aa: "✅ Підтверджую візит", ac: "❌ Скасувати візит", stop: "🔕 Не нагадувати" };
  return {
    token,
    actions: { rows: [
      [{ label: labels.aa, data: `aa:${token}` }, { label: labels.ac, data: `ac:${token}` }],
      [{ label: labels.stop, data: "i:stop" }],
    ] },
    options: [
      { verb: "aa", label: labels.aa, appointmentId },
      { verb: "ac", label: labels.ac, appointmentId },
      { verb: "i:stop", label: labels.stop },
    ],
  };
}

export type ReminderCloseStatus = "cancelled" | "failed" | "skipped";
export interface PendingMessage { id: string; attempts: number }

export interface SendWindow { fromHour: number; toHour: number; defaultTimezone: string }
/** Contract §8: mailing hours 09:00-20:00 local. */
export const DEFAULT_WINDOW: SendWindow = { fromHour: 9, toHour: 20, defaultTimezone: "Europe/Kyiv" };

/** Only visits completed within this period get a review request (avoids spamming history on first start). */
export const REVIEW_LOOKBACK_DAYS = 3;

/** Selection queries of the pollers (tenant-scoped through TenantDb). */
export class PgPollStore {
  constructor(private readonly db: TenantDb, private readonly window: SendWindow = DEFAULT_WINDOW) {}

  /**
   * Scheduled reminders that are due and have not been turned into an outbox message yet. Only telegram/auto rows
   * (and legacy rows without a method); call points live in beauty_call_tasks, not here.
   */
  async listDueReminders(now: Date, limit: number): Promise<DueReminder[]> {
    const { rows } = await this.db.tx((c) => c.query(
      `SELECT r.id, r.appointment_id, a.starts_at, a.reminder_option, r.offset_minutes, r.planned_starts_at
       FROM beauty_reminders r JOIN beauty_appointments a ON a.id = r.appointment_id
       WHERE r.status = 'scheduled' AND r.scheduled_at <= $1::timestamptz
         AND (r.method IS NULL OR r.method IN ('telegram', 'auto'))
         AND NOT EXISTS (SELECT 1 FROM beauty_messages m WHERE m.idempotency_key = 'reminder:' || r.id::text)
       ORDER BY r.scheduled_at LIMIT $2`, [now, limit]));
    return rows.map((r) => ({
      id: r.id, appointmentId: r.appointment_id, startsAt: r.starts_at,
      offset: r.reminder_option === "1h" || r.reminder_option === "2h" ? r.reminder_option : null,
      offsetMinutes: r.offset_minutes === null ? null : Number(r.offset_minutes),
      plannedStartsAt: r.planned_starts_at,
    }));
  }

  /** Appointment, venue (zone, name, address), tenant quiet hours and the client's preference; null if the appointment is gone. */
  async loadReminderContext(appointmentId: string): Promise<ReminderContext | null> {
    const { rows } = await this.db.tx((c) => c.query(
      `SELECT a.status, a.starts_at, a.client_id, a.reminder_option, l.timezone, l.name AS loc_name, l.address AS loc_address,
              s.name AS service_name,
              to_char(coalesce(st.quiet_start, '22:00'::time), 'HH24:MI') AS quiet_start,
              to_char(coalesce(st.quiet_end, '08:00'::time), 'HH24:MI') AS quiet_end,
              p.method AS pref_method, p.telegram_conversation_id AS pref_conv
       FROM beauty_appointments a
       JOIN beauty_locations l ON l.tenant_id = a.tenant_id AND l.id = a.location_id
       LEFT JOIN beauty_services s ON s.tenant_id = a.tenant_id AND s.id = a.service_id
       LEFT JOIN beauty_reminder_settings st ON st.tenant_id = a.tenant_id
       LEFT JOIN beauty_client_contact_preferences p ON p.tenant_id = a.tenant_id AND p.client_id = a.client_id
       WHERE a.id = $1`, [appointmentId]));
    const r = rows[0];
    if (!r) return null;
    const clock = (v: string) => Number(v.slice(0, 2)) * 60 + Number(v.slice(3, 5));
    return {
      status: r.status, startsAt: r.starts_at, clientId: r.client_id, timeZone: r.timezone, service: r.service_name,
      locationName: r.loc_name, locationAddress: r.loc_address, legacyOption: r.reminder_option,
      quietStartMinutes: clock(r.quiet_start), quietEndMinutes: clock(r.quiet_end),
      preferenceMethod: r.pref_method, preferenceConversationId: r.pref_conv,
    };
  }

  /** Telegram address (§22.5 step 6): the preference's conversation, else the client's latest one, in an active Telegram channel. */
  async findTelegramConversation(clientId: string, preferredConversationId: string | null): Promise<string | null> {
    return this.db.tx(async (c) => {
      const sql = `SELECT c.id FROM beauty_conversations c
                   JOIN beauty_channels ch ON ch.tenant_id = c.tenant_id AND ch.id = c.channel_id
                   WHERE c.client_id = $1 AND ch.is_active AND lower(ch.type) = 'telegram'`;
      if (preferredConversationId) {
        const pref = await c.query<{ id: string }>(`${sql} AND c.id = $2 LIMIT 1`, [clientId, preferredConversationId]);
        if (pref.rows[0]) return pref.rows[0].id;
      }
      const latest = await c.query<{ id: string }>(`${sql} ORDER BY c.last_message_at DESC NULLS LAST, c.created_at DESC LIMIT 1`, [clientId]);
      return latest.rows[0]?.id ?? null;
    });
  }

  /**
   * One transaction: outbound system message with key `reminder:{id}` + `beauty_reminders.message_id`.
   * "duplicate" = the key already exists (second poller / rerun); "closed" = the reminder is no longer scheduled
   * (replanned or cancelled concurrently): the message is rolled back and nothing is sent.
   * `keyboard` (TASK-751, §23.7 p. 10): when set and the channel has `assistant.enabled=true` and `buttons<>false`, the same
   * transaction also writes `beauty_messages.actions` and the `appointment_actions` row of `beauty_chat_keyboards`
   * (token, expires_at = visit start). Rollback of the message rolls the keyboard back too.
   */
  async enqueueReminderMessage(reminderId: string, conversationId: string, text: string, now: Date,
    keyboard?: ReminderKeyboardRequest)
    : Promise<{ result: "created"; messageId: string; keyboardId?: string } | { result: "duplicate" | "closed" }> {
    const tenantId = currentTenantId();
    const closed = new Error("reminder_closed");
    try {
      return await this.db.tx(async (c) => {
        let layout: ReturnType<typeof buildReminderKeyboard> | null = null;
        if (keyboard) {
          const ch = await c.query<{ settings: unknown }>(
            `SELECT ch.settings FROM beauty_conversations c JOIN beauty_channels ch ON ch.tenant_id = c.tenant_id AND ch.id = c.channel_id
             WHERE c.id = $1 AND lower(ch.type) = 'telegram'`, [conversationId]);
          if (ch.rows[0] && channelButtonsOn(ch.rows[0].settings)) layout = buildReminderKeyboard(keyboard.appointmentId);
        }
        const ins = await c.query<{ id: string }>(
          `INSERT INTO beauty_messages (tenant_id, conversation_id, direction, sender_type, body, status, idempotency_key, sent_at, actions)
           VALUES ($1, $2, 'outbound', 'system', $3, 'pending', $4, $5, $6::jsonb)
           ON CONFLICT (tenant_id, idempotency_key) WHERE idempotency_key IS NOT NULL DO NOTHING
           RETURNING id`, [tenantId, conversationId, text, `reminder:${reminderId}`, now, layout ? JSON.stringify(layout.actions) : null]);
        if (!ins.rows[0]) return { result: "duplicate" as const };
        const upd = await c.query(
          "UPDATE beauty_reminders SET message_id = $2, updated_at = $3 WHERE id = $1 AND status = 'scheduled'",
          [reminderId, ins.rows[0].id, now]);
        if (upd.rowCount !== 1) throw closed;
        let keyboardId: string | undefined;
        if (layout && keyboard) {
          const kb = await c.query<{ id: string }>(
            `INSERT INTO beauty_chat_keyboards (tenant_id, conversation_id, message_id, kind, token, options, appointment_id, status, expires_at)
             VALUES ($1, $2, $3, 'appointment_actions', $4, $5::jsonb, $6, 'active', $7) RETURNING id`,
            [tenantId, conversationId, ins.rows[0].id, layout.token, JSON.stringify(layout.options), keyboard.appointmentId, keyboard.expiresAt]);
          keyboardId = kb.rows[0]!.id;
        }
        return keyboardId ? { result: "created" as const, messageId: ins.rows[0].id, keyboardId } : { result: "created" as const, messageId: ins.rows[0].id };
      });
    } catch (err) {
      if (err === closed) return { result: "closed" };
      throw err;
    }
  }

  /** Quiet hours: postpone to the next quiet_end. */
  async deferReminder(id: string, scheduledAt: Date, now: Date): Promise<void> {
    await this.db.tx((c) => c.query(
      "UPDATE beauty_reminders SET scheduled_at = $2, updated_at = $3 WHERE id = $1 AND status = 'scheduled'", [id, scheduledAt, now]));
  }

  /** Completed visits (recent) that have no review-request message yet. */
  async listCompletedWithoutReview(now: Date, limit: number, lookbackDays = REVIEW_LOOKBACK_DAYS): Promise<string[]> {
    const { rows } = await this.db.tx((c) => c.query<{ id: string }>(
      `SELECT a.id FROM beauty_appointments a
       WHERE a.status = 'completed'
         AND a.starts_at <= $1::timestamptz AND a.starts_at >= $1::timestamptz - make_interval(days => $2::int)
         AND NOT EXISTS (SELECT 1 FROM beauty_messages m WHERE m.idempotency_key = 'review:' || a.id::text)
       ORDER BY a.starts_at LIMIT $3`, [now, lookbackDays, limit]));
    return rows.map((r) => r.id);
  }

  /** Closes a scheduled reminder with a reason code (no free text, §22.10): `error` and `skip_reason` both get the code. */
  async closeReminder(id: string, status: ReminderCloseStatus, code: string, now: Date): Promise<void> {
    await this.db.tx((c) => c.query(
      "UPDATE beauty_reminders SET status = $2::varchar, error = $3::varchar, skip_reason = $3::varchar, updated_at = $4 WHERE id = $1 AND status = 'scheduled'",
      [id, status, code, now]));
  }

  /**
   * Pending outbound messages ready for an attempt: fewer than 3 attempts, exponential backoff since the
   * last failure, and campaign/winback only inside the local sending window of the client's last location.
   */
  async listPendingOutbox(now: Date, limit: number, backoffMs: number): Promise<PendingMessage[]> {
    const w = this.window;
    const { rows } = await this.db.tx((c) => c.query(
      `SELECT m.id, m.attempts FROM beauty_messages m
       JOIN beauty_conversations conv ON conv.id = m.conversation_id
       WHERE m.direction = 'outbound' AND m.status = 'pending' AND m.attempts < $2
         AND (m.attempts = 0 OR m.updated_at <= $1::timestamptz - make_interval(secs => ($3::float8 / 1000.0) * power(2, m.attempts - 1)))
         AND (m.idempotency_key IS NULL OR m.idempotency_key !~ '^(campaign|winback):'
              OR EXTRACT(HOUR FROM ($1::timestamptz AT TIME ZONE COALESCE(
                   (SELECT l.timezone FROM beauty_appointments a JOIN beauty_locations l ON l.id = a.location_id
                    WHERE a.client_id = conv.client_id ORDER BY a.starts_at DESC LIMIT 1), $6)))
                 BETWEEN $4 AND $5 - 1)
       ORDER BY m.created_at LIMIT $7`,
      [now, OUTBOX_MAX_ATTEMPTS, backoffMs, w.fromHour, w.toHour, w.defaultTimezone, limit]));
    return rows.map((r) => ({ id: r.id, attempts: r.attempts }));
  }
}
