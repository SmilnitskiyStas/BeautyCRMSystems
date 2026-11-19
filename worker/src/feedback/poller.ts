import type { Logger } from "../adapters/pg-support";
import { withTenant } from "../db/tenant-db";
import type { TenantDirectory } from "../db/tenants";
import { deliver, resolve, type FeedbackConfig } from "./delivery";
import type { FeedbackStore } from "./feedback-store";

export interface FeedbackPollContext {
  store: FeedbackStore;
  tenants: TenantDirectory;
  logger: Logger;
  config: FeedbackConfig;
  fetch?: typeof fetch;
  now?: () => Date;
  rand?: () => number;
  nonce?: () => string;
  batchSize?: number;
}

/** Delivers due feedback to ShelfGuard. Logs carry ids, status codes and attempt counts only (never message/contact/secret). */
export async function pollFeedback(ctx: FeedbackPollContext): Promise<{ processed: number; errors: number }> {
  const now = ctx.now ?? (() => new Date());
  const total = { processed: 0, errors: 0 };
  for (const tenantId of await ctx.tenants.listTenantIds()) {
    try {
      await withTenant(tenantId, async () => {
        for (const row of await ctx.store.claimDue(now(), ctx.batchSize ?? 20)) {
          try {
            const outcome = await deliver(ctx.config, row, { fetch: ctx.fetch ?? fetch, now, nonce: ctx.nonce });
            const res = resolve(outcome, row.attempts, now(), ctx.rand);
            await ctx.store.record(row.id, res, now());
            if (res.status === "delivered") total.processed++;
            else {
              total.errors++;
              const fields = { feedbackId: row.id, attempt: row.attempts, error: res.error };
              if (res.status === "failed") ctx.logger.error("feedback delivery failed permanently", fields);
              else ctx.logger.info("feedback delivery will retry", fields);
            }
          } catch (err) {
            total.errors++; // lease expires, the row is retried
            ctx.logger.error("feedback delivery error", { feedbackId: row.id, errorCode: (err as { code?: string })?.code ?? "error" });
          }
        }
      });
    } catch (err) {
      total.errors++;
      ctx.logger.error("pollFeedback failed for tenant", { tenantId, errorCode: (err as { code?: string })?.code ?? "error" });
    }
  }
  return total;
}
