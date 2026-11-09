import { describe, expect, it } from "vitest";
import { makeDeps } from "../testing/fakes";
import type { AppointmentInfo, ClientInfo, LapsedClient } from "../ports";
import { processOutbox } from "./beauty-outbox";
import { processReminder, scheduleReminder } from "./beauty-reminder";
import { processConfirmation } from "./beauty-confirmation";
import { processCampaign } from "./beauty-campaign";
import { processWinback } from "./beauty-winback";
import { processReviewRequest } from "./beauty-review";
import { UNSUBSCRIBE_NOTE } from "./beauty-common";

const client: ClientInfo = { id: "c1", tenantId: "t1", channel: "telegram", address: "42", marketingConsent: true };
const noConsent: ClientInfo = { ...client, id: "c2", marketingConsent: false };
const NOW = new Date("2026-01-10T10:00:00Z");
const appt = (over: Partial<AppointmentInfo> = {}): AppointmentInfo => ({
  id: "a1", tenantId: "t1", clientId: "c1", startsAt: new Date("2026-01-10T12:00:00Z"),
  status: "confirmed", reminderOption: "2h", ...over,
});

describe("reminders", () => {
  it("scheduleReminder_schedules-2h-before_when-option-2h", async () => {
    const t = makeDeps({ now: NOW });
    expect(await scheduleReminder(t.deps, appt())).toBe("2h");
    expect(t.reminders[0]).toMatchObject({ offset: "2h", delayMs: 0 });
  });
  it("scheduleReminder_schedules-1h-before_when-option-1h", async () => {
    const t = makeDeps({ now: NOW });
    await scheduleReminder(t.deps, appt({ reminderOption: "1h" }));
    expect(t.reminders[0]).toMatchObject({ offset: "1h", delayMs: 3_600_000 });
  });
  it("scheduleReminder_schedules-24h-before_when-option-24h", async () => {
    const t = makeDeps({ now: NOW });
    expect(await scheduleReminder(t.deps, appt({ startsAt: new Date("2026-01-11T14:00:00Z"), reminderOption: "24h" }))).toBe("24h");
    expect(t.reminders[0]).toMatchObject({ offset: "24h", delayMs: 4 * 3_600_000 });
  });
  it("scheduleReminder_skips-24h_when-too-late", async () => {
    const t = makeDeps({ now: NOW });
    expect(await scheduleReminder(t.deps, appt({ startsAt: new Date("2026-01-11T09:00:00Z"), reminderOption: "24h" }))).toBeNull();
    expect(t.reminders).toHaveLength(0);
  });
  it("processReminder_queues-24h-text_when-visit-is-24h-away", async () => {
    const t = makeDeps({ now: NOW, appointments: [appt({ startsAt: new Date("2026-01-11T10:00:00Z"), reminderOption: "24h" })], clients: [client] });
    expect(await processReminder(t.deps, { appointmentId: "a1", offset: "24h" })).toBe("queued");
    expect(t.messages.get("m1")!.text).toContain("завтра");
  });
  it("processReminder_skips-stale-24h-job_when-visit-moved-further-or-option-changed", async () => {
    const t = makeDeps({ now: NOW, appointments: [
      appt({ startsAt: new Date("2026-01-14T10:00:00Z"), reminderOption: "24h" }),
      appt({ id: "a2", startsAt: new Date("2026-01-11T10:00:00Z"), reminderOption: "2h" }),
    ], clients: [client] });
    expect(await processReminder(t.deps, { appointmentId: "a1", offset: "24h" })).toBe("skipped");
    expect(await processReminder(t.deps, { appointmentId: "a2", offset: "24h" })).toBe("skipped");
    expect(t.outbox).toHaveLength(0);
  });
  it("scheduleReminder_sends-nothing_when-option-none", async () => {
    const t = makeDeps({ now: NOW });
    expect(await scheduleReminder(t.deps, appt({ reminderOption: "none" }))).toBeNull();
    expect(t.reminders).toHaveLength(0);
  });
  it("processReminder_queues-once_when-run-twice", async () => {
    const t = makeDeps({ now: NOW, appointments: [appt()], clients: [client] });
    expect(await processReminder(t.deps, { appointmentId: "a1", offset: "2h" })).toBe("queued");
    expect(await processReminder(t.deps, { appointmentId: "a1", offset: "2h" })).toBe("skipped");
    expect(t.outbox).toHaveLength(1);
  });
  it("processReminder_skips_when-cancelled-or-option-changed-or-not-found", async () => {
    const t = makeDeps({ now: NOW, appointments: [appt({ status: "cancelled" }), appt({ id: "a2", reminderOption: "none" })], clients: [client] });
    expect(await processReminder(t.deps, { appointmentId: "a1", offset: "2h" })).toBe("skipped");
    expect(await processReminder(t.deps, { appointmentId: "a2", offset: "2h" })).toBe("skipped");
    expect(await processReminder(t.deps, { appointmentId: "zz", offset: "2h" })).toBe("skipped");
    expect(t.outbox).toHaveLength(0);
  });
});

