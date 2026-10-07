import type pg from "pg";
import type { IdempotencyPort, NotificationLogEntry, NotificationLogPort, QueuePort } from "../ports";
import { currentTenantId } from "../db/tenant-db";

/**
 * Idempotency claims via PostgreSQL session advisory locks, one dedicated connection per held claim
 * (use a separate pool from the data pool, and a direct connection, not transaction-mode PgBouncer).
 * A crashed worker drops its connection, so its claims vanish by themselves (no stuck keys). A claim
 * only guards concurrency; "already sent" is enforced by beauty_messages.status (re-checked under the claim).
 */
export class PgAdvisoryIdempotency implements IdempotencyPort {
  private readonly held = new Map<string, pg.PoolClient>();
  constructor(private readonly lockPool: pg.Pool) {}

  private id(key: string): string { return `${currentTenantId()}|${key}`; }

  async claim(key: string): Promise<boolean> {
    const id = this.id(key);
    if (this.held.has(id)) return false;
    const client = await this.lockPool.connect();
    try {
      const { rows } = await client.query<{ ok: boolean }>("SELECT pg_try_advisory_lock(hashtextextended($1, 0)) AS ok", [id]);
      if (!rows[0]?.ok) { client.release(); return false; }
    } catch (err) {
      client.release(err as Error);
      throw err;
    }
    this.held.set(id, client);
    return true;
  }

  async complete(key: string): Promise<void> { await this.unlock(key); }
  async release(key: string): Promise<void> { await this.unlock(key); }

  private async unlock(key: string): Promise<void> {
    const id = this.id(key);
    const client = this.held.get(id);
    if (!client) return;
    this.held.delete(id);
    try {
      await client.query("SELECT pg_advisory_unlock(hashtextextended($1, 0))", [id]);
      client.release();
    } catch (err) {
      client.release(err as Error); // destroying the connection drops the lock
      throw err;
    }
  }
}

export interface Logger {
  info(msg: string, fields?: Record<string, unknown>): void;
  error(msg: string, fields?: Record<string, unknown>): void;
}

export const consoleLogger: Logger = {
  info: (msg, f) => console.log(JSON.stringify({ level: "info", msg, ...f })),
  error: (msg, f) => console.error(JSON.stringify({ level: "error", msg, ...f })),
};

/**
 * notification-log: structured log line per event. Durable per-message state (status/attempts/last_error)
 * lives in beauty_messages; a separate notification_log table would need a migration (open question).
 */
export class LoggerNotificationLog implements NotificationLogPort {
  constructor(private readonly logger: Logger = consoleLogger) {}
  async append(e: NotificationLogEntry): Promise<void> {
    const log = e.status === "failed" || e.status === "retry" ? this.logger.error : this.logger.info;
    log.call(this.logger, "notification", { ...e, at: e.at.toISOString() });
  }
}

/**
 * QueuePort for the DB-driven outbox: the table IS the queue. A message row with status 'pending' is the
 * outbox entry and beauty_reminders is the reminder schedule, so these enqueues are intentionally no-ops.
 * Campaigns have no table yet -> fail loudly instead of silently dropping.
 */
export class PgQueuePort implements QueuePort {
  async enqueueOutbox(): Promise<void> { /* row already inserted with status 'pending' */ }
  async enqueueReminder(): Promise<void> { /* beauty_reminders.scheduled_at is polled */ }
  async enqueueCampaign(): Promise<void> { throw new Error("Campaigns are not supported by the DB schema yet (no campaigns table)"); }
}
