import { describe, expect, it } from "vitest";
import { hasWorkingDays, normalizeHours, summarizeHours, validateDay, validateHours } from "./working-hours";

describe("validateDay", () => {
  it("порожній день або відсутній - валідний (вихідний)", () => {
    expect(validateDay(undefined)).toBeNull();
    expect(validateDay([])).toBeNull();
  });
  it("коректний інтервал проходить", () => {
    expect(validateDay([{ from: "09:00", to: "18:00" }])).toBeNull();
  });
  it("початок має бути раніше за кінець", () => {
    expect(validateDay([{ from: "18:00", to: "09:00" }])).toMatch(/раніше/);
    expect(validateDay([{ from: "09:00", to: "09:00" }])).toMatch(/раніше/);
  });
  it("некоректний формат часу", () => {
    expect(validateDay([{ from: "9:00", to: "18:00" }])).toMatch(/ГГ:ХХ/);
    expect(validateDay([{ from: "09:00", to: "24:00" }])).toMatch(/ГГ:ХХ/);
    expect(validateDay([{ from: "", to: "18:00" }])).toMatch(/ГГ:ХХ/);
  });
  it("інтервали не перетинаються, дотик меж дозволений", () => {
    expect(validateDay([{ from: "09:00", to: "13:00" }, { from: "12:00", to: "18:00" }])).toMatch(/перетин/);
    expect(validateDay([{ from: "09:00", to: "13:00" }, { from: "13:00", to: "18:00" }])).toBeNull();
  });
  it("порядок введення не важливий для перевірки перетину", () => {
    expect(validateDay([{ from: "14:00", to: "18:00" }, { from: "09:00", to: "15:00" }])).toMatch(/перетин/);
  });
  it("обмежує кількість інтервалів на день", () => {
    const many = Array.from({ length: 5 }, (_, i) => ({ from: `${String(8 + i * 2).padStart(2, "0")}:00`, to: `${String(9 + i * 2).padStart(2, "0")}:00` }));
    expect(validateDay(many)).toMatch(/Не більше/);
  });
});

describe("validateHours / normalizeHours", () => {
  it("збирає помилки по днях", () => {
    const errors = validateHours({ mon: [{ from: "10:00", to: "09:00" }], tue: [{ from: "09:00", to: "18:00" }] });
    expect(Object.keys(errors)).toEqual(["mon"]);
  });
  it("normalize прибирає порожні дні й сортує інтервали", () => {
    const out = normalizeHours({ mon: [{ from: "14:00", to: "18:00" }, { from: "09:00", to: "12:00" }], tue: [] });
    expect(out).toEqual({ mon: [{ from: "09:00", to: "12:00" }, { from: "14:00", to: "18:00" }] });
  });
  it("hasWorkingDays і summarizeHours", () => {
    expect(hasWorkingDays({})).toBe(false);
    expect(summarizeHours({})).toBe("Немає робочих днів");
    expect(summarizeHours({ mon: [{ from: "09:00", to: "18:00" }] })).toBe("Пн 09:00–18:00");
  });
});
