import { beforeEach, describe, expect, it, vi } from "vitest";

// Час фіксується ДО імпорту mock-клієнта: початковий стан будується від «сьогодні». Середа 14.10.2026, 12:00 за Києвом.
vi.hoisted(() => {
  vi.useFakeTimers({ toFake: ["Date"], now: new Date("2026-10-14T09:00:00Z") });
});

import { mockBeautyApi } from "./mock-client";

beforeEach(() => {
  process.env.NEXT_PUBLIC_MOCK_ROLE = "owner";
});

/** Усі записи поточного тижня: майстри × заклади (календар показує один заклад за раз). */
async function weekAppointments() {
  const out = new Map<string, Awaited<ReturnType<typeof mockBeautyApi.getCalendarWeek>>["appointments"][number]>();
  for (const m of ["m", "a", "o", "im"]) {
    for (const loc of ["c", "p", "k"]) for (const a of (await mockBeautyApi.getCalendarWeek(m, false, loc)).appointments) out.set(a.id, a);
  }
  return [...out.values()];
}

describe("mock-клієнт: нагадування в записах календаря й узгодженість з вподобаннями", () => {
  it("owner бачить reminders з точками у майбутніх записах; specialist - ні", async () => {
    const week = await mockBeautyApi.getCalendarWeek("m");
    const upcoming = week.appointments.filter((a) => a.startsAt > "2026-10-14T12:00" && a.status !== "cancelled" && a.status !== "completed");
    expect(upcoming.length).toBeGreaterThan(0);
    const withPlan = upcoming.filter((a) => a.reminders && a.reminders.method !== "none");
    expect(withPlan.length).toBeGreaterThan(0);
    for (const a of withPlan) {
      expect(a.reminders!.offsetsMinutes.length).toBeGreaterThan(0);
      for (const p of a.reminders!.points ?? []) expect(["message", "call"]).toContain(p.kind);
    }
    const past = week.appointments.filter((a) => a.startsAt <= "2026-10-14T12:00");
    expect(past.every((a) => a.reminders === undefined)).toBe(true);

    process.env.NEXT_PUBLIC_MOCK_ROLE = "specialist";
    const mine = await mockBeautyApi.getCalendarWeek("m");
    expect(mine.appointments.every((a) => a.reminders === undefined)).toBe(true);
  });

  it("зміна вподобання клієнта через API одразу змінює точки його майбутніх записів і задачі дзвінків", async () => {
    const appt = (await weekAppointments()).find((a) => a.clientId === "sm" && a.startsAt > "2026-10-14T12:00" && a.status !== "cancelled")!;
    expect(appt.reminders?.method).toBe("none");
    const saved = await mockBeautyApi.setContactPreferences("sm", { method: "call", offsetsMinutes: [1440] });
    expect(saved.replannedAppointments).toBeGreaterThan(0);
    const after = (await weekAppointments()).find((a) => a.id === appt.id)!;
    expect(after.reminders).toMatchObject({ method: "call", offsetsMinutes: [1440] });
    const tasks = await mockBeautyApi.getCallTasks({ day: "2026-10-14", status: "all" });
    const all = [
      ...tasks.items,
      ...(await mockBeautyApi.getCallTasks({ day: "2026-10-15", status: "all" })).items,
      ...(await mockBeautyApi.getCallTasks({ day: "2026-10-16", status: "all" })).items,
    ];
    expect(all.some((t) => t.client.id === "sm")).toBe(true);
  }, 30_000);

  it("inbox: передача від ШІ -> відповідь -> «Повернути ШІ» через публічний mock API", async () => {
    const before = await mockBeautyApi.getConversations({ filter: "needs_reply" });
    expect(before.meta.needsReplyCount).toBe(2);
    await mockBeautyApi.replyToConversation("cv2", "Так, нагадаємо вам у Telegram.", "mock-key-0001");
    expect((await mockBeautyApi.getConversations({ filter: "needs_reply" })).meta.needsReplyCount).toBe(1);
    await mockBeautyApi.resumeConversationAi("cv2");
    const c = (await mockBeautyApi.getConversation("cv2")).conversation;
    expect(c.aiState).toBe("active");
    process.env.NEXT_PUBLIC_MOCK_ROLE = "specialist";
    await expect(mockBeautyApi.getConversations({ filter: "all" })).rejects.toMatchObject({ status: 403, code: "forbidden_role" });
  });
});
