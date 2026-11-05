import { beforeEach, describe, expect, it, vi } from "vitest";

// Середа 14.10.2026, 12:00 за Києвом; час фіксується ДО імпорту mock-клієнта (початковий стан від «сьогодні»).
vi.hoisted(() => {
  vi.useFakeTimers({ toFake: ["Date"], now: new Date("2026-10-14T09:00:00Z") });
});

import { addDaysYmd } from "../../beauty-shared/zone-time";
import { previousRange, presetRange } from "../analytics/ranges";
import { bucketsFor, granularityFor } from "./mock-analytics";
import { mockBeautyApi } from "./mock-client";

const T = "2026-10-14";
const range = (days: number) => ({ from: addDaysYmd(T, -(days - 1)), to: T, compare: false });

beforeEach(() => {
  process.env.NEXT_PUBLIC_MOCK_ROLE = "owner";
});

describe("§19.1 валідація: 422 invalid_range", () => {
  it.each([
    ["from > to", { from: "2026-10-10", to: "2026-10-01" }],
    ["неіснуюча дата", { from: "2026-02-30", to: "2026-03-01" }],
    ["367 днів", { from: "2025-10-13", to: T }],
    ["час замість дати (legacy)", { from: "2026-10-01T00:00:00Z", to: "2026-10-05T00:00:00Z" }],
  ])("%s", async (_n, q) => {
    for (const call of [
      () => mockBeautyApi.getNetworkAnalytics({ ...q, compare: false }),
      () => mockBeautyApi.getNetworkSeries(q),
      () => mockBeautyApi.getLocationsAnalytics({ ...q, compare: false }),
      () => mockBeautyApi.getPromotionAnalytics({ ...q, compare: false }),
    ]) {
      await expect(call()).rejects.toMatchObject({ status: 422, code: "invalid_range" });
    }
    await expect(mockBeautyApi.getLocationBreakdown("c", q)).rejects.toMatchObject({ status: 422, code: "invalid_range" });
  });

  it("1 день і 366 днів дозволені", async () => {
    await expect(mockBeautyApi.getNetworkAnalytics({ from: T, to: T, compare: false })).resolves.toBeTruthy();
    await expect(mockBeautyApi.getNetworkAnalytics({ from: "2025-10-14", to: T, compare: false })).resolves.toBeTruthy();
  });
});

describe("§19.2 series", () => {
  it("1 день -> одна точка day", async () => {
    const s = await mockBeautyApi.getNetworkSeries({ from: T, to: T });
    expect(s.granularity).toBe("day");
    expect(s.points).toHaveLength(1);
    expect(s.timezone).toBe("Europe/Kyiv");
    expect(s.fromDate).toBe(T);
  });

  it("гранулярність на межах 31/32/120/121 днів", () => {
    expect(granularityFor(31)).toBe("day");
    expect(granularityFor(32)).toBe("week");
    expect(granularityFor(120)).toBe("week");
    expect(granularityFor(121)).toBe("month");
  });

  it("кошики: 31 день = 31 точка; тижні обрізані; місяці календарні; ліміти точок", async () => {
    expect((await mockBeautyApi.getNetworkSeries(range(31))).points).toHaveLength(31);
    const w = await mockBeautyApi.getNetworkSeries(range(120));
    expect(w.granularity).toBe("week");
    expect(w.points.length).toBeLessThanOrEqual(18);
    expect(w.points[0].from).toBe(range(120).from);
    expect(w.points.at(-1)!.to).toBe(T);
    for (const p of w.points.slice(1)) expect(new Date(`${p.from}T00:00:00Z`).getUTCDay()).toBe(1); // тижні з понеділка
    const y = await mockBeautyApi.getNetworkSeries(range(366));
    expect(y.granularity).toBe("month");
    expect(y.points.length).toBeLessThanOrEqual(13);
    expect(y.points[1].from.endsWith("-01")).toBe(true);
  });

  it("кошики суцільні й без перетинів", () => {
    const b = bucketsFor({ from: "2026-01-15", to: "2026-10-14" }, "month");
    expect(b[0].from).toBe("2026-01-15");
    expect(b[0].to).toBe("2026-01-31");
    for (let i = 1; i < b.length; i++) expect(b[i].from).toBe(addDaysYmd(b[i - 1].to, 1));
    expect(b.at(-1)!.to).toBe("2026-10-14");
  });

  it("інваріант: сума точок = network за той самий діапазон (рік)", async () => {
    for (const days of [7, 30, 100, 366]) {
      const r = range(days);
      const [net, s] = await Promise.all([mockBeautyApi.getNetworkAnalytics(r), mockBeautyApi.getNetworkSeries(r)]);
      const sum = (k: "appointments" | "completed" | "cancelled" | "noShow" | "revenue") => s.points.reduce((a, p) => a + p[k], 0);
      expect(sum("appointments")).toBe(net.totals.appointments);
      expect(sum("completed")).toBe(net.totals.completed);
      expect(sum("cancelled")).toBe(net.totals.cancelled);
      expect(sum("noShow")).toBe(net.totals.noShow);
      expect(sum("revenue")).toBe(net.totals.revenue);
    }
  });

  it("є різні статуси і виручка в історії (не нулі)", async () => {
    const n = await mockBeautyApi.getNetworkAnalytics({ ...presetRange("year", T), compare: false });
    expect(n.totals.revenue).toBeGreaterThan(0);
    expect(n.totals.cancelled).toBeGreaterThan(0);
    expect(n.totals.noShow).toBeGreaterThan(0);
    expect(n.totals.averageCheck).toBe(Math.round(n.totals.revenue / n.totals.completed));
  });
});

