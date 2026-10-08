import type { BeautyDeps } from "../ports";
import { DEFAULT_MARKETING_DAILY_LIMIT, MARKETING_KINDS, OUTBOX_MAX_ATTEMPTS } from "./beauty-common";

export interface OutboxJobData { messageId: string }

/**
 * beauty.outbox.send. `attemptsMade` = attempts already made before this run (BullMQ job.attemptsMade).
 * Throws on send failure so BullMQ retries (attempts: 3, exponential backoff).
 */
export async function processOutbox(deps: BeautyDeps, data: OutboxJobData, attemptsMade = 0): Promise<"sent" | "skipped" | "deferred"> {
  const msg = await deps.data.getMessage(data.messageId);
  if (!msg) throw new Error(`Outbox message not found: ${data.messageId}`);
  const attempt = attemptsMade + 1;
  const base = { messageId: msg.id, idempotencyKey: msg.idempotencyKey, tenantId: msg.tenantId, kind: msg.kind, attempt } as const;

  if (msg.status === "sent" || !(await deps.idempotency.claim(msg.idempotencyKey))) {
    await deps.log.append({ ...base, status: "skipped", reason: "already sent or in progress", at: deps.now() });
    return "skipped";
  }
  // Re-check under the claim: another worker may have finished between our read and the claim.
  if ((await deps.data.getMessage(msg.id))?.status === "sent") {
    await deps.idempotency.complete(msg.idempotencyKey);
    await deps.log.append({ ...base, status: "skipped", reason: "already sent", at: deps.now() });
    return "skipped";
  }
  if (MARKETING_KINDS.has(msg.kind)) {
    // Last check right before delivery: the client may have unsubscribed after the audience was picked.
    const client = msg.clientId ? await deps.data.getClient(msg.clientId) : null;
    if (!client || !client.marketingConsent) {
      await deps.idempotency.release(msg.idempotencyKey);
      await deps.data.recordSendFailure(msg.id, attempt, "recipient unsubscribed or no marketing consent", true);
      await deps.log.append({ ...base, status: "skipped", reason: "unsubscribed", at: deps.now() });
      return "skipped";
    }
    const limit = deps.marketingDailyLimit ?? DEFAULT_MARKETING_DAILY_LIMIT;
    const since = new Date(deps.now().getTime() - 24 * 3_600_000);
    if (await deps.data.countMarketingSentSince(since) >= limit) {
      // Stays pending: picked up again once older sends leave the 24 h window.
      await deps.idempotency.release(msg.idempotencyKey);
      await deps.log.append({ ...base, status: "skipped", reason: `daily marketing limit ${limit} reached, deferred`, at: deps.now() });
      return "deferred";
    }
  }
  try {
    await deps.sender.send(msg);
  } catch (err) {
    await deps.idempotency.release(msg.idempotencyKey);
    // A permanent channel error (bad token, closed 24h window) will not heal by retrying.
    const final = attempt >= OUTBOX_MAX_ATTEMPTS || (err as { permanent?: boolean } | null)?.permanent === true;
    const error = err instanceof Error ? err.message : String(err);
    await deps.data.recordSendFailure(msg.id, attempt, error, final);
    await deps.log.append({ ...base, status: final ? "failed" : "retry", error, at: deps.now() });
    throw err; // never swallow: BullMQ retries / marks failed
  }
  try {
    await deps.data.setMessageStatus(msg.id, "sent");
    await deps.idempotency.complete(msg.idempotencyKey);
  } catch (err) {
    await deps.idempotency.release(msg.idempotencyKey); // delivered but not recorded: surface it, never hold the claim
    throw err;
  }
  await deps.log.append({ ...base, status: "sent", at: deps.now() });
  return "sent";
}
