import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const apiJson = vi.fn<(path: string, init?: { method?: string; body?: string }) => Promise<unknown>>();
vi.mock("@/features/beauty-auth/session", () => ({
  apiJson: (path: string, init?: { method?: string; body?: string }) => apiJson(path, init),
  getUser: () => ({ id: "u", role: "owner", specialistId: null }),
}));

import { staffOfLocation } from "../location-hours";
import { httpBeautyApi } from "./http-client";

const KYIV = "Europe/Kyiv";
const hours = (from: string, to: string, days: string[]) => Object.fromEntries(days.map((d) => [d, [{ from, to }]]));

const roster = [
  {
    id: "s1",
    name: "Марина",
    phone: null,
    position: "Майстер",
    photoUrl: null,
    isActive: true,
    services: [{ id: "sv", name: "Манікюр" }],
    locations: [
      { locationId: "L1", locationName: "Центр", isActive: true, workingHours: hours("09:00", "18:00", ["mon", "tue", "wed", "sun"]) },
      { locationId: "L2", locationName: "Поділ", isActive: false, workingHours: hours("09:00", "18:00", ["thu"]) },
    ],
  },
];
const known = [{ id: "L1", name: "Центр", timezone: KYIV, isActive: true, closedWeekdays: ["sun"], workingHours: hours("10:00", "17:00", ["mon", "tue", "thu", "sun"]) }];

const appt = (id: string, startsAt: string, over: Record<string, unknown> = {}) => ({
  id,
  locationId: "L1",
  locationName: "Центр",
  specialistId: "s1",
  specialistName: "Марина",
  serviceId: "sv",
  serviceName: "Манікюр",
  clientId: "c1",
  clientName: "Олена",
  startsAt,
  endsAt: startsAt,
  durationMinutes: 60,
  status: "confirmed",
  source: "admin",
  priceOriginal: 700,
  priceFinal: 700,
  promotionId: null,
  timezone: KYIV,
  ...over,
});

let calls: { path: string; init?: { method?: string; body?: string } }[] = [];
let appointments: unknown[] = [];
let closuresFail = false;
let overview: unknown = null;
let clientDetail: unknown = null;

beforeEach(() => {
  calls = [];
  closuresFail = false;
  appointments = [];
  apiJson.mockReset().mockImplementation(async (path, init) => {
    calls.push({ path, init });
    if (path.startsWith("/api/beauty/specialists")) return roster;
    if (path.startsWith("/api/beauty/locations") && path.includes("/closures")) {
      if (closuresFail) throw new Error("boom");
      return [];
    }
    if (path.startsWith("/api/beauty/locations")) return known;
    if (path.startsWith("/api/beauty/appointments") && init?.method === "PATCH") return {};
    if (path.startsWith("/api/beauty/appointments")) return appointments;
    if (path.startsWith("/api/beauty/overview")) return overview;
    if (path.startsWith("/api/beauty/clients?")) return [];
    if (path.startsWith("/api/beauty/clients/")) return clientDetail;
    throw new Error(`unexpected ${path}`);
  });
  // Середа 28.10.2026 12:00 за Києвом (зима, UTC+2): тиждень 26.10-01.11.
  vi.useFakeTimers({ toFake: ["Date"], now: new Date("2026-10-28T10:00:00Z") });
});
afterEach(() => vi.useRealTimers());

