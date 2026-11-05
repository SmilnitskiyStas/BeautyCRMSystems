import type { BeautyDeps } from "../ports";
import { withUnsubscribe } from "./beauty-common";
import { campaignOptions, greetingName } from "./beauty-campaign";

export const WINBACK_INACTIVE_DAYS = 60;

/**
 * beauty.winback.run ("давно не були"): consented Telegram clients with no visit for N days (M6).
 * Window, frequency (1/7 days) and the daily limit are checked inside `enqueueWinbackMessage` under one lock; a deferral
 * (window / limit / module off) stops the pass for the tenant, the next run picks the rest up. Name = first word, sanitized.
 */
export async function processWinback(deps: BeautyDeps, inactiveDays = WINBACK_INACTIVE_DAYS): Promise<{ sent: number; skipped: number }> {
  let sent = 0;
  let skipped = 0;
  const opts = campaignOptions(deps);
  for (const c of await deps.data.listLapsedClients(deps.now(), inactiveDays, deps.marketingClientIntervalDays)) {
    if (!c.marketingConsent || c.optedOut || c.channel !== "telegram") { skipped++; continue; }
    // Keyed by last visit: one nudge per lapse, repeat runs do not duplicate.
    const r = await deps.data.enqueueWinbackMessage({
      tenantId: c.tenantId, clientId: c.id, channel: c.channel, address: c.address,
      text: withUnsubscribe(`${greetingName(c.name)}, ми давно вас не бачили! Запишіться на візит.`),
      idempotencyKey: `winback:${c.id}:${c.lastVisitAt.toISOString().slice(0, 10)}`, kind: "winback",
    }, deps.now(), opts);
    if (r.result === "queued") {
      await deps.queue.enqueueOutbox(r.message.id);
      await deps.log.append({ messageId: r.message.id, idempotencyKey: r.message.idempotencyKey, tenantId: r.message.tenantId, kind: "winback", at: deps.now(), status: "enqueued" });
      sent++;
    } else if (r.result === "deferred_window" || r.result === "deferred_limit" || r.result === "deferred_disabled") {
      break;
    } else skipped++;
  }
  return { sent, skipped };
}
