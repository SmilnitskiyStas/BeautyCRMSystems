import type { PgPollStore } from "../adapters/pg-poll-store";
import type { Logger } from "../adapters/pg-support";
import { withTenant } from "../db/tenant-db";
import type { TenantDirectory } from "../db/tenants";
import { processOutbox } from "../jobs/beauty-outbox";
import { processReviewRequest } from "../jobs/beauty-review";
import { processDueReminder } from "../jobs/beauty-reminder";
import { processWinback } from "../jobs/beauty-winback";
import { processCampaign } from "../jobs/beauty-campaign";
import type { BeautyDeps } from "../ports";

export interface PollContext {
  deps: BeautyDeps;
  store: PgPollStore;
  tenants: TenantDirectory;
  logger: Logger;
  batchSize?: number;
  backoffMs?: number;
  /**
   * WORKER_REMINDERS_ENABLED (§22.2). `false` => pollReminders does nothing. main.ts always sets it from the env
   * (default false); an unset value means "enabled" only for in-process callers/tests that build the context by hand.
   */
  remindersEnabled?: boolean;
  /** WORKER_REMINDER_MIN_LEAD_MINUTES (default 15). */
  reminderMinLeadMinutes?: number;
  /** WORKER_REMINDER_BUTTONS_ENABLED (§23.2, default false): inline buttons under the reminder (also needs the channel flag). */
  reminderButtonsEnabled?: boolean;
  /** WORKER_REVIEWS_ENABLED (TASK-760, default false in main.ts). `false` => pollReviews does nothing; unset = enabled (hand-built contexts/tests). */
  reviewsEnabled?: boolean;
  /** WORKER_CAMPAIGNS_ENABLED (§26.2, default false in main.ts). `false` => pollCampaigns does nothing; unset = enabled (hand-built contexts/tests). */
  campaignsEnabled?: boolean;
  /** WORKER_WINBACK_ENABLED (§26.2, default false in main.ts). `false` => pollWinback does nothing; unset = enabled (hand-built contexts/tests). */
  winbackEnabled?: boolean;
}

export interface PollResult { processed: number; errors: number }

/** Runs `fn` for every tenant; one failing tenant/item is logged and counted, never hides the others. */
async function forEachTenant(ctx: PollContext, fn: () => Promise<PollResult>, what: string): Promise<PollResult> {
  const total: PollResult = { processed: 0, errors: 0 };
  for (const tenantId of await ctx.tenants.listTenantIds()) {
    try {
      const r = await withTenant(tenantId, fn);
      total.processed += r.processed;
      total.errors += r.errors;
    } catch (err) {
      total.errors++;
      ctx.logger.error(`${what} failed for tenant`, { tenantId, errorCode: errorCode(err) });
    }
  }
  return total;
}

/**
 * Due beauty_reminders -> outbox messages (idempotent per reminder row, key `reminder:{id}`), checks of §22.5.
 * Runs every minute, only with WORKER_REMINDERS_ENABLED=true. Logs carry ids and codes only.
 */
export function pollReminders(ctx: PollContext): Promise<PollResult> {
  if (ctx.remindersEnabled === false) return Promise.resolve({ processed: 0, errors: 0 });
  return forEachTenant(ctx, async () => {
    const res: PollResult = { processed: 0, errors: 0 };
    for (const r of await ctx.store.listDueReminders(ctx.deps.now(), ctx.batchSize ?? 100)) {
      try {
        await processDueReminder(ctx.deps, ctx.store, r, { minLeadMinutes: ctx.reminderMinLeadMinutes, buttonsEnabled: ctx.reminderButtonsEnabled === true });
        res.processed++;
      } catch (err) {
        res.errors++;
        ctx.logger.error("reminder failed", { reminderId: r.id, errorCode: errorCode(err) });
      }
    }
    return res;
  }, "pollReminders");
}

/** Error class/code only: DB and library messages can echo values (personal data). */
function errorCode(err: unknown): string {
  const e = err as { code?: unknown; name?: unknown } | null;
  return typeof e?.code === "string" ? e.code : typeof e?.name === "string" ? e.name : "error";
}

/**
 * Pending outbound messages -> channel send. A failed attempt is stored in beauty_messages
 * (attempts/last_error); after 3 attempts in total the message becomes 'failed'.
 */
export function pollOutbox(ctx: PollContext): Promise<PollResult> {
  return forEachTenant(ctx, async () => {
    const res: PollResult = { processed: 0, errors: 0 };
    for (const m of await ctx.store.listPendingOutbox(ctx.deps.now(), ctx.batchSize ?? 50, ctx.backoffMs ?? 5_000)) {
      try {
        await processOutbox(ctx.deps, { messageId: m.id }, m.attempts);
        res.processed++;
      } catch (err) {
        // already persisted by processOutbox (attempt/last_error/log); counted here so the scheduler sees it
        res.errors++;
        ctx.logger.error("outbox send failed", { messageId: m.id, attempt: m.attempts + 1, errorCode: errorCode(err) });
      }
    }
    return res;
  }, "pollOutbox");
}

/** "Давно не були": daily; keys are per lapse, so extra runs never duplicate. */
export function pollWinback(ctx: PollContext, inactiveDays?: number): Promise<PollResult> {
  if (ctx.winbackEnabled === false) return Promise.resolve({ processed: 0, errors: 0 });
  return forEachTenant(ctx, async () => {
    const r = await processWinback(ctx.deps, inactiveDays);
    return { processed: r.sent, errors: 0 };
  }, "pollWinback");
}

/**
 * Review request after a completed visit. Idempotent: key `review:{appointmentId}`; appointments that already
 * have the message are excluded by the query, and a concurrent duplicate is rejected by the unique key.
 */
export function pollReviews(ctx: PollContext): Promise<PollResult> {
  if (ctx.reviewsEnabled === false) return Promise.resolve({ processed: 0, errors: 0 });
  return forEachTenant(ctx, async () => {
    const res: PollResult = { processed: 0, errors: 0 };
    for (const id of await ctx.store.listCompletedWithoutReview(ctx.deps.now(), ctx.batchSize ?? 50)) {
      try {
        if (await processReviewRequest(ctx.deps, { appointmentId: id }) === "queued") res.processed++;
      } catch (err) {
        res.errors++;
        ctx.logger.error("review request failed", { appointmentId: id, errorCode: errorCode(err) });
      }
    }
    return res;
  }, "pollReviews");
}

/**
 * Due campaigns (§26.8): `scheduled -> sending`, recipients of the frozen audience -> outbox messages, `done` when drained.
 * Runs every minute, only with WORKER_CAMPAIGNS_ENABLED=true. Logs carry ids and result codes only.
 */
export function pollCampaigns(ctx: PollContext): Promise<PollResult> {
  if (ctx.campaignsEnabled === false) return Promise.resolve({ processed: 0, errors: 0 });
  return forEachTenant(ctx, async () => {
    const res: PollResult = { processed: 0, errors: 0 };
    for (const id of await ctx.deps.data.listDueCampaigns(ctx.deps.now())) {
      try {
        res.processed += (await processCampaign(ctx.deps, { campaignId: id })).queued;
      } catch (err) {
        res.errors++;
        ctx.logger.error("campaign failed", { campaignId: id, errorCode: errorCode(err) });
      }
    }
    return res;
  }, "pollCampaigns");
}
