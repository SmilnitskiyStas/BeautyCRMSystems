import { describe, expect, it } from "vitest";
import {
  closedWeekdaysLabel,
  countActiveStaff,
  isInsideLocationHours,
  localTimeIn,
  locationHoursLabel,
  mapUrl,
  outsideHours,
  staffOfLocation,
} from "./location-hours";
import type { StaffMember } from "./types";

const m = (id: string, name: string, isActive: boolean, locs: string[]): StaffMember => ({
  id,
  name,
  phone: null,
  position: null,
  isActive,
  services: [],
  locations: locs.map((l) => ({ locationId: l, locationName: l, workingHours: { mon: [{ from: "10:00", to: "18:00" }] } })),
});

describe("підписи годин і вихідних", () => {
  it("не задано / задано / жодного робочого дня", () => {
    expect(locationHoursLabel({ workingHours: null })).toBe("Години не задано");
    expect(locationHoursLabel({})).toBe("Години не задано");
    expect(locationHoursLabel({ workingHours: { mon: [{ from: "09:00", to: "18:00" }] } })).toBe("Пн 09:00–18:00");
    expect(locationHoursLabel({ workingHours: {} })).toBe("Немає робочих днів");
  });
  it("вихідні", () => {
    expect(closedWeekdaysLabel(["sun", "mon"])).toBe("Вихідні: Пн, Нд");
    expect(closedWeekdaysLabel([])).toBe("Без вихідних");
    expect(closedWeekdaysLabel(undefined)).toBe("Без вихідних");
  });
});

describe("посилання на карту", () => {
  it("кодує адресу", () => {
    expect(mapUrl("вул. Хрещатик, 22 & Київ")).toBe(
      `https://www.google.com/maps/search/?api=1&query=${encodeURIComponent("вул. Хрещатик, 22 & Київ")}`,
    );
  });
  it("порожня адреса - немає посилання", () => {
    expect(mapUrl("")).toBeNull();
    expect(mapUrl("   ")).toBeNull();
    expect(mapUrl(null)).toBeNull();
  });
});

describe("спеціалісти закладу", () => {
  const all = [m("1", "Яна", true, ["c"]), m("2", "Анна", false, ["c", "p"]), m("3", "Ольга", true, ["p"]), m("4", "Богдана", true, ["c"])];
  it("фільтр за закладом, активні перші, далі за іменем", () => {
    expect(staffOfLocation(all, "c").map((s) => s.member.name)).toEqual(["Богдана", "Яна", "Анна"]);
    expect(staffOfLocation(all, "p").map((s) => s.member.id)).toEqual(["3", "2"]);
    expect(staffOfLocation(all, "zzz")).toEqual([]);
  });
  it("графік береться саме з цього закладу; кількість - активні", () => {
    expect(staffOfLocation(all, "c")[0].workingHours).toEqual({ mon: [{ from: "10:00", to: "18:00" }] });
    expect(countActiveStaff(all, "c")).toBe(2);
  });
});

describe("записи поза годинами", () => {
  const hours = {
    mon: [
      { from: "09:00", to: "13:00" },
      { from: "14:00", to: "18:00" },
    ],
  };
  it("локальний час і зона", () => {
    expect(localTimeIn("2026-10-12T09:30")).toBe("09:30");
    expect(localTimeIn("2026-10-12T07:30:00Z", "Europe/Kyiv")).toBe("10:30");
  });
  it("усередині інтервалу / у перерві / у день без інтервалів", () => {
    // 2026-10-12 - понеділок
    expect(isInsideLocationHours("2026-10-12T09:00", hours)).toBe(true);
    expect(isInsideLocationHours("2026-10-12T13:30", hours)).toBe(false);
    expect(isInsideLocationHours("2026-10-12T18:00", hours)).toBe(false);
    expect(isInsideLocationHours("2026-10-13T10:00", hours)).toBe(false);
  });
  it("outsideHours: null = обмежень немає", () => {
    const items = [{ startsAt: "2026-10-12T10:00" }, { startsAt: "2026-10-12T19:00" }];
    expect(outsideHours(items, null)).toEqual([]);
    expect(outsideHours(items, hours)).toEqual([items[1]]);
  });
});

describe("F5: знятий зв'язок майстра із закладом (isActive=false)", () => {
  const link = (locationId: string, isActive?: boolean) => ({ locationId, locationName: locationId, workingHours: {}, ...(isActive === undefined ? {} : { isActive }) });
  const m = (id: string, locations: StaffMember["locations"]): StaffMember => ({ id, name: id, phone: null, position: null, isActive: true, services: [], locations });

  it("не потрапляє в спеціалістів закладу й не рахується; відсутнє поле = активний", () => {
    const staff = [m("a", [link("c", true), link("p", false)]), m("b", [link("p")])];
    expect(staffOfLocation(staff, "p").map((s) => s.member.id)).toEqual(["b"]);
    expect(countActiveStaff(staff, "p")).toBe(1);
    expect(countActiveStaff(staff, "c")).toBe(1);
  });
});