describe("outbox", () => {
  async function queued(t: ReturnType<typeof makeDeps>) {
    await processReminder(t.deps, { appointmentId: "a1", offset: "2h" });
    return t.outbox[0]!;
  }
  it("processOutbox_sends-once_when-rerun", async () => {
    const t = makeDeps({ now: NOW, appointments: [appt()], clients: [client] });
    const messageId = await queued(t);
    expect(await processOutbox(t.deps, { messageId })).toBe("sent");
    expect(await processOutbox(t.deps, { messageId })).toBe("skipped");
    expect(t.sent).toHaveLength(1);
    expect(t.logs.some((l) => l.status === "sent")).toBe(true);
  });
  it("processOutbox_succeeds-on-3rd-attempt_after-two-failures", async () => {
    const t = makeDeps({ now: NOW, appointments: [appt()], clients: [client], sendFailures: 2 });
    const messageId = await queued(t);
    await expect(processOutbox(t.deps, { messageId }, 0)).rejects.toThrow("channel down");
    await expect(processOutbox(t.deps, { messageId }, 1)).rejects.toThrow("channel down");
    expect(await processOutbox(t.deps, { messageId }, 2)).toBe("sent");
    expect(t.sent).toHaveLength(1);
    expect(t.logs.filter((l) => l.status === "retry")).toHaveLength(2);
  });
  it("processOutbox_marks-failed_after-3-failed-attempts", async () => {
    const t = makeDeps({ now: NOW, appointments: [appt()], clients: [client], sendFailures: 99 });
    const messageId = await queued(t);
    for (const n of [0, 1, 2]) await expect(processOutbox(t.deps, { messageId }, n)).rejects.toThrow();
    expect(t.messages.get(messageId)!.status).toBe("failed");
    expect(t.logs.at(-1)).toMatchObject({ status: "failed", attempt: 3 });
  });
  it("processOutbox_throws_when-message-not-found", async () => {
    const t = makeDeps();
    await expect(processOutbox(t.deps, { messageId: "nope" })).rejects.toThrow("not found");
  });
});

describe("campaign", () => {
  const campaign = { id: "k1", tenantId: "t1", text: "Акція", allowedFromHour: 9, allowedToHour: 21, utcOffsetMinutes: 0 };
  it("processCampaign_sends-only-consented_and-is-idempotent", async () => {
    const t = makeDeps({ now: NOW, campaign, audience: [client, noConsent] });
    expect(await processCampaign(t.deps, { campaignId: "k1" })).toEqual({ sent: 1, skipped: 1 });
    expect(await processCampaign(t.deps, { campaignId: "k1" })).toEqual({ sent: 0, skipped: 2 });
    expect(t.outbox).toHaveLength(1);
  });
  it("processCampaign_defers_when-outside-allowed-hours", async () => {
    const t = makeDeps({ now: new Date("2026-01-10T22:00:00Z"), campaign, audience: [client] });
    const r = await processCampaign(t.deps, { campaignId: "k1" });
    expect(r.deferredMs).toBe(11 * 3_600_000);
    expect(t.outbox).toHaveLength(0);
    expect(t.campaigns).toEqual([{ id: "k1", delayMs: 11 * 3_600_000 }]);
  });
  it("processCampaign_throws_when-not-found", async () => {
    await expect(processCampaign(makeDeps().deps, { campaignId: "x" })).rejects.toThrow("not found");
  });
});

