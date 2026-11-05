import { describe, expect, it } from "vitest";
import { todayInZone } from "../../beauty-shared/zone-time";
import {
  daysInRange,
  isValidYmd,
  parseRangeParams,
  presetRange,
  previousRange,
  rangeLabel,
  referenceZone,
  resolveRange,
  serializeRangeParams,
  validateRange,
} from "./ranges";

describe("пресети (§19.1) у зоні мережі", () => {
  const T = "2026-10-24";
  it("Сьогодні / 7 / 30 днів", () => {
    expect(presetRange("today", T)).toEqual({ from: "2026-10-24", to: "2026-10-24" });
    expect(presetRange("7d", T)).toEqual({ from: "2026-10-18", to: "2026-10-24" });
    expect(presetRange("30d", T)).toEqual({ from: "2026-09-25", to: "2026-10-24" });
    expect(daysInRange(presetRange("30d", T))).toBe(30);
  });
  it("поточний місяць: з 1-го числа", () => {
    expect(presetRange("month", T)).toEqual({ from: "2026-10-01", to: "2026-10-24" });
    expect(presetRange("month", "2026-10-01")).toEqual({ from: "2026-10-01", to: "2026-10-01" });
  });
  it("минулий місяць: березень для квітня, лютий 28/29 днів, грудень для січня", () => {
    expect(presetRange("prev_month", "2026-04-10")).toEqual({ from: "2026-03-01", to: "2026-03-31" });
    expect(daysInRange(presetRange("prev_month", "2026-03-05"))).toBe(28);
    expect(daysInRange(presetRange("prev_month", "2028-03-05"))).toBe(29);
    expect(presetRange("prev_month", "2026-01-15")).toEqual({ from: "2025-12-01", to: "2025-12-31" });
  });
  it("квартал: з 1-го дня календарного кварталу, межі кварталів", () => {
    expect(presetRange("quarter", "2026-10-24").from).toBe("2026-10-01");
    expect(presetRange("quarter", "2026-04-01")).toEqual({ from: "2026-04-01", to: "2026-04-01" });
    expect(presetRange("quarter", "2026-03-31").from).toBe("2026-01-01");
    expect(presetRange("quarter", "2026-08-15").from).toBe("2026-07-01");
    expect(presetRange("quarter", "2026-12-31").from).toBe("2026-10-01");
  });
  it("рік: з 1 січня до сьогодні (1 січня = 1 день)", () => {
    expect(presetRange("year", T)).toEqual({ from: "2026-01-01", to: T });
    expect(presetRange("year", "2026-01-01")).toEqual({ from: "2026-01-01", to: "2026-01-01" });
    expect(daysInRange(presetRange("year", "2026-12-31"))).toBe(365);
    expect(daysInRange(presetRange("year", "2028-12-31"))).toBe(366);
  });
});

describe("«сьогодні» береться в зоні мережі, а не в UTC/браузері (межа доби, DST)", () => {
  it("22:30 UTC 31.03 - уже 1 квітня в Києві (літній час): інший квартал", () => {
    const now = new Date("2026-03-31T22:30:00Z");
    expect(todayInZone("UTC", now)).toBe("2026-03-31");
    expect(presetRange("quarter", todayInZone("UTC", now)).from).toBe("2026-01-01");
    expect(todayInZone("Europe/Kyiv", now)).toBe("2026-04-01");
    expect(presetRange("quarter", todayInZone("Europe/Kyiv", now)).from).toBe("2026-04-01");
  });
  it("Окленд випереджає: 30.09 22:00 UTC = 1 жовтня там", () => {
    const now = new Date("2026-09-30T22:00:00Z");
    expect(presetRange("month", todayInZone("Pacific/Auckland", now))).toEqual({ from: "2026-10-01", to: "2026-10-01" });
  });
  it("доба переходу на зимовий час (25.10.2026, 25 год): 7 днів = рівно 7 календарних днів", () => {
    const t = todayInZone("Europe/Kyiv", new Date("2026-10-25T10:00:00Z"));
    expect(t).toBe("2026-10-25");
    expect(presetRange("7d", t)).toEqual({ from: "2026-10-19", to: "2026-10-25" });
    const spring = todayInZone("Europe/Kyiv", new Date("2026-03-29T12:00:00Z"));
    expect(presetRange("7d", spring)).toEqual({ from: "2026-03-23", to: "2026-03-29" });
  });
});

