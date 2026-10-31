import { beforeEach, describe, expect, it, vi } from "vitest";

// Час фіксується ДО імпорту mock-клієнтів: їхній початковий стан будується від «сьогодні».
// Середа 14.10.2026, 12:00 за Києвом (літо, UTC+3). Тиждень: пн 12.10 .. нд 18.10.
vi.hoisted(() => {
  vi.useFakeTimers({ toFake: ["Date"], now: new Date("2026-10-14T09:00:00Z") });
});

import { bookableAt, toMinutes } from "@/features/beauty-shared/availability";
import { mockBookingApi, mockSlots } from "@/features/beauty-booking/api/mock-client";
import { isClosedDate } from "@/features/beauty-booking/dates";
import { mockBeautyApi } from "./mock-client";

const NOW = new Date("2026-10-14T09:00:00Z");

beforeEach(() => {
  process.env.NEXT_PUBLIC_MOCK_ROLE = "owner";
  window.sessionStorage.clear();
});

describe("F3: календар від поточної дати, одні дані з рештою екранів", () => {
  it("тиждень = поточний понеділок..неділя, підписи днів згенеровані", async () => {
    const w = await mockBeautyApi.getCalendarWeek("m");
    expect(w.weekStart).toBe("2026-10-12");
    expect(w.weekLabel).toBe("Тиждень 12 жовтня – 18 жовтня");
    expect(w.days).toEqual(["Пн 12", "Вт 13", "Ср 14", "Чт 15", "Пт 16", "Сб 17", "Нд 18"]);
  });

  it("відсутність «Оксана сьогодні відсутня» (staff) збігається з календарем: у її день записів немає", async () => {
    const staff = await mockBeautyApi.getStaff();
    expect(staff.find((m) => m.id === "o")).toBeTruthy();
    const abs = await mockBeautyApi.getAbsences({ from: "2026-10-12", to: "2026-10-18", specialistId: "o" });
    expect(abs).toEqual([expect.objectContaining({ dateFrom: "2026-10-14", dateTo: "2026-10-14", status: "approved" })]);
    const w = await mockBeautyApi.getCalendarWeek("o");
    expect(w.appointments.some((a) => a.startsAt.startsWith("2026-10-14"))).toBe(false);
  });

  it("кількість майбутніх записів реальна (не 0), як і в календарі", async () => {
    expect(await mockBeautyApi.getUpcomingAppointmentsCount("o")).toBeGreaterThan(0);
    expect(await mockBeautyApi.getUpcomingAppointmentsCount("m")).toBeGreaterThan(0);
  });

  it("аналітика посилається лише на майстрів, які є в штаті", async () => {
    const ids = new Set((await mockBeautyApi.getStaff()).map((m) => m.id));
    const b = await mockBeautyApi.getLocationBreakdown("c", { from: "2026-09-01", to: "2026-10-14" });
    expect(b.specialists.length).toBeGreaterThan(0);
    for (const s of b.specialists) expect(ids.has(s.specialistId)).toBe(true);
    for (const loc of await mockBeautyApi.getLocationsAnalytics({ from: "2026-09-01", to: "2026-10-14", compare: false })) {
      const d = await mockBeautyApi.getLocationBreakdown(loc.locationId, { from: "2026-09-01", to: "2026-10-14" });
      for (const s of d.specialists) expect(ids.has(s.specialistId)).toBe(true);
    }
  });
});

