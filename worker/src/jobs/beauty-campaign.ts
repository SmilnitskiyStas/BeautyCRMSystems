import type { BeautyDeps, CampaignInfo } from "../ports";
import { enqueueMessage, withUnsubscribe } from "./beauty-common";

export interface CampaignJobData { campaignId: string }

/** ms until the next allowed window opens; 0 if now is allowed. */
export function delayUntilAllowed(c: Pick<CampaignInfo, "allowedFromHour" | "allowedToHour" | "utcOffsetMinutes">, now: Date): number {
  const local = new Date(now.getTime() + c.utcOffsetMinutes * 60_000);
  const h = local.getUTCHours() + local.getUTCMinutes() / 60;
  if (h >= c.allowedFromHour && h < c.allowedToHour) return 0;
  const hoursToOpen = h < c.allowedFromHour ? c.allowedFromHour - h : 24 - h + c.allowedFromHour;
  return Math.ceil(hoursToOpen * 3_600_000);
}

/** beauty.campaign.run: only clients with marketing consent, only in allowed hours. */
export async function processCampaign(deps: BeautyDeps, data: CampaignJobData): Promise<{ sent: number; skipped: number; deferredMs?: number }> {
  const campaign = await deps.data.getCampaign(data.campaignId);
  if (!campaign) throw new Error(`Campaign not found: ${data.campaignId}`);
  const wait = delayUntilAllowed(campaign, deps.now());
  if (wait > 0) {
    await deps.queue.enqueueCampaign(campaign.id, wait);
    return { sent: 0, skipped: 0, deferredMs: wait };
  }
  let sent = 0;
  let skipped = 0;
  for (const c of await deps.data.listCampaignAudience(campaign.id)) {
    if (!c.marketingConsent) { skipped++; continue; }
    const ok = await enqueueMessage(deps, {
      tenantId: campaign.tenantId, clientId: c.id, channel: c.channel, address: c.address,
      text: withUnsubscribe(campaign.text), idempotencyKey: `campaign:${campaign.id}:${c.id}`, kind: "campaign",
    });
    if (ok) sent++; else skipped++;
  }
  return { sent, skipped };
}
