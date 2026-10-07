import type { TenantDb } from "../db/tenant-db";
import { OUTBOX_MAX_ATTEMPTS } from "../jobs/beauty-common";
import type { ReminderOffset } from "../ports";

export interface DueReminder { id: string; appointmentId: string; startsAt: Date; offset: ReminderOffset | null }
export interface PendingMessage { id: string; attempts: number }

export interface SendWindow { fromHour: number; toHour: number; defaultTimezone: string }
/** Contract §8: mailing hours 09:00-20:00 local. */
export const DEFAULT_WINDOW: SendWindow = { fromHour: 9, toHour: 20, defaultTimezone: "Europe/Kyiv" };

/** Selection queries of the pollers (tenant-scoped through TenantDb). */
export class PgPollStore {
  constructor(private readonly db: TenantDb, private readonly window: SendWindow = DEFAULT_WINDOW) {}

  /** Scheduled reminders that are due and have not been turned into an outbox message yet. */
  async listDueReminders(now: Date, limit: number): Promise<DueReminder[]> {
    const { rows } = await this.db.tx((c) => c.query(
      `SELECT r.id, r.appointment_id, a.starts_at, a.reminder_option
       FROM beauty_reminders r JOIN beauty_appointments a ON a.id = r.appointment_id
       WHERE r.status = 'scheduled' AND r.scheduled_at <= $1::timestamptz
         AND NOT EXISTS (SELECT 1 FROM beauty_messages m WHERE m.idempotency_key = 'reminder:' || r.id::text)
       ORDER BY r.scheduled_at LIMIT $2`, [now, limit]));
    return rows.map((r) => ({
      id: r.id, appointmentId: r.appointment_id, startsAt: r.starts_at,
      offset: r.reminder_option === "1h" || r.reminder_option === "2h" ? r.reminder_option : null,
    }));
  }

  async closeReminder(id: string, status: "cancelled" | "failed", reason: string, now: Date): Promise<void> {
    await this.db.tx((c) => c.query(
      "UPDATE beauty_reminders SET status = $2, error = $3, updated_at = $4 WHERE id = $1 AND status = 'scheduled'",
      [id, status, reason, now]));
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
