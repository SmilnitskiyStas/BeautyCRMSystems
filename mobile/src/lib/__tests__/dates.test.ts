import { dateOptions, longDate, todayIn } from "../dates";

describe("mobile dates (no localized Intl)", () => {
  it("longDate uses uk texts", () => {
    expect(longDate("2026-10-12")).toBe("понеділок, 12 жовтня");
  });
  it("dateOptions: labels, weekdays, closed days", () => {
    const now = new Date("2026-11-09T10:00:00Z");
    const o = dateOptions("UTC", 3, now, { closedWeekdays: ["tue"] });
    expect(o.map((x) => x.iso)).toEqual(["2026-11-09", "2026-11-10", "2026-11-11"]);
    expect(o[0]).toMatchObject({ label: "Сьогодні", weekday: "пн", closed: false });
    expect(o[1]).toMatchObject({ label: "Завтра", weekday: "вт", closed: true });
    expect(o[2].label).toBe("11 лис");
  });
  it("todayIn respects the venue timezone", () => {
    expect(todayIn("Pacific/Kiritimati", new Date("2026-11-09T12:00:00Z"))).toBe("2026-11-10");
  });
  it("todayIn falls back to the device date for an invalid zone", () => {
    const now = new Date(2026, 10, 9, 12);
    expect(todayIn("Not/AZone", now)).toBe("2026-11-09");
  });
});
