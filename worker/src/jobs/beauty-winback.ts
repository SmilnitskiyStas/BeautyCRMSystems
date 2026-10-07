import type { BeautyDeps } from "../ports";
import { enqueueMessage } from "./beauty-common";

export const WINBACK_INACTIVE_DAYS = 60;

/** beauty.winback.run ("давно не були"): consented clients with no visit for N days. */
export async function processWinback(deps: BeautyDeps, inactiveDays = WINBACK_INACTIVE_DAYS): Promise<{ sent: number; skipped: number }> {
  let sent = 0;
  let skipped = 0;
  for (const c of await deps.data.listLapsedClients(deps.now(), inactiveDays)) {
    if (!c.marketingConsent) { skipped++; continue; }
    // Keyed by last visit: one nudge per lapse, repeat runs do not duplicate.
    const ok = await enqueueMessage(deps, {
      tenantId: c.tenantId, clientId: c.id, channel: c.channel, address: c.address,
      text: `${c.name ?? "Привіт"}, ми давно вас не бачили! Запишіться на візит.`,
      idempotencyKey: `winback:${c.id}:${c.lastVisitAt.toISOString().slice(0, 10)}`, kind: "winback",
    });
    if (ok) sent++; else skipped++;
  }
  return { sent, skipped };
}
