import { beforeEach, describe, expect, it } from "vitest";
import { BeautyApiError } from "@/features/beauty-auth/errors";
import { mockBeautyApi } from "./mock-client";

// Mock-«сервер» зберігає стан між тестами в межах файла: тести написані так, щоб не залежати від порядку.

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

  it("календар: закритий день тижня й закриття за датою затінюють колонку; відкриті дні - null", async () => {
    // Ірина Мельник (im) працює лише в Поділ: закриття 8 жовтня (чт) із причиною.
    const week = await mockBeautyApi.getCalendarWeek("im");
    expect(week.closedDays).toHaveLength(7);
    expect(week.closedDays![3]).toMatchObject({ date: "2026-10-08", source: "closure", reason: "Санітарний день" });
    expect(week.closedDays![0]).toBeNull();
    expect(week.closedDays![6]).toBeNull(); // Поділ у неділю працює

    // Марина працює в Центрі в неділю, але Центр у неділю закритий
    const m = await mockBeautyApi.getCalendarWeek("m");
    expect(m.closedDays![6]).toMatchObject({ source: "weekday" });
  });

  it("specialist отримує закритий день без причини", async () => {
    // Центр, пт 9 жовтня: закриття з причиною (на цей день є записи, тому confirm=true).
    await mockBeautyApi.addClosure("c", { dateFrom: "2026-10-09", dateTo: "2026-10-09", reason: "Корпоратив", confirm: true });
    const asOwner = await mockBeautyApi.getCalendarWeek("m");
    expect(asOwner.closedDays![4]).toMatchObject({ source: "closure", reason: "Корпоратив" });

    process.env.NEXT_PUBLIC_MOCK_ROLE = "specialist";
    const week = await mockBeautyApi.getCalendarWeek("m");
    const day = week.closedDays![4]!;
    expect(day.source).toBe("closure");
    expect(day.reason).toBeUndefined();
    const closures = await mockBeautyApi.getClosures("p", { from: "2026-10-01", to: "2026-10-31" });
    expect(closures).toHaveLength(1);
    expect(closures[0].reason).toBeUndefined();
    expect((await catchError(mockBeautyApi.addClosure("p", { dateFrom: "2026-11-01", dateTo: "2026-11-01" }))).status).toBe(403);
  });

  it("closure_overlap, invalid_dates, delete", async () => {
    const overlap = await catchError(mockBeautyApi.addClosure("p", { dateFrom: "2026-10-08", dateTo: "2026-10-09" }));
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
    // Центр, 6 жовтня (вт): записи Марини/Анни/Оксани
    const err = await catchError(mockBeautyApi.addClosure("c", { dateFrom: "2026-10-06", dateTo: "2026-10-06" }));
    expect(err.code).toBe("has_appointments_on_closed_days");
    expect(err.conflicts!.length).toBeGreaterThan(0);
    await mockBeautyApi.addClosure("c", { dateFrom: "2026-10-06", dateTo: "2026-10-06", confirm: true });

    const moveErr = await catchError(mockBeautyApi.moveAppointment("m0", "2026-10-06T10:00"));
    expect([moveErr.status, moveErr.code]).toEqual([409, "location_closed"]);
    await expect(mockBeautyApi.moveAppointment("m0", "2026-10-07T10:00")).resolves.toBeUndefined();
  });
});