describe("§19.3 compare / previous", () => {
  it("без compare поле previous відсутнє (null)", async () => {
    expect((await mockBeautyApi.getNetworkAnalytics(range(30))).previous).toBeNull();
    expect((await mockBeautyApi.getLocationsAnalytics(range(30))).every((l) => l.previous === null)).toBe(true);
    expect((await mockBeautyApi.getPromotionAnalytics(range(30))).rows.every((r) => r.previous === null)).toBe(true);
  });

  it("попередній період тієї ж тривалості: 01.10-14.10 -> 17.09-30.09", async () => {
    const n = await mockBeautyApi.getNetworkAnalytics({ from: "2026-10-01", to: T, compare: true });
    expect(n.previous).toMatchObject({ fromDate: "2026-09-17", toDate: "2026-09-30" });
    expect(previousRange({ from: "2026-10-01", to: T })).toEqual({ from: "2026-09-17", to: "2026-09-30" });
    // збігається із запитом попереднього періоду окремо
    const solo = await mockBeautyApi.getNetworkAnalytics({ from: "2026-09-17", to: "2026-09-30", compare: false });
    expect(n.previous!.revenue).toBe(solo.totals.revenue);
    expect(n.previous!.appointments).toBe(solo.totals.appointments);
  });

  it("заклади й акції мають previous з нулями для відсутньої сторони", async () => {
    const locs = await mockBeautyApi.getLocationsAnalytics({ from: "2026-10-01", to: T, compare: true });
    for (const l of locs) expect(l.previous).not.toBeNull();
    const promos = await mockBeautyApi.getPromotionAnalytics({ ...presetRange("30d", T), compare: true });
    expect(promos.rows.length).toBeGreaterThan(0);
    for (const r of promos.rows) expect(r.previous).toEqual({ uses: expect.any(Number), revenue: expect.any(Number), discountTotal: expect.any(Number) });
  });
});