describe("F4: огляд і календар - ті самі записи, імена й клієнти", () => {
  it("кожен рядок огляду є в календарі свого майстра з тими самими id/іменами/часом", async () => {
    const o = await mockBeautyApi.getOverview(null);
    expect(o.rows.length).toBeGreaterThan(0);
    const staff = await mockBeautyApi.getStaff();
    for (const row of o.rows) {
      expect(staff.find((m) => m.id === row.specialistId)?.name).toBe(row.specialistName);
      const w = await mockBeautyApi.getCalendarWeek(row.specialistId, false, row.locationId);
      const a = w.appointments.find((x) => x.id === row.id);
      expect(a, `${row.id} має бути в календарі`).toBeDefined();
      expect(a!.clientName).toBe(row.clientName);
      expect(a!.serviceName).toBe(row.serviceName);
      expect(a!.startsAt.slice(11, 16)).toBe(row.time);
      expect(a!.startsAt.slice(0, 10)).toBe("2026-10-14");
    }
    expect(o.kpis[0].value).toBe(String(o.rows.length));
    expect(o.kpis.map((k) => k.label)).toContain("Вільних слотів");
  });

  it("огляд закладу показує лише його записи; різні clientId", async () => {
    const all = await mockBeautyApi.getOverview(null);
    const centre = await mockBeautyApi.getOverview("c");
    expect(centre.rows.every((r) => r.locationId === "c")).toBe(true);
    expect(centre.rows.length).toBeLessThanOrEqual(all.rows.length);
    const ids = new Set<string | null>();
    for (const m of ["m", "a", "o", "im"]) (await mockBeautyApi.getCalendarWeek(m)).appointments.forEach((a) => ids.add(a.clientId));
    expect(ids.size).toBeGreaterThanOrEqual(4);
  });

  it("клієнти мають різні профілі, а візити профілю - реальні записи з майстрами зі штату", async () => {
    const list = await mockBeautyApi.getClients();
    expect(new Set(list.map((c) => c.meta)).size).toBeGreaterThan(1);
    const staffIds = new Set((await mockBeautyApi.getStaff()).map((m) => m.id));
    const [oc, sm] = await Promise.all([mockBeautyApi.getClient("oc"), mockBeautyApi.getClient("sm")]);
    expect(oc!.contactLine).not.toBe(sm!.contactLine);
    expect(oc!.kpis[0].value).not.toBe(sm!.kpis[0].value);
    for (const v of [...oc!.visits, ...sm!.visits]) expect(staffIds.has(v.specialistId)).toBe(true);
  });
});

describe("F1: доступність у mock-календарі", () => {
  it("кожен запис лежить у незатіненому часі (або день заблоковано відсутністю - тоді записів немає)", async () => {
    for (const id of ["m", "a", "o", "im", "ng"]) {
      const w = await mockBeautyApi.getCalendarWeek(id);
      expect(w.availability).toHaveLength(7);
      for (const a of w.appointments) {
        const d = w.availability![w.days.findIndex((_, i) => a.startsAt.startsWith(`2026-10-${12 + i}`))];
        expect(bookableAt(d, toMinutes(a.startsAt.slice(11, 16)), a.durationMinutes), `${id} ${a.startsAt}`).toBe(true);
      }
    }
  });

  it("майстер у двох закладах: чипи, доступність і записи залежать від обраного закладу", async () => {
    const centre = await mockBeautyApi.getCalendarWeek("m");
    expect(centre.locations!.map((l) => l.id)).toEqual(["c", "k"]);
    expect(centre.locationId).toBe("c");
    const pech = await mockBeautyApi.getCalendarWeek("m", false, "k");
    expect(pech.locationId).toBe("k");
    // Чт (idx 3): у Печерську працює 10-18 при годинах закладу 10-19; у Центрі чт - "Не працює"
    expect(pech.availability![3]).toMatchObject({ state: "open", free: [{ from: 600, to: 1080 }] });
    expect(centre.availability![3].state).toBe("master_off");
    // Сб (idx 5): графік 10-18 ∩ години Печерська 10-16 -> 16-18 поза годинами закладу
    expect(pech.availability![5].free).toEqual([{ from: 600, to: 960 }]);
    expect(pech.availability![5].hoursOff).toContainEqual({ from: 960, to: 1440 });
    expect(pech.appointments.every((a) => a.locationId === "k")).toBe(true);
    expect(centre.appointments.every((a) => a.locationId === "c")).toBe(true);
    expect(centre.otherLocationsCount).toBeGreaterThan(0);
  });

  it("сітка підлаштована під графік, а не фіксована 9-20; Марина (10-19 у Центрі) -> 10-19", async () => {
    const w = await mockBeautyApi.getCalendarWeek("m");
    expect([w.gridStartHour, w.gridEndHour]).toEqual([10, 19]);
    const anna = await mockBeautyApi.getCalendarWeek("a");
    expect([anna.gridStartHour, anna.gridEndHour]).toEqual([9, 18]);
  });

  it("F5: знятий зв'язок Анни з Печерськом не показується заклад-чипом і в підписі майстра", async () => {
    const w = await mockBeautyApi.getCalendarWeek("a");
    expect(w.locations).toEqual([{ id: "c", name: "Центр" }]);
    expect(w.masters.find((m) => m.id === "a")!.locationName).toBe("Центр");
  });

  it("перенос у недоступний час заборонений: відсутність, години закладу, вихідний, поза графіком", async () => {
    const anna = await mockBeautyApi.getCalendarWeek("a");
    const aAppt = anna.appointments.find((a) => a.startsAt >= "2026-10-14T12:00")!; // майбутній
    // Відпустка Анни сб 17 - нд 18
    await expect(mockBeautyApi.moveAppointment(aAppt.id, "2026-10-17T11:00")).rejects.toMatchObject({ status: 409, code: "specialist_unavailable" });
    // Центр у неділю закритий; але Анна на відпустці, тому беремо Оксану
    const oks = await mockBeautyApi.getCalendarWeek("o");
    const oAppt = oks.appointments.find((a) => a.startsAt >= "2026-10-14T12:00")!;
    await expect(mockBeautyApi.moveAppointment(oAppt.id, "2026-10-18T12:00")).rejects.toMatchObject({ code: "location_closed" });
    // Марина у Печерську в пт-сб: сб після 16:00 - поза годинами закладу
    const mar = await mockBeautyApi.getCalendarWeek("m", false, "k");
    const kAppt = mar.appointments.find((a) => a.startsAt >= "2026-10-15T00:00")!;
    await expect(mockBeautyApi.moveAppointment(kAppt.id, "2026-10-17T16:00")).rejects.toMatchObject({ status: 409, code: "outside_location_hours" });
    // минуле
    await expect(mockBeautyApi.moveAppointment(kAppt.id, "2026-10-13T11:00")).rejects.toMatchObject({ code: "slot_in_past" });
  });
});

