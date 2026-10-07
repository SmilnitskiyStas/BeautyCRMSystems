import type { BeautyDeps, ReminderOffset, ReminderOption } from "../ports";
import { enqueueMessage } from "./beauty-common";

export interface ReminderJobData {
  appointmentId: string;
  offset: ReminderOffset;
  /** beauty_reminders.id when driven by the DB poller: the key is then per reminder row (a reschedule creates a new row). */
  reminderId?: string;
}

const OFFSET_MS: Record<ReminderOffset, number> = { "1h": 3_600_000, "2h": 7_200_000 };

/** Called when an appointment is created/changed. "none" schedules nothing. Returns scheduled offset or null. */
export async function scheduleReminder(
  deps: BeautyDeps,
  appt: { id: string; startsAt: Date; reminderOption: ReminderOption },
): Promise<ReminderOffset | null> {
  if (appt.reminderOption === "none") return null;
  const delay = appt.startsAt.getTime() - OFFSET_MS[appt.reminderOption] - deps.now().getTime();
  if (delay < 0) return null; // too late to remind
  await deps.queue.enqueueReminder({ appointmentId: appt.id, offset: appt.reminderOption }, delay);
  return appt.reminderOption;
}

/** beauty.reminder.send */
export async function processReminder(deps: BeautyDeps, data: ReminderJobData): Promise<"queued" | "skipped"> {
  const appt = await deps.data.getAppointment(data.appointmentId);
  // Skip if gone/cancelled, or the client's choice changed (e.g. rescheduled to "none"/other offset).
  if (!appt || !["pending", "confirmed"].includes(appt.status) || appt.reminderOption !== data.offset) return "skipped";
  // Rescheduled: this job is stale if the visit is no longer ~offset away.
  const remaining = appt.startsAt.getTime() - deps.now().getTime();
  if (remaining <= 0 || remaining > OFFSET_MS[data.offset] + 60_000) return "skipped";
  const client = await deps.data.getClient(appt.clientId);
  if (!client) return "skipped";
  const when = appt.startsAt.toISOString();
  const created = await enqueueMessage(deps, {
    tenantId: appt.tenantId, clientId: client.id, channel: client.channel, address: client.address,
    text: `Нагадуємо: ваш візит ${when} (через ${data.offset === "1h" ? "1 годину" : "2 години"}).`,
    idempotencyKey: data.reminderId ? `reminder:${data.reminderId}` : `reminder:${appt.id}:${data.offset}`, kind: "reminder",
  });
  return created ? "queued" : "skipped";
}
