import { describe, expect, it } from "vitest";
import {
  canSeeClosureReason,
  closedDayForMaster,
  closedDayInfo,
  closedDayLabel,
  isLocationClosedOn,
  localDateIn,
  validateClosure,
  weekdayOf,
} from "./closed-days";

describe("weekdayOf", () => {
  it("визначає день тижня календарної дати", () => {
    expect(weekdayOf("2026-10-05")).toBe("mon");
    expect(weekdayOf("2026-10-10")).toBe("sat");
    expect(weekdayOf("2026-10-11")).toBe("sun");
    expect(weekdayOf("2026-10-11T22:30")).toBe("sun");
  });
});

describe("localDateIn (часова зона закладу)", () => {
  it("момент 22:30Z у Києві (UTC+3 восени) - уже наступна календарна дата", () => {
    expect(localDateIn("2026-10-10T22:30:00Z", "Europe/Kyiv")).toBe("2026-10-11");
    expect(localDateIn("2026-10-10T22:30:00Z", "UTC")).toBe("2026-10-10");
  });
  it("зимовий час (UTC+2): 22:30Z -> наступна дата, 21:30Z -> та сама", () => {
    expect(localDateIn("2026-12-10T22:30:00Z", "Europe/Kyiv")).toBe("2026-12-11");
    expect(localDateIn("2026-12-10T21:30:00Z", "Europe/Kyiv")).toBe("2026-12-10");
  });
  it("зсув у рядку враховується, зона інша - інша дата", () => {
    expect(localDateIn("2026-10-10T23:30:00+03:00", "America/New_York")).toBe("2026-10-10");
    expect(localDateIn("2026-10-11T01:30:00+03:00", "America/New_York")).toBe("2026-10-10");
  });
  it("локальний рядок без зсуву береться як є; невалідна зона не падає", () => {
    expect(localDateIn("2026-10-11T00:15")).toBe("2026-10-11");
    expect(localDateIn("2026-10-10T22:30:00Z", "Not/AZone")).toBe("2026-10-10");
  });
  it("запис о 22:30Z у закритий (за Києвом) день тижня потрапляє на цей день", () => {
    const date = localDateIn("2026-10-10T22:30:00Z", "Europe/Kyiv");
    expect(isLocationClosedOn(date, { closedWeekdays: ["sun"] })).toBe(true);
    expect(isLocationClosedOn(localDateIn("2026-10-10T22:30:00Z", "UTC"), { closedWeekdays: ["sun"] })).toBe(false);
  });
});

describe("closedDayInfo: тиждень + дата", () => {
  const rules = {
    closedWeekdays: ["sun" as const],
    closures: [{ dateFrom: "2026-10-14", dateTo: "2026-10-16", reason: "Ремонт" }],
  };
  it("щотижневий вихідний", () => {
    expect(closedDayInfo("2026-10-11", rules)).toEqual({ date: "2026-10-11", source: "weekday" });
  });
  it("закриття за датою включно з межами, з причиною", () => {
    expect(closedDayInfo("2026-10-14", rules)).toMatchObject({ source: "closure", reason: "Ремонт" });
    expect(closedDayInfo("2026-10-16", rules)).toMatchObject({ source: "closure" });
    expect(closedDayInfo("2026-10-13", rules)).toBeNull();
    expect(closedDayInfo("2026-10-17", rules)).toBeNull();
  });
  it("закриття має пріоритет над вихідним днем тижня (несе причину)", () => {
    const r = { closedWeekdays: ["sun" as const], closures: [{ dateFrom: "2026-10-11", dateTo: "2026-10-11", reason: "Свято" }] };
    expect(closedDayInfo("2026-10-11", r)).toMatchObject({ source: "closure", reason: "Свято" });
  });
  it("за замовчуванням заклад працює 7 днів", () => {
    expect(isLocationClosedOn("2026-10-11", {})).toBe(false);
    expect(isLocationClosedOn("2026-10-11", { closedWeekdays: [], closures: [] })).toBe(false);
  });
});

describe("closedDayForMaster", () => {
  const hours = { mon: [{ from: "09:00", to: "18:00" }], sun: [{ from: "10:00", to: "15:00" }] };
  it("закритий, коли закриті всі заклади, де майстер працює в цей день тижня", () => {
    const rules = [{ closedWeekdays: ["sun" as const], workingHours: hours }];
    expect(closedDayForMaster("2026-10-11", rules)).toMatchObject({ source: "weekday" });
    expect(closedDayForMaster("2026-10-12", rules)).toBeNull();
  });
  it("інший заклад майстра відкритий у цей день - день не закритий", () => {
    const rules = [
      { closedWeekdays: ["sun" as const], workingHours: hours },
      { closedWeekdays: [], workingHours: { sun: [{ from: "10:00", to: "14:00" }] } },
    ];
    expect(closedDayForMaster("2026-10-11", rules)).toBeNull();
  });
  it("заклад, де майстер у цей день не працює, не рахується", () => {
    const rules = [
      { closedWeekdays: ["sun" as const], workingHours: hours },
      { closedWeekdays: [], workingHours: { mon: [{ from: "09:00", to: "18:00" }] } },
    ];
    expect(closedDayForMaster("2026-10-11", rules)).toMatchObject({ source: "weekday" });
  });
  it("без графіка в цей день дивимось на всі заклади майстра; без закладів - не закритий", () => {
    expect(closedDayForMaster("2026-10-11", [{ closedWeekdays: ["sun"], workingHours: {} }])).not.toBeNull();
    expect(closedDayForMaster("2026-10-11", [])).toBeNull();
  });
  it("закриття за датою перекриває графік майстра", () => {
    const rules = [{ workingHours: hours, closures: [{ dateFrom: "2026-10-12", dateTo: "2026-10-12", reason: "Ремонт" }] }];
    expect(closedDayForMaster("2026-10-12", rules)).toMatchObject({ source: "closure", reason: "Ремонт" });
  });
});

describe("причина закриття за роллю", () => {
  it("причину бачать лише owner/admin", () => {
    expect(canSeeClosureReason("owner")).toBe(true);
    expect(canSeeClosureReason("admin")).toBe(true);
    expect(canSeeClosureReason("specialist")).toBe(false);
  });
  it("підпис колонки: причина лише для керівників і лише для закриття за датою", () => {
    const c = { date: "2026-10-14", source: "closure" as const, reason: "Ремонт" };
    expect(closedDayLabel(c, true)).toBe("Вихідний: Ремонт");
    expect(closedDayLabel(c, false)).toBe("Вихідний");
    expect(closedDayLabel({ date: "2026-10-11", source: "weekday" }, true)).toBe("Вихідний");
  });
});

describe("validateClosure", () => {
  it("коректний діапазон", () => {
    expect(validateClosure({ dateFrom: "2026-10-14", dateTo: "2026-10-14", reason: "x" })).toBeNull();
  });
  it("кінець раніше початку, порожні дати, період понад 366 днів, причина понад 200", () => {
    expect(validateClosure({ dateFrom: "2026-10-15", dateTo: "2026-10-14" })).toMatch(/раніше/);
    expect(validateClosure({ dateFrom: "", dateTo: "" })).toMatch(/дати/);
    expect(validateClosure({ dateFrom: "2026-01-01", dateTo: "2027-01-02" })).toMatch(/366/);
    expect(validateClosure({ dateFrom: "2026-01-01", dateTo: "2026-12-31" })).toBeNull();
    expect(validateClosure({ dateFrom: "2026-10-14", dateTo: "2026-10-14", reason: "я".repeat(201) })).toMatch(/200/);
  });
});
