import pg from "pg";
import { HttpChannelSender } from "./adapters/channel-sender";
import { loadKey } from "./adapters/channel-crypto";
import { MockChannelSender, useMockChannels } from "./adapters/mock-channel-sender";
import { PgBeautyData } from "./adapters/pg-data";
import { DEFAULT_WINDOW, PgPollStore } from "./adapters/pg-poll-store";
import { LoggerNotificationLog, PgAdvisoryIdempotency, PgQueuePort, consoleLogger } from "./adapters/pg-support";
import { TenantDb } from "./db/tenant-db";
import { assertMarketingLegalGate } from "./marketing-gate";
import { StaticTenantDirectory, type TenantDirectory } from "./db/tenants";
import { DEFAULT_CLIENT_INTERVAL_DAYS, DEFAULT_MARKETING_DAILY_LIMIT } from "./jobs/beauty-common";
import { DEFAULT_CAMPAIGN_BATCH, DEFAULT_FAVORITE_SERVICES_DAYS } from "./jobs/beauty-campaign";
import { DEFAULT_MIN_LEAD_MINUTES } from "./jobs/beauty-reminder";
import type { BeautyDeps } from "./ports";
import { Scheduler } from "./scheduler/scheduler";
import { pollCampaigns, pollOutbox, pollReminders, pollReviews, pollWinback, type PollContext } from "./scheduler/pollers";

const MIN = 60_000;

export interface WorkerRuntime { scheduler: Scheduler; ctx: PollContext; stop(): Promise<void> }

/** Wires the DB-driven worker: `WORKER_DATABASE_URL` must be a role WITHOUT BYPASSRLS. */
export async function createWorker(env: NodeJS.ProcessEnv = process.env, tenants?: TenantDirectory): Promise<WorkerRuntime> {
  assertMarketingLegalGate(env);
  const url = env.WORKER_DATABASE_URL;
  if (!url) throw new Error("WORKER_DATABASE_URL is not set");
  const db = TenantDb.create(url, 10);
  await db.assertRlsEnforced();
  const lockPool = new pg.Pool({ connectionString: url, max: 10 });
  const now = () => new Date();
  const posInt = (v: string | undefined, d: number) => (Number.isInteger(Number(v)) && Number(v) > 0 ? Number(v) : d);
  const defaultTimezone = env.WORKER_DEFAULT_TIMEZONE ?? DEFAULT_WINDOW.defaultTimezone;
  const deps: BeautyDeps = {
    now,
    data: new PgBeautyData(db, now),
    // Channels__UseMocks=true (Development only) -> no real Telegram/Instagram HTTP, as in the API.
    sender: useMockChannels(env) ? new MockChannelSender() : new HttpChannelSender(db, loadKey(env["Channels__EncryptionKey"]), fetch, now),
    idempotency: new PgAdvisoryIdempotency(lockPool),
    log: new LoggerNotificationLog(consoleLogger),
    queue: new PgQueuePort(),
    marketingDailyLimit: Number(env.WORKER_MARKETING_DAILY_LIMIT) > 0 ? Number(env.WORKER_MARKETING_DAILY_LIMIT) : DEFAULT_MARKETING_DAILY_LIMIT,
    marketingClientIntervalDays: posInt(env.WORKER_MARKETING_CLIENT_INTERVAL_DAYS, DEFAULT_CLIENT_INTERVAL_DAYS),
    campaign: {
      batch: Math.min(500, posInt(env.WORKER_CAMPAIGN_BATCH, DEFAULT_CAMPAIGN_BATCH)), defaultTimezone,
      favoriteServicesDays: posInt(env.WORKER_MARKETING_FAVORITE_SERVICES_DAYS, DEFAULT_FAVORITE_SERVICES_DAYS),
    },
  };
  const store = new PgPollStore(db, { ...DEFAULT_WINDOW, defaultTimezone: env.WORKER_DEFAULT_TIMEZONE ?? DEFAULT_WINDOW.defaultTimezone });
  const lead = Number(env.WORKER_REMINDER_MIN_LEAD_MINUTES);
  const ctx: PollContext = {
    deps, store, tenants: tenants ?? StaticTenantDirectory.fromEnv(env.WORKER_TENANT_IDS), logger: consoleLogger,
    // §22.2: without an explicit "true" reminders are not sent (rows wait); legal gate §22.14 p. 1 outside dev.
    remindersEnabled: /^true$/i.test(env.WORKER_REMINDERS_ENABLED ?? ""),
    // TASK-760 (M2): review requests only with an explicit WORKER_REVIEWS_ENABLED=true.
    reviewsEnabled: /^true$/i.test(env.WORKER_REVIEWS_ENABLED ?? ""),
    // §26.2: campaigns and winback are off unless explicitly enabled (legal gate §26.16).
    campaignsEnabled: /^true$/i.test(env.WORKER_CAMPAIGNS_ENABLED ?? ""),
    winbackEnabled: /^true$/i.test(env.WORKER_WINBACK_ENABLED ?? ""),
    reminderButtonsEnabled: /^true$/i.test(env.WORKER_REMINDER_BUTTONS_ENABLED ?? ""),
    reminderMinLeadMinutes: Number.isFinite(lead) && lead >= 0 && env.WORKER_REMINDER_MIN_LEAD_MINUTES ? lead : DEFAULT_MIN_LEAD_MINUTES,
  };
  const scheduler = new Scheduler([
    { name: "beauty.reminders", everyMs: 1 * MIN, run: () => pollReminders(ctx) },
    { name: "beauty.outbox", everyMs: 30_000, run: () => pollOutbox(ctx) },
    { name: "beauty.reviews", everyMs: 10 * MIN, run: () => pollReviews(ctx) },
    { name: "beauty.campaigns", everyMs: 1 * MIN, run: () => pollCampaigns(ctx) },
    // hourly: a pass deferred by the 09-20 window / daily limit resumes; keys are per lapse and frequency is checked, so reruns never duplicate
    { name: "beauty.winback", everyMs: 60 * MIN, run: () => pollWinback(ctx) },
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
