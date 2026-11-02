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
/** Permanent channel failure codes (§22.5); stored as `last_error` / `skip_reason` instead of free text. */
export const CHANNEL_ERROR_CODES = ["bot_blocked", "chat_not_found", "channel_inactive", "token_missing"] as const;
export type ChannelErrorCode = (typeof CHANNEL_ERROR_CODES)[number];
export const isChannelErrorCode = (v: unknown): v is ChannelErrorCode => CHANNEL_ERROR_CODES.includes(v as ChannelErrorCode);
/** Codes after which a "delivery_failed" call task is created (the client cannot be reached in Telegram). */
export const CALL_TASK_CODES: ReadonlySet<string> = new Set(["bot_blocked", "chat_not_found"]);
/** §26.10 opt-out line for texts without buttons (winback, campaigns in channels without buttons). Legal gate §26.16. */
export const UNSUBSCRIBE_NOTE = "Щоб не отримувати акції, напишіть «не надсилати акції» (або «Стоп» — тоді вимкнуться й нагадування).";
/** Opt-out line for campaign messages with the [Не надсилати акції] button. */
export const UNSUBSCRIBE_NOTE_BUTTONS = "Щоб не отримувати акції, напишіть «Стоп» або натисніть кнопку «Не надсилати акції».";
/** §26.2: at most one marketing message per client per N days. */
export const DEFAULT_CLIENT_INTERVAL_DAYS = 7;

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
