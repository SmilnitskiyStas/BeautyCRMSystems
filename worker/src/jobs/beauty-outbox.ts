import type { BeautyDeps } from "../ports";
import { OUTBOX_MAX_ATTEMPTS } from "./beauty-common";

export interface OutboxJobData { messageId: string }

/**
 * beauty.outbox.send. `attemptsMade` = attempts already made before this run (BullMQ job.attemptsMade).
 * Throws on send failure so BullMQ retries (attempts: 3, exponential backoff).
 */
export async function processOutbox(deps: BeautyDeps, data: OutboxJobData, attemptsMade = 0): Promise<"sent" | "skipped"> {
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
