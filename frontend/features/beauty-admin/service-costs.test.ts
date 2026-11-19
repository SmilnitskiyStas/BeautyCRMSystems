import { describe, expect, it } from "vitest";
import { costSummary, lossWarning, parseDecimal, toFormValues, validateCostForm } from "./service-costs";

describe("валідація редактора собівартості (§24.6.1)", () => {
  const ok = (cost: string, percent = "") => validateCostForm({ cost, percent });
  it("приймає 0, кому й крапку, до 2 знаків", () => {
    expect(ok("0").value).toEqual({ costAmount: 0, masterPercent: null });
    expect(ok("120,50", "33.33").value).toEqual({ costAmount: 120.5, masterPercent: 33.33 });
    expect(ok("1000000", "100").value).toEqual({ costAmount: 1_000_000, masterPercent: 100 });
    expect(ok(" 12 ", " 0 ").value).toEqual({ costAmount: 12, masterPercent: 0 });
  });
  it("собівартість: порожня, від'ємна, > 1 000 000, 3 знаки, не число", () => {
    expect(ok("").errors.cost).toMatch(/Вкажіть/);
    expect(ok("-1").errors.cost).toMatch(/від’ємною/);
    expect(ok("1000000,01").errors.cost).toMatch(/1 000 000/);
    expect(ok("1,234").errors.cost).toMatch(/2 знаків/);
    expect(ok("abc").errors.cost).toMatch(/число/);
    expect(ok("1e3").errors.cost).toMatch(/число/);
    expect(ok("").value).toBeUndefined();
  });
  it("відсоток: необов'язковий; 0..100; 2 знаки; помилка блокує значення", () => {
    expect(ok("10", "").errors).toEqual({});
    expect(ok("10", "101").errors.percent).toMatch(/100/);
    expect(ok("10", "-0,01").errors.percent).toMatch(/від’ємним/);
    expect(ok("10", "1,234").errors.percent).toMatch(/2 знаків/);
    expect(ok("10", "x").errors.percent).toMatch(/число/);
    expect(ok("10", "101").value).toBeUndefined();
  });
  it("parseDecimal і початкові значення форми", () => {
    expect(parseDecimal("")).toBeNull();
    expect(parseDecimal("1,5")).toBe(1.5);
    expect(parseDecimal("1 5")).toBe(15);
    expect(parseDecimal("a")).toBe("invalid");
    expect(toFormValues({ costAmount: 120.5, masterPercent: null })).toEqual({ cost: "120,5", percent: "" });
    expect(toFormValues(undefined)).toEqual({ cost: "", percent: "" });
  });
  it("попередження: собівартість + виплата > ціни", () => {
    expect(lossWarning(500, { cost: "300", percent: "50" })).toMatch(/збитковим/);
    expect(lossWarning(500, { cost: "200", percent: "50" })).toBeNull();
    expect(lossWarning(undefined, { cost: "9999", percent: "" })).toBeNull();
    expect(lossWarning(500, { cost: "abc", percent: "" })).toBeNull();
  });
  it("підпис списку", () => {
    expect(costSummary({ costAmount: 220, masterPercent: 40 })).toBe("220 ₴ · майстру 40%");
    expect(costSummary({ costAmount: 220, masterPercent: null })).toBe("220 ₴");
    expect(costSummary(undefined)).toBe("не задано");
  });
});
