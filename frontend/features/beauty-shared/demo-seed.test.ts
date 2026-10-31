import { describe, expect, it } from "vitest";
import { weekdayOf } from "@/features/beauty-admin/closed-days";
import { bookableAt, dayAvailability, toMinutes } from "./availability";
import { CATALOG, DEMO_LOCATIONS, STAFF, absencesSeed, appointmentsSeed, blocksOn, closuresSeed } from "./demo-seed";
import { addDaysYmd, mondayOfYmd } from "./zone-time";

/** F3/F4: демо-набір будується від «сьогодні» й не суперечить власним правилам - за будь-якого дня тижня/місяця. */
const TODAYS = Array.from({ length: 21 }, (_, i) => addDaysYmd("2026-10-12", i)).concat(["2027-03-27", "2027-03-29", "2026-12-31"]);

describe("демо-записи вкладаються в графіки, години, вихідні й відсутності (будь-яка дата)", () => {
  for (const today of TODAYS) {
    it(`сьогодні ${today}`, () => {
      const now = new Date(`${today}T09:00:00Z`);
      const appts = appointmentsSeed(today, now);
      const absences = absencesSeed(today);
      const closures = closuresSeed(today);
      const monday = mondayOfYmd(today);
      expect(appts.length).toBeGreaterThan(20);

      for (const a of appts) {
        const date = a.startsAt.slice(0, 10);
        const master = STAFF.find((m) => m.id === a.specialistId)!;
        const loc = DEMO_LOCATIONS.find((l) => l.id === a.locationId)!;
        const link = master.locations.find((l) => l.locationId === a.locationId)!;
        // заклад і зв'язок активні, послуга призначена майстру
        expect(loc.isActive).not.toBe(false);
        expect(link.isActive).not.toBe(false);
        expect(master.services.some((s) => s.id === a.serviceId)).toBe(true);
        expect(CATALOG.find((s) => s.id === a.serviceId)!.durationMinutes).toBe(a.durationMinutes);
        // не вихідний, не відсутність, у перетині графіка й годин закладу
        expect(blocksOn(absences, a.specialistId, date)).toBe(false);
        const day = dayAvailability(
          date,
          { masterHours: link.workingHours, locationHours: loc.workingHours, closedWeekdays: loc.closedWeekdays, closures: closures.filter((c) => c.locationId === loc.id) },
          false,
        );
        expect(day.state, `${a.id} ${a.startsAt}`).toBe("open");
        expect(bookableAt(day, toMinutes(a.startsAt.slice(11, 16)), a.durationMinutes), `${a.id} ${a.startsAt}`).toBe(true);
        expect(weekdayOf(date)).toBeTruthy();
        // поточний і наступний тиждень відносно сьогодні
        expect(date >= monday && date < addDaysYmd(monday, 14)).toBe(true);
      }
    });
  }

  it("записи одного майстра не перетинаються; id унікальні; клієнти різні", () => {
    const appts = appointmentsSeed("2026-10-14", new Date("2026-10-14T09:00:00Z"));
    expect(new Set(appts.map((a) => a.id)).size).toBe(appts.length);
    expect(new Set(appts.map((a) => a.clientId)).size).toBeGreaterThanOrEqual(4);
    for (const m of STAFF) {
      const mine = appts.filter((a) => a.specialistId === m.id).sort((a, b) => a.startsAt.localeCompare(b.startsAt));
      for (let i = 1; i < mine.length; i++) {
        if (mine[i].startsAt.slice(0, 10) !== mine[i - 1].startsAt.slice(0, 10)) continue;
        const prevEnd = toMinutes(mine[i - 1].startsAt.slice(11, 16)) + mine[i - 1].durationMinutes;
        expect(toMinutes(mine[i].startsAt.slice(11, 16))).toBeGreaterThanOrEqual(prevEnd);
      }
    }
  });

  it("минулі записи completed, майбутні confirmed/pending", () => {
    const appts = appointmentsSeed("2026-10-14", new Date("2026-10-14T09:00:00Z")); // 12:00 за Києвом
    for (const a of appts) {
      if (a.startsAt < "2026-10-14T12:00") expect(a.status).toBe("completed");
      else expect(["confirmed", "pending"]).toContain(a.status);
    }
  });

  it("відсутності й закриття відносні до сьогодні", () => {
    const a1 = absencesSeed("2026-10-14");
    const a2 = absencesSeed("2026-11-20");
    expect(a1.find((x) => x.id === "ab1")!.dateFrom).toBe("2026-10-17");
    expect(a2.find((x) => x.id === "ab1")!.dateFrom).toBe("2026-11-23");
    expect(closuresSeed("2026-10-14").find((c) => c.id === "cl1")!.dateFrom).toBe("2026-10-16");
  });
});
