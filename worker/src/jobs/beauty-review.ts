import type { BeautyDeps } from "../ports";
import { enqueueMessage } from "./beauty-common";

export interface ReviewJobData { appointmentId: string }

/** beauty.review.request: after a completed visit. */
export async function processReviewRequest(deps: BeautyDeps, data: ReviewJobData): Promise<"queued" | "skipped"> {
  const appt = await deps.data.getAppointment(data.appointmentId);
  if (!appt || appt.status !== "completed") return "skipped";
  const client = await deps.data.getClient(appt.clientId);
  if (!client) return "skipped";
  const created = await enqueueMessage(deps, {
    tenantId: appt.tenantId, clientId: client.id, channel: client.channel, address: client.address,
    text: "Дякуємо за візит! Залиште, будь ласка, відгук.",
    idempotencyKey: `review:${appt.id}`, kind: "review",
  });
  return created ? "queued" : "skipped";
}
