import { describe, expect, it } from "vitest";
import { bookableAt, buildWeekAvailability, countFreeSlots, dayAvailability, gridRange, intersect, slotStarts, subtract } from "./availability";

const h = (from: string, to: string) => [{ from, to }];
// 2026-10-26 - понеділок
const MON = "2026-10-26";

describe("інтервали", () => {
  it("перетин і різниця", () => {
    expect(intersect([{ from: 540, to: 1080 }], [{ from: 600, to: 1020 }])).toEqual([{ from: 600, to: 1020 }]);
    expect(intersect([{ from: 540, to: 600 }], [{ from: 600, to: 700 }])).toEqual([]);
    expect(subtract([{ from: 0, to: 1440 }], [{ from: 600, to: 1020 }])).toEqual([
      { from: 0, to: 600 },
      { from: 1020, to: 1440 },
    ]);
  });
});

describe("F1: доступність = графік майстра ∩ години закладу ∩ не вихідний (§18.1)", () => {
  const rules = {
    masterHours: { mon: h("09:00", "18:00"), tue: h("09:00", "18:00"), sun: h("09:00", "18:00") },
    locationHours: { mon: h("10:00", "17:00"), tue: h("10:00", "17:00"), sat: h("10:00", "14:00") },
    closedWeekdays: ["sun" as const],
    closures: [{ dateFrom: "2026-10-27", dateTo: "2026-10-27" }],
  };

  it("перетин: пн 10:00-17:00, зайве поза годинами закладу й поза графіком", () => {
    const d = dayAvailability(MON, rules);
    expect(d.state).toBe("open");
    expect(d.free).toEqual([{ from: 600, to: 1020 }]);
    expect(d.hoursOff).toEqual([
      { from: 0, to: 600 },
      { from: 1020, to: 1440 },
    ]);
    expect(d.masterOff).toEqual([]); // у годинах закладу майстер працює весь час
  });

  it("закриття за датою й вихідний тижня перекривають графік", () => {
    expect(dayAvailability("2026-10-27", rules)).toMatchObject({ state: "location_closed", free: [] });
    expect(dayAvailability("2026-11-01", rules)).toMatchObject({ state: "location_closed", free: [] }); // нд
  });

  it("день відсутній у годинах закладу - заклад не працює; відсутній у графіку - майстер не працює", () => {
    expect(dayAvailability("2026-10-28", rules)).toMatchObject({ state: "location_hours_off", free: [] }); // ср: годин закладу немає
  });

  it("сб: заклад працює 10-14, майстер не працює -> master_off", () => {
    const d = dayAvailability("2026-10-31", { ...rules, locationHours: { sat: h("10:00", "14:00") } });
    expect(d.state).toBe("master_off");
    expect(d.masterOff).toEqual([{ from: 600, to: 840 }]);
  });

  it("відсутність блокує день; години null = без обмеження", () => {
    expect(dayAvailability(MON, { ...rules, locationHours: null }, true)).toMatchObject({ state: "absence", free: [] });
    expect(dayAvailability(MON, { ...rules, locationHours: null }).free).toEqual([{ from: 540, to: 1080 }]);
  });

  it("слоти збігаються з незатіненим: 15-хв сітка від початку інтервалу майстра, слот не виходить за години закладу", () => {
    const d = dayAvailability(MON, rules);
    const starts = slotStarts(d, 90);
    expect(starts[0]).toBe(600); // 10:00 - початок інтервалу майстра 9:00 + 15-хв сітка, але заклад відкривається о 10:00
    expect(starts[starts.length - 1]).toBe(930); // 15:30 + 90 хв = 17:00
    for (const s of starts) expect(bookableAt(d, s, 90)).toBe(true);
    expect(bookableAt(d, 540, 60)).toBe(false); // 9:00 - заклад ще зачинений
    expect(bookableAt(d, 960, 90)).toBe(false); // 16:00+90 = 17:30 - вже після закриття
  });

  it("слоти не перетинають перерву між інтервалами закладу, зайняте й минулий час", () => {
    const d = dayAvailability(MON, { masterHours: { mon: h("09:00", "18:00") }, locationHours: { mon: [{ from: "09:00", to: "12:00" }, { from: "13:00", to: "18:00" }] } });
    const starts = slotStarts(d, 60);
    expect(starts).not.toContain(11 * 60 + 30); // 11:30-12:30 перетинає перерву
    expect(starts).toContain(13 * 60);
    expect(slotStarts(d, 60, { busy: [{ from: 13 * 60, to: 14 * 60 }] })).not.toContain(13 * 60 + 30);
    expect(Math.min(...slotStarts(d, 60, { minStart: 15 * 60 }))).toBe(15 * 60);
    expect(countFreeSlots([540, 570, 600, 660], 60)).toBe(3);
  });
});

describe("діапазон сітки (F1): не фіксований 9-20", () => {
  const week = (masterHours: Record<string, { from: string; to: string }[]>, locationHours: Record<string, { from: string; to: string }[]> | null = null) =>
    buildWeekAvailability(MON, { masterHours, locationHours });

  it("за графіком майстра", () => {
    const w = week({ mon: h("10:00", "19:30"), sat: h("08:00", "12:00") });
    expect([w.gridStartHour, w.gridEndHour]).toEqual([8, 20]);
  });

  it("мінімум 6 годин і межі доби", () => {
    expect(gridRange(week({ mon: h("10:00", "11:00") }).availability)).toEqual({ start: 10, end: 16 });
    expect(gridRange(week({ mon: h("20:00", "23:59") }).availability)).toEqual({ start: 18, end: 24 });
  });

  it("без графіка - за годинами закладу; зовсім без даних - 9-21", () => {
    expect([week({}, { mon: h("07:00", "15:00") }).gridStartHour, week({}, { mon: h("07:00", "15:00") }).gridEndHour]).toEqual([7, 15]);
    expect(gridRange(week({}).availability)).toEqual({ start: 9, end: 21 });
  });

  it("closedDays тижня збігаються зі станом location_closed", () => {
    const w = buildWeekAvailability(MON, {
      masterHours: { mon: h("09:00", "18:00"), tue: h("09:00", "18:00") },
      closedWeekdays: ["tue"],
    });
    expect(w.closedDays[1]).toMatchObject({ source: "weekday" });
    expect(w.availability[1].state).toBe("location_closed");
    expect(w.closedDays[0]).toBeNull();
  });
});