describe("валідація власного діапазону (дзеркало сервера)", () => {
  it("валідні межі: 1 день і рівно 366 днів", () => {
    expect(validateRange("2026-10-24", "2026-10-24")).toBeNull();
    expect(validateRange("2025-10-24", "2026-10-24")).toBeNull(); // 366 днів включно
  });
  it("367 днів - помилка з кількістю", () => {
    expect(validateRange("2025-10-23", "2026-10-24")).toMatch(/366/);
    expect(validateRange("2025-10-23", "2026-10-24")).toMatch(/367/);
  });
  it("from > to", () => {
    expect(validateRange("2026-10-25", "2026-10-24")).toMatch(/не може бути пізніше/);
  });
  it("порожні, неіснуючі дати й рік поза 2000..2100", () => {
    expect(validateRange("", "2026-10-24")).toMatch(/Вкажіть початок/);
    expect(validateRange("2026-02-30", "2026-03-01")).toMatch(/коректні/);
    expect(validateRange("2026-1-1", "2026-03-01")).toMatch(/коректні/);
    expect(validateRange("1999-12-31", "2000-01-02")).toMatch(/2000/);
    expect(validateRange("2100-12-30", "2101-01-02")).toMatch(/2100/);
  });
  it("isValidYmd: високосний день", () => {
    expect(isValidYmd("2028-02-29")).toBe(true);
    expect(isValidYmd("2026-02-29")).toBe(false);
  });
  it("resolveRange: неприпустимий власний діапазон = null", () => {
    expect(resolveRange({ preset: "custom", custom: { from: "2026-10-25", to: "2026-10-01" }, compare: false }, "2026-10-24")).toBeNull();
    expect(resolveRange({ preset: "custom", custom: null, compare: false }, "2026-10-24")).toBeNull();
    expect(resolveRange({ preset: "custom", custom: { from: "2026-10-01", to: "2026-10-05" }, compare: false }, "2026-10-24")).toEqual({ from: "2026-10-01", to: "2026-10-05" });
  });
});

describe("попередній період (§19.3)", () => {
  it("01.10-24.10 (24 дні) -> 07.09-30.09", () => {
    expect(previousRange({ from: "2026-10-01", to: "2026-10-24" })).toEqual({ from: "2026-09-07", to: "2026-09-30" });
  });
  it("1 день -> попередній день", () => {
    expect(previousRange({ from: "2026-10-24", to: "2026-10-24" })).toEqual({ from: "2026-10-23", to: "2026-10-23" });
  });
  it("через межу року", () => {
    expect(previousRange({ from: "2026-01-01", to: "2026-01-10" })).toEqual({ from: "2025-12-22", to: "2025-12-31" });
  });
});

describe("стан в URL", () => {
  it("за замовчуванням порожній query й дефолт 30d", () => {
    expect(serializeRangeParams({ preset: "30d", custom: null, compare: false })).toBe("");
    expect(parseRangeParams(new URLSearchParams(""))).toEqual({ preset: "30d", custom: null, compare: false });
  });
  it("round-trip пресет + compare", () => {
    const s = { preset: "prev_month" as const, custom: null, compare: true };
    expect(serializeRangeParams(s)).toBe("range=prev_month&compare=1");
    expect(parseRangeParams(new URLSearchParams(serializeRangeParams(s)))).toEqual(s);
  });
  it("round-trip власного діапазону; невідомий range -> 30d", () => {
    const s = { preset: "custom" as const, custom: { from: "2026-10-01", to: "2026-10-05" }, compare: false };
    expect(parseRangeParams(new URLSearchParams(serializeRangeParams(s)))).toEqual(s);
    expect(parseRangeParams(new URLSearchParams("range=bogus")).preset).toBe("30d");
  });
});

describe("підписи й еталонна зона", () => {
  it("rangeLabel показує реальні дати й кількість днів", () => {
    expect(rangeLabel({ from: "2026-09-25", to: "2026-10-24" })).toBe("25 вересня 2026 – 24 жовтня 2026 · 30 днів");
    expect(rangeLabel({ from: "2026-10-24", to: "2026-10-24" })).toBe("24 жовтня 2026 · 1 день");
  });
  it("referenceZone: перший активний за назвою, далі за id; без активних UTC", () => {
    expect(
      referenceZone([
        { id: "2", name: "Поділ", timezone: "Europe/Kyiv" },
        { id: "1", name: "Аркада", timezone: "Pacific/Auckland" },
        { id: "0", name: "А", timezone: "America/New_York", isActive: false },
      ]),
    ).toBe("Pacific/Auckland");
    expect(referenceZone([])).toBe("UTC");
  });
});
