import type { BeautyDeps, OutboundMessage } from "../ports";

export const QUEUES = {
  reminder: "beauty.reminder.send",
  outbox: "beauty.outbox.send",
  campaign: "beauty.campaign.run",
  winback: "beauty.winback.run",
  review: "beauty.review.request",
} as const;

export const OUTBOX_MAX_ATTEMPTS = 3;
export const OUTBOX_BACKOFF_MS = 5_000;
/** Marketing messages per tenant per rolling 24 h (override: WORKER_MARKETING_DAILY_LIMIT). */
export const DEFAULT_MARKETING_DAILY_LIMIT = 200;
export const MARKETING_KINDS: ReadonlySet<OutboundMessage["kind"]> = new Set(["campaign", "winback"]);
export const UNSUBSCRIBE_NOTE = "Щоб не отримувати повідомлення, надішліть STOP";

/** Marketing texts must carry the opt-out instruction. */
export const withUnsubscribe = (text: string): string => `${text}

${UNSUBSCRIBE_NOTE}`;

/** Create the message once per key and enqueue it into the outbox. Returns false if it already existed. */
export async function enqueueMessage(deps: BeautyDeps, msg: OutboundMessage): Promise<boolean> {
  const { message, created } = await deps.data.upsertMessage(msg);
  const base = { messageId: message.id, idempotencyKey: msg.idempotencyKey, tenantId: msg.tenantId, kind: msg.kind, at: deps.now() };
  if (!created) {
    await deps.log.append({ ...base, status: "skipped", reason: "duplicate" });
    return false;
  }
  await deps.queue.enqueueOutbox(message.id);
  await deps.log.append({ ...base, status: "enqueued" });
  return true;
}