describe("winback and review", () => {
  const lapsed: LapsedClient = { ...client, lastVisitAt: new Date("2025-10-01T00:00:00Z") };
  it("processWinback_skips-no-consent_and-does-not-duplicate", async () => {
    const t = makeDeps({ now: NOW, lapsed: [lapsed, { ...noConsent, lastVisitAt: lapsed.lastVisitAt }] });
    expect(await processWinback(t.deps)).toEqual({ sent: 1, skipped: 1 });
    expect(await processWinback(t.deps)).toEqual({ sent: 0, skipped: 2 });
    expect(t.outbox).toHaveLength(1);
  });
  it("processReviewRequest_queues-once_only-for-completed", async () => {
    const t = makeDeps({ now: NOW, appointments: [appt({ status: "completed" }), appt({ id: "a2" })], clients: [client] });
    expect(await processReviewRequest(t.deps, { appointmentId: "a1" })).toBe("queued");
    expect(await processReviewRequest(t.deps, { appointmentId: "a1" })).toBe("skipped");
    expect(await processReviewRequest(t.deps, { appointmentId: "a2" })).toBe("skipped");
    expect(t.outbox).toHaveLength(1);
  });
});

describe("marketing safeguards (unsubscribe, daily limit)", () => {
  const lapsed: LapsedClient = { ...client, lastVisitAt: new Date("2025-10-01T00:00:00Z") };
  const campaign = { id: "k1", tenantId: "t1", text: "Акція", allowedFromHour: 9, allowedToHour: 21, utcOffsetMinutes: 0 };

  it("winback_and_campaign_texts_include-unsubscribe-instruction", async () => {
    const t = makeDeps({ now: NOW, lapsed: [lapsed], campaign, audience: [client] });
    await processWinback(t.deps);
    await processCampaign(t.deps, { campaignId: "k1" });
    const texts = [...t.messages.values()].map((m) => m.text);
    expect(texts).toHaveLength(2);
    for (const x of texts) expect(x).toContain(UNSUBSCRIBE_NOTE);
    expect(UNSUBSCRIBE_NOTE).toContain("STOP");
  });

  it("reminder_text_has-no-unsubscribe-line", async () => {
    const t = makeDeps({ now: NOW, appointments: [appt()], clients: [client] });
    await processReminder(t.deps, { appointmentId: "a1", offset: "2h" });
    expect([...t.messages.values()][0]!.text).not.toContain(UNSUBSCRIBE_NOTE);
  });

  it("processOutbox_skips-and-fails-marketing_when-client-unsubscribed-after-enqueue", async () => {
    const t = makeDeps({ now: NOW, lapsed: [lapsed], clients: [client] });
    await processWinback(t.deps);
    const messageId = t.outbox[0]!;
    // client unsubscribes between audience selection and delivery
    t.deps.data.getClient = async () => ({ ...client, marketingConsent: false });
    expect(await processOutbox(t.deps, { messageId })).toBe("skipped");
    expect(t.sent).toHaveLength(0);
    expect(t.messages.get(messageId)!.status).toBe("failed");
    expect(t.logs.at(-1)).toMatchObject({ status: "skipped", reason: "unsubscribed" });
  });

  it("processOutbox_still-sends-reminder_to-unsubscribed-client", async () => {
    const t = makeDeps({ now: NOW, appointments: [appt()], clients: [{ ...client, marketingConsent: false }] });
    await processReminder(t.deps, { appointmentId: "a1", offset: "2h" });
    expect(await processOutbox(t.deps, { messageId: t.outbox[0]! })).toBe("sent");
  });

  it("processOutbox_defers-marketing_when-daily-limit-reached_but-not-reminders", async () => {
    const l2: LapsedClient = { ...lapsed, id: "c3" };
    const t = makeDeps({ now: NOW, lapsed: [lapsed, l2], clients: [client, { ...client, id: "c3" }], appointments: [appt()], marketingDailyLimit: 1 });
    await processWinback(t.deps);
    expect(await processOutbox(t.deps, { messageId: t.outbox[0]! })).toBe("sent");
    expect(await processOutbox(t.deps, { messageId: t.outbox[1]! })).toBe("deferred");
    expect(t.sent).toHaveLength(1);
    expect(t.messages.get(t.outbox[1]!)!.status).toBe("queued"); // not failed: retried later
    expect(t.logs.at(-1)).toMatchObject({ status: "skipped" });
    expect(t.logs.at(-1)!.reason).toContain("daily marketing limit");
    // transactional reminder is not limited
    await processReminder(t.deps, { appointmentId: "a1", offset: "2h" });
    expect(await processOutbox(t.deps, { messageId: t.outbox[2]! })).toBe("sent");
  });
});

