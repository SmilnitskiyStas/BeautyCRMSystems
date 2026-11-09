import type { DayAvailability } from "@beauty/booking-core";
import { buildMonthGrid, cellSelectable, firstAvailable, monthBounds, shiftMonth } from "../calendar";

const days: DayAvailability[] = [
  { date: "2026-11-09", status: "past", freeSlots: 0 },
  { date: "2026-11-10", status: "full", freeSlots: 0 },
  { date: "2026-11-11", status: "closed", freeSlots: 0 },
  { date: "2026-11-12", status: "available", freeSlots: 5 },
  { date: "2026-11-13", status: "available", freeSlots: 2 },
  { date: "2026-12-01", status: "available", freeSlots: 1 },
];

describe("calendar", () => {
  const grid = buildMonthGrid("2026-11", days);
  const cells = grid.flat();
  const at = (iso: string) => cells.find((c) => c.iso === iso && c.inMonth)!;

  it("сітка пн-нд, повні тижні (листопад 2026 починається з неділі)", () => {
    expect(grid.every((w) => w.length === 7)).toBe(true);
    expect(grid[0][6].iso).toBe("2026-11-01"); // неділя - остання колонка
    expect(grid[0][0].inMonth).toBe(false);
    expect(grid).toHaveLength(6);
  });
  it("стани клітинок із availability", () => {
    expect(at("2026-11-12")).toMatchObject({ state: "available", selectable: true, freeSlots: 5 });
    expect(at("2026-11-10")).toMatchObject({ state: "full", selectable: false });
    expect(at("2026-11-11")).toMatchObject({ state: "closed", selectable: false });
    expect(at("2026-11-09")).toMatchObject({ state: "past", selectable: false });
    expect(at("2026-11-20")).toMatchObject({ state: "outside", selectable: false });
  });
  it("відмова вибрати full/closed/past/outside", () => {
    expect((["full", "closed", "past", "outside"] as const).some(cellSelectable)).toBe(false);
    expect(cellSelectable("available")).toBe(true);
  });
  it("firstAvailable ігнорує full/closed/past; порожньо -> null", () => {
    expect(firstAvailable(days)).toBe("2026-11-12");
    expect(firstAvailable(days.filter((d) => d.status !== "available"))).toBeNull();
  });
  it("місяці: зсув і межі", () => {
    expect(shiftMonth("2026-12", 1)).toBe("2027-01");
    expect(shiftMonth("2026-01", -1)).toBe("2025-12");
    expect(monthBounds(days)).toEqual({ first: "2026-11", last: "2026-12" });
    expect(monthBounds([])).toBeNull();
  });
});