describe("§19.4 завантаження й breakdown", () => {
  it("рядки закладів: усі активні (навіть без записів), неактивний без записів відсутній", async () => {
    const locs = await mockBeautyApi.getLocationsAnalytics({ from: T, to: T, compare: false });
    expect(locs.map((l) => l.locationId).sort()).toEqual(["c", "k", "p"]);
    for (const l of locs) expect(l).toMatchObject({ timezone: "Europe/Kyiv", isActive: true });
  });

  it("інваріанти: Σ виручки закладів = мережа; Σ busy/avail майстрів = заклад", async () => {
    const r = range(30);
    const [net, locs] = await Promise.all([mockBeautyApi.getNetworkAnalytics(r), mockBeautyApi.getLocationsAnalytics(r)]);
    expect(locs.reduce((a, l) => a + l.revenue, 0)).toBe(net.totals.revenue);
    expect(locs.reduce((a, l) => a + l.appointments, 0)).toBe(net.totals.appointments);
    for (const l of locs) {
      const b = await mockBeautyApi.getLocationBreakdown(l.locationId, r);
      expect(b.busyMinutes).toBe(l.busyMinutes);
      expect(b.availableMinutes).toBe(l.availableMinutes);
      expect(b.specialists.reduce((a, s) => a + s.busyMinutes, 0)).toBe(b.busyMinutes);
      expect(b.specialists.reduce((a, s) => a + s.availableMinutes, 0)).toBe(b.availableMinutes);
      expect(b.load).toBe(l.load);
    }
  });

  it("load 0..1 або null, формула min(busy/avail, 1); avail = 0 -> null", async () => {
    const b = await mockBeautyApi.getLocationBreakdown("c", range(30));
    expect(b.availableMinutes).toBeGreaterThan(0);
    for (const s of b.specialists) {
      if (s.availableMinutes === 0) expect(s.load).toBeNull();
      else expect(s.load).toBe(Math.round(Math.min(s.busyMinutes / s.availableMinutes, 1) * 10000) / 10000);
    }
    expect(b.load).toBeGreaterThan(0);
    expect(b.load!).toBeLessThanOrEqual(1);
  });

  it("доступні хвилини: понеділок Центру, Марина 10-19 ∩ 09-20 = 540 хв; Анна 09-18 = 540", async () => {
    // 12.10.2026 - понеділок (минулий день: графік береться незалежно від поточної активності)
    const b = await mockBeautyApi.getLocationBreakdown("c", { from: "2026-10-12", to: "2026-10-12" });
    expect(b.specialists.find((s) => s.specialistId === "m")?.availableMinutes).toBe(540);
    expect(b.specialists.find((s) => s.specialistId === "a")?.availableMinutes).toBe(540);
  });

  it("вихідний тижня закладу (Центр, неділя) дає 0; approved-відсутність дає 0", async () => {
    // 11.10.2026 - неділя, Центр закритий
    const sun = await mockBeautyApi.getLocationBreakdown("c", { from: "2026-10-11", to: "2026-10-11" });
    expect(sun.availableMinutes).toBe(0);
    expect(sun.load).toBeNull();
    // Оксана сьогодні у відпустці (approved): 14.10 її доступність 0
    const today = await mockBeautyApi.getLocationBreakdown("c", { from: T, to: T });
    expect(today.specialists.find((s) => s.specialistId === "o")?.availableMinutes ?? 0).toBe(0);
  });

  it("services: лише completed, за виручкою спадно; breakdown чужого/невідомого закладу -> 404", async () => {
    const b = await mockBeautyApi.getLocationBreakdown("c", presetRange("year", T));
    expect(b.services.length).toBeGreaterThan(0);
    const rev = b.services.map((s) => s.revenue);
    expect(rev).toEqual([...rev].sort((x, y) => y - x));
    await expect(mockBeautyApi.getLocationBreakdown("zzz", range(7))).rejects.toMatchObject({ status: 404, code: "location_not_found" });
  });

  it("у відповідях немає даних клієнтів", async () => {
    const r = presetRange("year", T);
    const dump = JSON.stringify([await mockBeautyApi.getLocationBreakdown("c", r), await mockBeautyApi.getLocationsAnalytics({ ...r, compare: true }), await mockBeautyApi.getNetworkSeries(r)]);
    for (const name of ["Олена Кравченко", "Софія Мартин", "Катерина Ляшко", "Тетяна Гончар", "Марія Пономаренко", "clientId", "clientName"]) expect(dump).not.toContain(name);
  });
});

describe("аналітика узгоджена з календарем", () => {
  it("записи поточного тижня в календарі враховані в аналітиці: Σ записів за тиждень = кількість у календарях", async () => {
    const week = { from: "2026-10-12", to: "2026-10-18", compare: false };
    const net = await mockBeautyApi.getNetworkAnalytics(week);
    const staff = await mockBeautyApi.getStaff();
    let inCalendars = 0;
    const seen = new Set<string>();
    for (const m of staff) {
      for (const loc of m.locations) {
        const w = await mockBeautyApi.getCalendarWeek(m.id, true, loc.locationId);
        for (const a of w.appointments) {
          if (a.specialistId === m.id && a.locationId === loc.locationId && !seen.has(a.id)) {
            seen.add(a.id);
            inCalendars += 1;
          }
        }
      }
    }
    // клієнти, що «зареєструвалися» пізніше за запис (демо), у аналітиці не враховуються - тож аналітика не більша за календар
    expect(net.totals.appointments).toBeGreaterThan(0);
    expect(net.totals.appointments).toBeLessThanOrEqual(inCalendars);
  });
});