describe("F8: публічний mock бере дані з того самого джерела й рахує слоти за правилами", () => {
  it("каталог: майстри, години й закриття збігаються з адмінкою; причин закриття немає", async () => {
    const locs = await mockBookingApi.getLocations("t");
    const admin = await mockBeautyApi.getLocations();
    expect(locs.map((l) => l.id)).toEqual(admin.map((l) => l.id));
    expect(locs.find((l) => l.id === "c")!.workingHours).toEqual(admin.find((l) => l.id === "c")!.workingHours);
    expect(locs.find((l) => l.id === "p")!.workingHours).toBeNull();
    const podil = locs.find((l) => l.id === "p")!;
    expect(podil.closures).toEqual([{ dateFrom: "2026-10-16", dateTo: "2026-10-16" }]);
    expect(JSON.stringify(locs)).not.toContain("Санітарний");

    const adminStaff = await mockBeautyApi.getStaff();
    for (const l of locs) {
      for (const s of (await mockBookingApi.getSpecialists("t", l.id)).filter((x) => x.id !== "any")) {
        const m = adminStaff.find((x) => x.id === s.id)!;
        expect(m.isActive).toBe(true);
        expect(m.name).toBe(s.name);
        expect(m.locations.find((x) => x.locationId === l.id)?.isActive).toBe(true);
      }
    }
    // неактивна Наталя Гук і знятий зв'язок Анни з Печерськом не пропонуються
    expect((await mockBookingApi.getSpecialists("t", "k")).map((s) => s.id)).toEqual(["any", "m"]);
    expect((await mockBookingApi.getServices("t", "c", "o")).map((s) => s.id)).toEqual(["cl"]);
  });

  it("слоти: вихідний тижня, закриття й відсутність дають порожній список", () => {
    expect(mockSlots({ locationId: "c", specialistId: "any", serviceId: "mn", date: "2026-10-18" }, NOW)).toEqual([]); // нд, Центр
    expect(mockSlots({ locationId: "p", specialistId: "any", serviceId: "hc", date: "2026-10-16" }, NOW)).toEqual([]); // закриття Поділу
    expect(mockSlots({ locationId: "c", specialistId: "a", serviceId: "col", date: "2026-10-17" }, NOW)).toEqual([]); // відпустка Анни
    expect(mockSlots({ locationId: "c", specialistId: "a", serviceId: "col", date: "2026-10-24" }, NOW).length).toBeGreaterThan(0);
  });

  it("слоти не виходять за години закладу: Печерськ сб 10-16, послуга 90 хв -> останній старт 14:30", () => {
    const s = mockSlots({ locationId: "k", specialistId: "m", serviceId: "mn", date: "2026-10-31" }, NOW);
    expect(s[0].label).toBe("10:00");
    expect(s[s.length - 1].label).toBe("14:30");
  });

  it("слоти поважають зайняте з календаря адмінки й зону: Анна, пн 19.10, стрижка 60 хв", () => {
    const s = mockSlots({ locationId: "c", specialistId: "a", serviceId: "hc", date: "2026-10-19" }, NOW);
    const labels = s.map((x) => x.label);
    // у Анни пн 11:00-13:30 (фарбування) і 15:00-16:00 (стрижка) -> між ними вікно з 13:30
    expect(labels[0]).toBe("09:00");
    expect(s[0].startsAt).toBe("2026-10-19T06:00:00Z"); // 09:00 за Києвом (літо) = 06:00Z
    expect(labels).toContain("10:00");
    expect(labels).not.toContain("10:15");
    expect(labels).not.toContain("13:15");
    expect(labels).toContain("13:30");
    expect(labels).toContain("14:00");
    expect(labels).not.toContain("14:15");
    expect(labels).toContain("16:00");
    // графік Анни до 18:00: останній старт 17:00
    expect(labels[labels.length - 1]).toBe("17:00");
  });

  it("минулі слоти сьогодні не пропонуються", () => {
    const s = mockSlots({ locationId: "c", specialistId: "o", serviceId: "cl", date: "2026-10-14" }, NOW);
    expect(s.every((x) => toMinutes(x.label) > 12 * 60)).toBe(true);
  });

  it("запис: закритий день і недоступний час -> 409 slot_unavailable; дійсний слот створюється й зникає зі списку", async () => {
    const base = { locationId: "c", specialistId: "any", serviceId: "cl", client: { name: "Тест Клієнт", phone: "+380501112233" }, reminder: "none" as const, paymentMethod: "cash" as const };
    await expect(mockBookingApi.createAppointment("t", { ...base, startsAt: "2026-10-18T09:00:00Z" }, "k1")).rejects.toMatchObject({ status: 409, code: "slot_unavailable" });
    const slots = mockSlots({ locationId: "c", specialistId: "any", serviceId: "cl", date: "2026-10-26" }, NOW);
    expect(slots.length).toBeGreaterThan(0);
    const first = slots[0];
    await expect(mockBookingApi.createAppointment("t", { ...base, startsAt: "2026-10-26T05:00:00Z" }, "k2")).rejects.toMatchObject({ code: "slot_unavailable" }); // 08:00 - до відкриття
    const ok = await mockBookingApi.createAppointment("t", { ...base, startsAt: first.startsAt }, "k3");
    expect(ok.appointment.specialistName).toBe("Оксана Лис");
    const after = mockSlots({ locationId: "c", specialistId: "any", serviceId: "cl", date: "2026-10-26" }, NOW);
    expect(after.find((s) => s.startsAt === first.startsAt)).toBeUndefined();
  });

  it("dateOptions не пропонує день без інтервалів у годинах закладу (навіть без вихідного)", () => {
    expect(isClosedDate("2026-10-18", { closedWeekdays: [], workingHours: { mon: [{ from: "10:00", to: "19:00" }] } })).toBe(true);
    expect(isClosedDate("2026-10-19", { closedWeekdays: [], workingHours: { mon: [{ from: "10:00", to: "19:00" }] } })).toBe(false);
    expect(isClosedDate("2026-10-18", { closedWeekdays: [], workingHours: null })).toBe(false);
  });
});
