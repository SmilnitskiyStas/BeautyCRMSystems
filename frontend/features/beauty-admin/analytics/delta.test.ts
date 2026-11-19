import { describe, expect, it } from "vitest";
import { computeDelta, computeShareDelta, describeDelta } from "./delta";

describe("delta (§19.3)", () => {
  it("abs і % з округленням до 0,1", () => {
    expect(computeDelta(112.5, 100)).toEqual({ abs: 12.5, pct: 12.5 });
    expect(computeDelta(200, 300)).toEqual({ abs: -100, pct: -33.3 });
  });
  it("prev = 0 -> pct null, показується «—»", () => {
    const d = computeDelta(5, 0);
    expect(d).toEqual({ abs: 5, pct: null });
    expect(describeDelta(d, "count").text).toBe("▲ — (+5)");
    expect(computeDelta(0, 0)).toEqual({ abs: 0, pct: null });
  });
  it("null/undefined -> «—» без напрямку", () => {
    expect(computeDelta(null, 3)).toBeNull();
    expect(computeDelta(3, undefined)).toBeNull();
    expect(describeDelta(null, "money")).toMatchObject({ text: "—", arrow: "", tone: "neutral" });
  });
  it("частки: лише абсолютна різниця в п.п.", () => {
    const d = computeShareDelta(0.125, 0.1);
    expect(d).toEqual({ abs: 2.5, pct: null });
    expect(describeDelta(d, "share", "down-good").text).toBe("▲ +2,5 п.п.");
    expect(computeShareDelta(0.1, 0.15)).toEqual({ abs: -5, pct: null });
    expect(computeShareDelta(null, 0.1)).toBeNull();
  });
  it("від'ємна зміна виручки: стрілка вниз, мінус і поганий тон", () => {
    const v = describeDelta(computeDelta(800, 1000), "money");
    expect(v.arrow).toBe("▼");
    expect(v.text).toBe("▼ −20% (−200 ₴)");
    expect(v.tone).toBe("bad");
    expect(v.sr).toMatch(/^Менше на 20%/);
  });
  it("для скасувань і no-show зростання - погано, зменшення - добре", () => {
    expect(describeDelta(computeDelta(12, 10), "count", "down-good").tone).toBe("bad");
    expect(describeDelta(computeDelta(8, 10), "count", "down-good").tone).toBe("good");
    expect(describeDelta(computeDelta(12, 10), "count", "up-good").tone).toBe("good");
  });
  it("нейтральна полярність і відсутність змін", () => {
    expect(describeDelta(computeDelta(12, 10), "money", "neutral").tone).toBe("neutral");
    const same = describeDelta(computeDelta(10, 10), "count");
    expect(same).toMatchObject({ arrow: "▬", tone: "neutral", sr: "Без змін" });
  });
});
