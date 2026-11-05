import { beforeEach, describe, expect, it, vi } from "vitest";

// Час фіксується ДО імпорту mock-клієнтів (як у mock-consistency): середа 14.10.2026, 12:00 за Києвом.
vi.hoisted(() => {
  vi.useFakeTimers({ toFake: ["Date"], now: new Date("2026-10-14T09:00:00Z") });
});

import { mockBeautyApi } from "./mock-client";

beforeEach(() => {
  process.env.NEXT_PUBLIC_MOCK_ROLE = "owner";
  window.sessionStorage.clear();
});

async function openAppointment(master = "m") {
  const week = await mockBeautyApi.getCalendarWeek(master);
  const a = week.appointments.find((x) => x.kind !== "break" && (x.status === "pending" || x.status === "confirmed") && x.startsAt > "2026-10-14T12:00");
  expect(a).toBeDefined();
  return a!;
}

const statusOf = async (id: string, master = "m") => (await mockBeautyApi.getCalendarWeek(master, true)).appointments.find((x) => x.id === id)?.status;

describe("TASK-775: зміна статусу запису в mock (PATCH status)", () => {
  it("confirmed / no_show змінюють статус у календарі; закритий запис далі не змінюється (409 appointment_closed)", async () => {
    const a = await openAppointment();
    await mockBeautyApi.setAppointmentStatus(a.id, "confirmed");
    expect(await statusOf(a.id)).toBe("confirmed");
    await mockBeautyApi.setAppointmentStatus(a.id, "no_show");
    expect(await statusOf(a.id)).toBe("no_show");
    await expect(mockBeautyApi.setAppointmentStatus(a.id, "completed")).rejects.toMatchObject({ status: 409, code: "appointment_closed" });
    await expect(mockBeautyApi.moveAppointment(a.id, "2026-10-14T16:00")).rejects.toMatchObject({ status: 409, code: "appointment_closed" });
  });

  it("completed: статус стає «Завершено», повторне завершення - 409 appointment_closed, а запис потрапляє в аналітику як завершений", async () => {
    const q = { from: "2026-10-01", to: "2026-10-31", compare: false };
    const before = (await mockBeautyApi.getNetworkAnalytics(q)).totals.completed;
    const a = await openAppointment("o");
    await mockBeautyApi.setAppointmentStatus(a.id, "completed");
    expect(await statusOf(a.id, "o")).toBe("completed");
    await expect(mockBeautyApi.setAppointmentStatus(a.id, "completed")).rejects.toMatchObject({ code: "appointment_closed" });
    expect((await mockBeautyApi.getNetworkAnalytics(q)).totals.completed).toBe(before + 1);
  });

  it("невідомий запис - 404; specialist не змінює чужий запис (404, як на backend)", async () => {
    await expect(mockBeautyApi.setAppointmentStatus("no-such-id", "confirmed")).rejects.toMatchObject({ status: 404 });
    const foreign = await openAppointment("o");
    process.env.NEXT_PUBLIC_MOCK_ROLE = "specialist";
    await expect(mockBeautyApi.setAppointmentStatus(foreign.id, "confirmed")).rejects.toMatchObject({ status: 404 });
  });
});
