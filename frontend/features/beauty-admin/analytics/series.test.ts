import { describe, expect, it } from "vitest";
import { pointFullLabel, pointLabel, thousands } from "./series";

describe("підписи осей за гранулярністю", () => {
  it("day: дд.мм", () => {
    expect(pointLabel({ from: "2026-10-05", to: "2026-10-05" }, "day")).toBe("05.10");
  });
  it("week: діапазон, обрізаний тиждень з однією датою", () => {
    expect(pointLabel({ from: "2026-10-19", to: "2026-10-25" }, "week")).toBe("19.10–25.10");
    expect(pointLabel({ from: "2026-10-24", to: "2026-10-24" }, "week")).toBe("24.10");
  });
  it("month: коротка назва й рік; повний підпис", () => {
    expect(pointLabel({ from: "2026-10-01", to: "2026-10-31" }, "month")).toMatch(/^жовт\.? 2026$/);
    expect(pointFullLabel({ from: "2026-10-01", to: "2026-10-31" }, "month")).toBe("жовтень 2026");
  });
  it("тисячі гривень", () => {
    expect(thousands(4200)).toBe("4,2");
    expect(thousands(38000)).toBe("38");
    expect(thousands(0)).toBe("0");
  });
});