describe("F2: реальний режим - час у зоні закладу", () => {
  it("календар: 08:00Z = 10:00 за Києвом, а запис о 22:30Z потрапляє в колонку наступної доби", async () => {
    appointments = [appt("a1", "2026-10-26T08:00:00+00:00"), appt("a2", "2026-10-26T22:30:00+00:00")];
    const w = await httpBeautyApi.getCalendarWeek("s1");
    expect(w.weekStart).toBe("2026-10-26");
    expect(w.appointments.map((a) => [a.id, a.startsAt])).toEqual([
      ["a1", "2026-10-26T10:00"],
      ["a2", "2026-10-27T00:30"],
    ]);
  });

  it("тиждень запитується межами доби закладу, а не браузера", async () => {
    await httpBeautyApi.getCalendarWeek("s1");
    const list = calls.find((c) => c.path.startsWith("/api/beauty/appointments?"))!;
    expect(decodeURIComponent(list.path)).toContain("from=2026-10-25T22:00:00.000Z");
    expect(decodeURIComponent(list.path)).toContain("to=2026-11-01T22:00:00.000Z");
  });

  it("«сьогодні» і тиждень - за зоною закладу: 25.10 22:30Z уже понеділок 26.10 у Києві", async () => {
    vi.setSystemTime(new Date("2026-10-25T22:30:00Z"));
    const w = await httpBeautyApi.getCalendarWeek("s1");
    expect(w.weekStart).toBe("2026-10-26");
  });

  it("перенос: локальні 14:00 закладу йдуть як 12:00Z узимку й 11:00Z влітку", async () => {
    appointments = [appt("a1", "2026-10-26T08:00:00+00:00")];
    await httpBeautyApi.getCalendarWeek("s1");
    await httpBeautyApi.moveAppointment("a1", "2026-10-26T14:00");
    const patch = calls.find((c) => c.init?.method === "PATCH")!;
    expect(patch.path).toBe("/api/beauty/appointments/a1");
    expect(JSON.parse(patch.init!.body!)).toEqual({ startsAt: "2026-10-26T12:00:00Z" });

    vi.setSystemTime(new Date("2026-07-15T10:00:00Z"));
    appointments = [appt("a2", "2026-07-15T07:00:00+00:00")];
    const summer = await httpBeautyApi.getCalendarWeek("s1");
    expect(summer.appointments[0].startsAt).toBe("2026-07-15T10:00");
    await httpBeautyApi.moveAppointment("a2", "2026-07-15T14:00");
    const last = calls.filter((c) => c.init?.method === "PATCH").at(-1)!;
    expect(JSON.parse(last.init!.body!)).toEqual({ startsAt: "2026-07-15T11:00:00Z" });
  });

  it("огляд: GET /overview, час рядків у зоні закладу, KPI із відповіді (включно з вільними слотами)", async () => {
    overview = {
      date: "2026-10-28",
      timezone: KYIV,
      kpi: { appointmentsCount: 2, revenue: 1500, newClients: 1, freeSlots: 7 },
      appointments: [
        appt("b2", "2026-10-28T12:30:00+00:00"),
        appt("b1", "2026-10-28T08:00:00+00:00", { status: "completed" }),
        appt("b3", "2026-10-28T09:00:00+00:00", { status: "cancelled" }),
      ],
    };
    const o = await httpBeautyApi.getOverview(null);
    expect(calls.some((c) => c.path === "/api/beauty/overview")).toBe(true);
    expect(o.rows.map((r) => [r.id, r.time])).toEqual([
      ["b1", "10:00"],
      ["b2", "14:30"],
    ]);
    expect(o.kpis.map((k) => k.label)).toEqual(["Записів сьогодні", "Виручка за день", "Нові клієнти", "Вільних слотів"]);
    expect(o.kpis[3].value).toBe("7");
    expect(o.dateLabel).toBe("28 жовтня");
  });

  it("огляд для закладу передає locationId", async () => {
    overview = { date: "2026-10-28", timezone: KYIV, kpi: { appointmentsCount: 0, revenue: 0, newClients: 0, freeSlots: 0 }, appointments: [] };
    await httpBeautyApi.getOverview("L1");
    expect(calls.some((c) => c.path === "/api/beauty/overview?locationId=L1")).toBe(true);
  });

  it("картка клієнта: дата й час візиту в зоні закладу", async () => {
    clientDetail = {
      client: { id: "c1", fullName: "Олена", phone: null, email: null, visits: 1, lastVisitAt: null },
      notes: [],
      history: [appt("h1", "2026-10-25T22:30:00+00:00", { status: "completed" })],
    };
    const c = await httpBeautyApi.getClient("c1");
    expect(c!.visits[0].dateLabel).toBe("26 жовтня, 00:30");
  });
});

describe("F5: неактивні зв'язки майстра із закладом", () => {
  it("getStaff зберігає isActive зв'язку, staffOfLocation не рахує знятий заклад", async () => {
    const staff = await httpBeautyApi.getStaff();
    expect(staff[0].locations.map((l) => [l.locationId, l.isActive])).toEqual([
      ["L1", true],
      ["L2", false],
    ]);
    expect(staffOfLocation(staff, "L1")).toHaveLength(1);
    expect(staffOfLocation(staff, "L2")).toHaveLength(0);
  });

  it("календар: чипи й підпис майстра лише з активних закладів", async () => {
    const w = await httpBeautyApi.getCalendarWeek("s1");
    expect(w.masters[0].locationName).toBe("Центр");
    expect(w.locations).toEqual([{ id: "L1", name: "Центр" }]);
    expect(w.locationId).toBe("L1");
  });
});

describe("F1: доступність у реальному календарі", () => {
  it("графік ∩ години закладу ∩ вихідні: пн 10:00-17:00, нд закритий, ср - заклад не працює", async () => {
    const w = await httpBeautyApi.getCalendarWeek("s1");
    expect(w.availability![0]).toMatchObject({ state: "open", free: [{ from: 600, to: 1020 }] });
    expect(w.availability![6].state).toBe("location_closed");
    expect(w.closedDays![6]).toMatchObject({ source: "weekday" });
    expect(w.availability![2].state).toBe("location_hours_off"); // ср: у годинах закладу немає
    expect(w.availability![3].state).toBe("master_off"); // чт: заклад працює, а графіка майстра у цьому закладі немає
    expect(w.gridStartHour).toBe(9);
    expect(w.gridEndHour).toBe(18);
  });
});

describe("F9: збій завантаження закриттів не мовчазний", () => {
  it("збій запиту закриттів: календар відкривається, але є availabilityWarning", async () => {
    closuresFail = true;
    appointments = [appt("a1", "2026-10-26T08:00:00+00:00")];
    const w = await httpBeautyApi.getCalendarWeek("s1");
    expect(w.availabilityWarning).toBe(true);
    expect(w.appointments).toHaveLength(1);
  });

  it("без збою попередження немає", async () => {
    const w = await httpBeautyApi.getCalendarWeek("s1");
    expect(w.availabilityWarning).toBeUndefined();
  });
});
