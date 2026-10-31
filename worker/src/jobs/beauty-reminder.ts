import type { BeautyDeps, ReminderOffset, ReminderOption } from "../ports";
import { currentTenantId as currentTenant } from "../db/tenant-db";
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

/** beauty.reminder.send. @deprecated The DB poller uses processDueReminder (venue-local time, quiet hours, Telegram address); this queue-style job is kept for its unit tests. */
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

// ---------------------------------------------------------------------------------------------------------------------
// TASK-742 (§22.5): DB-driven reminder rows (offset_minutes / planned_starts_at), venue-local text, quiet hours, Telegram address.

import type { DueReminder, PgPollStore, ReminderCloseStatus } from "../adapters/pg-poll-store";
import { buildReminderText, isQuiet, nextQuietEnd } from "./reminder-time";

export const DEFAULT_MIN_LEAD_MINUTES = 15;
const MIN_GAP_MS = 60 * 60_000; // §22.4: a shifted point must still be >= 60 min before the visit

export type ReminderStore = Pick<PgPollStore,
  "loadReminderContext" | "findTelegramConversation" | "enqueueReminderMessage" | "deferReminder" | "closeReminder">;

export type ReminderOutcome =
  | { kind: "queued"; messageId: string }
  | { kind: "duplicate" | "deferred" }
  | { kind: "closed"; code: string };

/**
 * One due `beauty_reminders` row -> outbox message, checks in the order of §22.5. A legacy row (offset_minutes IS NULL)
 * takes its offset from `reminder_option` and keeps the old close statuses (cancelled / failed); new rows are `skipped`
 * with `skip_reason`. Logs ids and codes only (no text, names, phones, chat ids).
 */
export async function processDueReminder(
  deps: BeautyDeps, store: ReminderStore, r: DueReminder, opts: { minLeadMinutes?: number; buttonsEnabled?: boolean } = {},
): Promise<ReminderOutcome> {
  const now = deps.now();
  const legacy = r.offsetMinutes === null;
  const close = async (code: string, startsAt?: Date): Promise<ReminderOutcome> => {
    const status: ReminderCloseStatus = !legacy ? "skipped" : (startsAt ?? r.startsAt).getTime() <= now.getTime() ? "failed" : "cancelled";
    await store.closeReminder(r.id, status, code, now);
    await deps.log.append({ messageId: "", idempotencyKey: `reminder:${r.id}`, tenantId: currentTenant(), kind: "reminder", status: "skipped", reason: code, at: now });
    return { kind: "closed", code };
  };

  const ctx = await store.loadReminderContext(r.appointmentId);
  // 1. appointment must be pending|confirmed
  if (!ctx || !["pending", "confirmed"].includes(ctx.status)) return close("appointment_inactive", ctx?.startsAt);
  const remaining = ctx.startsAt.getTime() - now.getTime();
  // 2. the plan must still match the visit time (legacy rows: the old "visit is ~offset away" check)
  if (legacy) {
    const offsetMs = r.offset === "1h" ? 3_600_000 : r.offset === "2h" ? 7_200_000 : null;
    if (remaining <= 0) return close("too_late", ctx.startsAt);
    if (offsetMs === null || ctx.legacyOption !== r.offset || remaining > offsetMs + 60_000) return close("replanned", ctx.startsAt);
  } else if (r.plannedStartsAt && r.plannedStartsAt.getTime() !== ctx.startsAt.getTime()) {
    return close("replanned", ctx.startsAt);
  }
  // 3. enough lead time
  if (remaining < (opts.minLeadMinutes ?? DEFAULT_MIN_LEAD_MINUTES) * 60_000) return close("too_late", ctx.startsAt);
  // 4. quiet hours in the venue zone: shift to the next quiet_end if the visit is still >= 60 min away, otherwise skip
  const window = { startMinutes: ctx.quietStartMinutes, endMinutes: ctx.quietEndMinutes };
  if (isQuiet(now, ctx.timeZone, window)) {
    const shifted = nextQuietEnd(now, ctx.timeZone, ctx.quietEndMinutes);
    if (ctx.startsAt.getTime() - shifted.getTime() >= MIN_GAP_MS) {
      await store.deferReminder(r.id, shifted, now);
      await deps.log.append({ messageId: "", idempotencyKey: `reminder:${r.id}`, tenantId: currentTenant(), kind: "reminder", status: "retry", reason: "quiet_hours_deferred", at: now });
      return { kind: "deferred" };
    }
    return close("quiet_hours", ctx.startsAt);
  }
  // 5. STOP preference
  if (ctx.preferenceMethod === "none") return close("stopped", ctx.startsAt);
  // 6. address: Telegram conversation of the client in an active channel
  const conversationId = await store.findTelegramConversation(ctx.clientId, ctx.preferenceConversationId);
  if (!conversationId) return close("no_channel", ctx.startsAt);

  const text = buildReminderText({
    service: ctx.service, startsAt: ctx.startsAt, timeZone: ctx.timeZone, locationName: ctx.locationName,
    locationAddress: ctx.locationAddress, legacyOption: legacy ? r.offset : null,
  });
  const res = await store.enqueueReminderMessage(r.id, conversationId, text, now,
    opts.buttonsEnabled ? { appointmentId: r.appointmentId, expiresAt: ctx.startsAt } : undefined); // WORKER_REMINDER_BUTTONS_ENABLED (§23.7 p. 10)
  const base = { idempotencyKey: `reminder:${r.id}`, tenantId: currentTenant(), kind: "reminder", at: now } as const;
  if (res.result === "created") {
    await deps.log.append({ ...base, messageId: res.messageId, status: "enqueued" });
    return { kind: "queued", messageId: res.messageId };
  }
  await deps.log.append({ ...base, messageId: "", status: "skipped", reason: res.result === "closed" ? "reminder_closed" : "duplicate" });
  return res.result === "closed" ? { kind: "closed", code: "reminder_closed" } : { kind: "duplicate" };
}
