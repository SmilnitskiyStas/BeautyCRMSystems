import pg from "pg";
import { HttpChannelSender } from "./adapters/channel-sender";
import { loadKey } from "./adapters/channel-crypto";
import { PgBeautyData } from "./adapters/pg-data";
import { DEFAULT_WINDOW, PgPollStore } from "./adapters/pg-poll-store";
import { LoggerNotificationLog, PgAdvisoryIdempotency, PgQueuePort, consoleLogger } from "./adapters/pg-support";
import { TenantDb } from "./db/tenant-db";
import { StaticTenantDirectory, type TenantDirectory } from "./db/tenants";
import type { BeautyDeps } from "./ports";
import { Scheduler } from "./scheduler/scheduler";
import { pollOutbox, pollReminders, pollWinback, type PollContext } from "./scheduler/pollers";

const MIN = 60_000;

export interface WorkerRuntime { scheduler: Scheduler; ctx: PollContext; stop(): Promise<void> }

/** Wires the DB-driven worker: `WORKER_DATABASE_URL` must be a role WITHOUT BYPASSRLS. */
export async function createWorker(env: NodeJS.ProcessEnv = process.env, tenants?: TenantDirectory): Promise<WorkerRuntime> {
  const url = env.WORKER_DATABASE_URL;
  if (!url) throw new Error("WORKER_DATABASE_URL is not set");
  const db = TenantDb.create(url, 10);
  await db.assertRlsEnforced();
  const lockPool = new pg.Pool({ connectionString: url, max: 10 });
  const now = () => new Date();
  const deps: BeautyDeps = {
    now,
    data: new PgBeautyData(db, now),
    sender: new HttpChannelSender(db, loadKey(env["Channels__EncryptionKey"]), fetch, now),
    idempotency: new PgAdvisoryIdempotency(lockPool),
    log: new LoggerNotificationLog(consoleLogger),
    queue: new PgQueuePort(),
  };
  const store = new PgPollStore(db, { ...DEFAULT_WINDOW, defaultTimezone: env.WORKER_DEFAULT_TIMEZONE ?? DEFAULT_WINDOW.defaultTimezone });
  const ctx: PollContext = { deps, store, tenants: tenants ?? StaticTenantDirectory.fromEnv(env.WORKER_TENANT_IDS), logger: consoleLogger };
  const scheduler = new Scheduler([
    { name: "beauty.reminders", everyMs: 1 * MIN, run: () => pollReminders(ctx) },
    { name: "beauty.outbox", everyMs: 30_000, run: () => pollOutbox(ctx) },
    { name: "beauty.winback", everyMs: 24 * 60 * MIN, run: () => pollWinback(ctx) },
  ], consoleLogger);
  return {
    scheduler, ctx,
    stop: async () => { await scheduler.stop(); await lockPool.end(); await db.close(); },
  };
}

// Entry point: `npm start`
if (process.argv[1] && /main\.(ts|js)$/.test(process.argv[1])) {
  const rt = await createWorker();
  rt.scheduler.start();
  for (const sig of ["SIGINT", "SIGTERM"] as const) process.on(sig, () => void rt.stop().then(() => process.exit(0)));
}
