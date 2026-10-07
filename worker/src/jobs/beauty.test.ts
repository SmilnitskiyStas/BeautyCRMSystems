import { describe, expect, it } from "vitest";
import { makeDeps } from "../testing/fakes";
import type { AppointmentInfo, ClientInfo, LapsedClient } from "../ports";
import { processOutbox } from "./beauty-outbox";
import { processReminder, scheduleReminder } from "./beauty-reminder";
import { processCampaign } from "./beauty-campaign";
import { processWinback } from "./beauty-winback";
import { processReviewRequest } from "./beauty-review";

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
