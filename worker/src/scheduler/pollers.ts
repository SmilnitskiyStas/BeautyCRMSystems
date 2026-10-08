import type { PgPollStore } from "../adapters/pg-poll-store";
import type { Logger } from "../adapters/pg-support";
import { withTenant } from "../db/tenant-db";
import type { TenantDirectory } from "../db/tenants";
import { processOutbox } from "../jobs/beauty-outbox";
import { processReviewRequest } from "../jobs/beauty-review";
import { processReminder } from "../jobs/beauty-reminder";
import { processWinback } from "../jobs/beauty-winback";
import type { BeautyDeps } from "../ports";

export interface PollContext {
  deps: BeautyDeps;
  store: PgPollStore;
  tenants: TenantDirectory;
  logger: Logger;
  batchSize?: number;
  backoffMs?: number;
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
      ctx.logger.error(`${what} failed for tenant`, { tenantId, error: String(err) });
    }
  }
  return total;
}

/** Due beauty_reminders -> outbox messages (idempotent per reminder row). Runs every few minutes. */
export function pollReminders(ctx: PollContext): Promise<PollResult> {
  return forEachTenant(ctx, async () => {
    const now = ctx.deps.now();
    const res: PollResult = { processed: 0, errors: 0 };
    for (const r of await ctx.store.listDueReminders(now, ctx.batchSize ?? 100)) {
      try {
        const outcome = r.offset
          ? await processReminder(ctx.deps, { appointmentId: r.appointmentId, offset: r.offset, reminderId: r.id })
          : "skipped";
        if (outcome === "skipped") {
          const missed = r.startsAt.getTime() <= now.getTime();
          await ctx.store.closeReminder(r.id, missed ? "failed" : "cancelled",
            missed ? "visit already started" : "appointment inactive, reminder option changed or client unreachable", now);
        }
        res.processed++;
      } catch (err) {
        res.errors++;
        ctx.logger.error("reminder failed", { reminderId: r.id, error: String(err) });
      }
    }
    return res;
  }, "pollReminders");
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
        ctx.logger.error("outbox send failed", { messageId: m.id, attempt: m.attempts + 1, error: String(err) });
      }
    }
    return res;
  }, "pollOutbox");
}

/** "Давно не були": daily; keys are per lapse, so extra runs never duplicate. */
export function pollWinback(ctx: PollContext, inactiveDays?: number): Promise<PollResult> {
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
  return forEachTenant(ctx, async () => {
    const res: PollResult = { processed: 0, errors: 0 };
    for (const id of await ctx.store.listCompletedWithoutReview(ctx.deps.now(), ctx.batchSize ?? 50)) {
      try {
        if (await processReviewRequest(ctx.deps, { appointmentId: id }) === "queued") res.processed++;
      } catch (err) {
        res.errors++;
        ctx.logger.error("review request failed", { appointmentId: id, error: String(err) });
      }
    }
    return res;
  }, "pollReviews");
}