describe("confirmation", () => {
  const emailClient: ClientInfo = { id: "c1", tenantId: "t1", channel: "email", address: "anna@example.com", marketingConsent: false, name: "Анна" };
  const details = { locationName: "Салон Центр", locationAddress: "вул. Хрещатик, 1", timezone: "Europe/Kyiv", serviceName: "Манікюр", specialistName: "Олена" };
  const future = appt({ startsAt: new Date("2026-01-12T12:30:00Z"), reminderOption: "none" });

  it("processConfirmation_queues-uk-text-in-location-timezone_without-marketing-consent", async () => {
    const t = makeDeps({ now: NOW, appointments: [future], clients: [emailClient], details });
    expect(await processConfirmation(t.deps, { appointmentId: "a1" })).toBe("queued");
    const m = t.messages.get("m1")!;
    expect(m).toMatchObject({ kind: "confirmation", channel: "email", address: "anna@example.com", idempotencyKey: "confirmation:a1" });
    for (const part of ["Салон Центр", "вул. Хрещатик, 1", "Манікюр", "Олена", "14:30", "12 січня 2026"]) expect(m.text).toContain(part);
    expect(m.text).not.toContain(UNSUBSCRIBE_NOTE);
  });
  it("processConfirmation_is-idempotent", async () => {
    const t = makeDeps({ now: NOW, appointments: [future], clients: [emailClient], details });
    await processConfirmation(t.deps, { appointmentId: "a1" });
    expect(await processConfirmation(t.deps, { appointmentId: "a1" })).toBe("skipped");
    expect(t.outbox).toHaveLength(1);
  });
  it("processConfirmation_skips_when-cancelled-past-no-address-or-no-details", async () => {
    const t = makeDeps({ now: NOW, details, clients: [emailClient, { ...emailClient, id: "c3", address: "" }], appointments: [
      appt({ id: "x1", status: "cancelled" }), appt({ id: "x2", startsAt: new Date("2026-01-09T10:00:00Z") }), appt({ id: "x3", clientId: "c3" }),
    ] });
    for (const id of ["x1", "x2", "x3", "zz"]) expect(await processConfirmation(t.deps, { appointmentId: id })).toBe("skipped");
    const t2 = makeDeps({ now: NOW, appointments: [future], clients: [emailClient], details: null });
    expect(await processConfirmation(t2.deps, { appointmentId: "a1" })).toBe("skipped");
    expect(t.outbox).toHaveLength(0);
  });
});
