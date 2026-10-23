import { beforeEach, describe, expect, it, vi } from "vitest";
import { BeautyApiError } from "@/features/beauty-auth/errors";

// Час фіксується ДО імпорту mock-клієнта: його початковий стан (закриття, відсутності) будується від «сьогодні».
vi.hoisted(() => {
  vi.useFakeTimers({ toFake: ["Date"], now: new Date("2026-10-14T09:00:00Z") });
});
import { mockBeautyApi } from "./mock-client";

// Mock-«сервер» зберігає стан між тестами в межах файла: тести написані так, щоб не залежати від порядку.
// Дати демо-набору відносні до сьогодні (F3/F4), тому системний час фіксуємо: середа 14.10.2026, 12:00 за Києвом.
// Тиждень календаря: пн 12.10 .. нд 18.10; закриття Поділу (санітарний день) = сьогодні + 2 = пт 16.10.

async function catchError(p: Promise<unknown>): Promise<BeautyApiError> {
  try {
    await p;
  } catch (e) {
    return e as BeautyApiError;
  }
  throw new Error("очікувалась помилка");
}

beforeEach(() => {
  process.env.NEXT_PUBLIC_MOCK_ROLE = "owner";
});

describe("mock API: вихідні закладу (§17)", () => {
  it("демо: Центр має вихідну неділю, закладів без вихідних немає за замовчуванням", async () => {
    const list = await mockBeautyApi.getLocations();
    expect(list.find((l) => l.id === "c")?.closedWeekdays).toEqual(["sun"]);
    expect(list.find((l) => l.id === "k")?.closedWeekdays).toEqual([]);
  });

  it("нові вихідні з активними записами -> 409 з conflicts[] без клієнтських даних; confirm=true застосовує", async () => {
    const err = await catchError(mockBeautyApi.setClosedWeekdays("c", ["sun", "sat"]));
    expect(err.status).toBe(409);
    expect(err.code).toBe("has_appointments_on_closed_days");
    expect(err.conflicts!.length).toBeGreaterThan(0);
    for (const c of err.conflicts!) {
      expect(Object.keys(c).sort()).toEqual(["appointmentId", "serviceName", "specialistName", "startsAt"]);
    }
    // зміни немає
    expect((await mockBeautyApi.getLocations()).find((l) => l.id === "c")?.closedWeekdays).toEqual(["sun"]);

    expect(await mockBeautyApi.setClosedWeekdays("c", ["sun", "sat"], true)).toEqual(["sun", "sat"]);
    expect((await mockBeautyApi.getLocations()).find((l) => l.id === "c")?.closedWeekdays).toEqual(["sun", "sat"]);
    // прибрати вихідний - без підтвердження
    expect(await mockBeautyApi.setClosedWeekdays("c", ["sun"])).toEqual(["sun"]);
  });

  it("календар: закриття за датою й закритий день тижня затінюють колонку; відкриті дні - null", async () => {
    // Ірина Мельник (im) працює лише в Поділ: санітарний день = пт 16.10 (сьогодні + 2) з причиною.
    const week = await mockBeautyApi.getCalendarWeek("im");
    expect(week.weekStart).toBe("2026-10-12");
    expect(week.closedDays).toHaveLength(7);
    expect(week.closedDays![4]).toMatchObject({ date: "2026-10-16", source: "closure", reason: "Санітарний день" });
    expect(week.closedDays![0]).toBeNull();
    expect(week.closedDays![6]).toBeNull(); // Поділ у неділю працює

    // Марина за замовчуванням у Центрі: неділя в Центрі закрита (хоч у графіку Марини вона робоча)
    const m = await mockBeautyApi.getCalendarWeek("m");
    expect(m.locationId).toBe("c");
    expect(m.closedDays![6]).toMatchObject({ source: "weekday" });
  });

  it("specialist отримує закритий день без причини", async () => {
    // Печерськ, сб 17.10: закриття з причиною (на цей день у Марини є записи, тому confirm=true).
    const k = await mockBeautyApi.addClosure("k", { dateFrom: "2026-10-17", dateTo: "2026-10-17", reason: "Корпоратив", confirm: true });
    const asOwner = await mockBeautyApi.getCalendarWeek("m", false, "k");
    expect(asOwner.closedDays![5]).toMatchObject({ source: "closure", reason: "Корпоратив" });

    process.env.NEXT_PUBLIC_MOCK_ROLE = "specialist";
    const week = await mockBeautyApi.getCalendarWeek("m", false, "k");
    const day = week.closedDays![5]!;
    expect(day.source).toBe("closure");
    expect(day.reason).toBeUndefined();
    const closures = await mockBeautyApi.getClosures("p", { from: "2026-10-01", to: "2026-10-31" });
    expect(closures).toHaveLength(1);
    expect(closures[0].reason).toBeUndefined();
    expect((await catchError(mockBeautyApi.addClosure("p", { dateFrom: "2026-11-01", dateTo: "2026-11-01" }))).status).toBe(403);

    process.env.NEXT_PUBLIC_MOCK_ROLE = "owner";
    await mockBeautyApi.deleteClosure("k", k.id);
  });

  it("closure_overlap, invalid_dates, delete", async () => {
    const overlap = await catchError(mockBeautyApi.addClosure("p", { dateFrom: "2026-10-16", dateTo: "2026-10-17" }));
    expect([overlap.status, overlap.code]).toEqual([409, "closure_overlap"]);

    const bad = await catchError(mockBeautyApi.addClosure("p", { dateFrom: "2026-11-05", dateTo: "2026-11-04" }));
    expect([bad.status, bad.code]).toEqual([422, "invalid_dates"]);

    const created = await mockBeautyApi.addClosure("p", { dateFrom: "2026-11-20", dateTo: "2026-11-21", reason: " Ремонт " });
    expect(created).toMatchObject({ locationId: "p", reason: "Ремонт" });
    await mockBeautyApi.deleteClosure("p", created.id);
    const list = await mockBeautyApi.getClosures("p", { from: "2026-11-01", to: "2026-11-30" });
    expect(list.some((c) => c.id === created.id)).toBe(false);
  });

  it("закриття на день із записами: 409 з переліком, confirm=true додає; перенесення на закритий день - location_closed", async () => {
    // Центр, пт 16.10: майбутні записи Марини/Анни/Оксани
    const err = await catchError(mockBeautyApi.addClosure("c", { dateFrom: "2026-10-16", dateTo: "2026-10-16" }));
    expect(err.code).toBe("has_appointments_on_closed_days");
    expect(err.conflicts!.length).toBeGreaterThan(0);
    const closure = await mockBeautyApi.addClosure("c", { dateFrom: "2026-10-16", dateTo: "2026-10-16", confirm: true });

    // Запис Марини в Центрі на ср 14.10 (14:00) переносимо: на закритий день - location_closed, на відкритий - ок.
    const week = await mockBeautyApi.getCalendarWeek("m");
    const appt = week.appointments.find((a) => a.startsAt.startsWith("2026-10-14T14"))!;
    expect(appt).toBeDefined();
    const moveErr = await catchError(mockBeautyApi.moveAppointment(appt.id, "2026-10-16T11:00"));
    expect([moveErr.status, moveErr.code]).toEqual([409, "location_closed"]);
    await expect(mockBeautyApi.moveAppointment(appt.id, "2026-10-15T14:00")).rejects.toMatchObject({ code: "outside_working_hours" }); // чт - Марина в Печерську, у Центрі не працює
    await expect(mockBeautyApi.moveAppointment(appt.id, "2026-10-14T16:00")).resolves.toBeUndefined();
    await mockBeautyApi.deleteClosure("c", closure.id);
  });
});
