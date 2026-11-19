import { describe, expect, it, vi } from "vitest";
import { hasErrors, newProgress, parsePrice, PartialCreateError, runCreateService, validateServiceForm, type ServiceCreatePlan, type ServiceFormValues } from "./service-create";

describe("parsePrice", () => {
  it.each([
    ["500", 500],
    ["0", 0],
    ["499,50", 499.5],
    ["499.5", 499.5],
    [" 1 000 ", 1000],
    ["1000000", 1_000_000],
  ])("приймає %j -> %d", (raw, n) => expect(parsePrice(raw)).toBe(n));

  it.each(["", "  ", "-5", "-0", "abc", "NaN", "Infinity", "1e3", "1,234", "1.2.3", "5,", ",5", "1000001"])("відхиляє %j", (raw) =>
    expect(parsePrice(raw)).toBeNull(),
  );
});

const ok: ServiceFormValues = { name: "Стрижка", category: "", duration: "45", description: "", price: "500", locationPrices: {} };

describe("validateServiceForm", () => {
  it("коректна форма без помилок", () => expect(hasErrors(validateServiceForm(ok))).toBe(false));
  it("порожня/пробільна назва", () => expect(validateServiceForm({ ...ok, name: "   " }).name).toBeTruthy());
  it.each(["", "0", "0.5", "45,5", "-10", "abc", "4", "721", "1440"])("тривалість %j некоректна", (d) =>
    expect(validateServiceForm({ ...ok, duration: d }).duration).toBeTruthy(),
  );
  it("тривалість 5 і 720 допустимі (межі бекенду)", () => {
    expect(validateServiceForm({ ...ok, duration: "5" }).duration).toBeUndefined();
    expect(validateServiceForm({ ...ok, duration: "720" }).duration).toBeUndefined();
  });
  it.each(["", "-1", "NaN", "abc"])("мережева ціна %j некоректна", (p) => expect(validateServiceForm({ ...ok, price: p }).price).toBeTruthy());
  it("ціна 0 з комою/крапкою допустима", () => {
    expect(validateServiceForm({ ...ok, price: "0" }).price).toBeUndefined();
    expect(validateServiceForm({ ...ok, price: "12,5" }).price).toBeUndefined();
  });
  it("ціна закладу: порожня ок, некоректна — помилка саме для цього закладу", () => {
    const e = validateServiceForm({ ...ok, locationPrices: { L1: "", L2: "-3", L3: "10,5" } });
    expect(Object.keys(e.locationPrices)).toEqual(["L2"]);
    expect(hasErrors(e)).toBe(true);
  });
});

describe("runCreateService: часткові збої та повтор", () => {
  const plan: ServiceCreatePlan = {
    input: { name: "X", description: "", category: "", durationMinutes: 30, isActive: true },
    networkPrice: 100,
    locationPrices: [
      { locationId: "L1", locationName: "Один", price: 110 },
      { locationId: "L2", locationName: "Два", price: 120 },
    ],
    specialists: [
      { id: "m1", name: "Анна" },
      { id: "m2", name: "Іра" },
    ],
  };
  const mk = () => {
    const state = { failLoc: "L2" as string | null, failSpec: "m2" as string | null };
    const api = {
      createService: vi.fn(async () => ({ id: "new" }) as never),
      setServicePrice: vi.fn(async (_id: string, _p: number, loc?: string | null) => {
        if (loc && loc === state.failLoc) throw new Error("price boom");
      }),
      getStaffProfile: vi.fn(async (id: string) => ({ id, services: [{ id: "old-" + id, name: "o" }] }) as never),
      setStaffServices: vi.fn(async (id: string) => {
        if (id === state.failSpec) throw new Error("spec boom");
      }),
    };
    return { api, state };
  };

  it("збій на ціні закладу: повтор виконує лише залишок, без дубля послуги", async () => {
    const { api, state } = mk();
    const progress = newProgress();
    await expect(runCreateService(api, plan, progress)).rejects.toBeInstanceOf(PartialCreateError);
    expect(progress.locationsDone).toEqual(["L1"]);
    state.failLoc = null;
    state.failSpec = null;
    await runCreateService(api, plan, progress);
    expect(api.createService).toHaveBeenCalledTimes(1);
    // мережева 1 + L1 1 + L2 2 (збій + повтор)
    expect(api.setServicePrice).toHaveBeenCalledTimes(4);
    expect(api.setStaffServices).toHaveBeenCalledTimes(2);
  });

  it("збій на другому майстрі: перший не перепризначається, merge зберігає старі послуги", async () => {
    const { api, state } = mk();
    state.failLoc = null;
    const progress = newProgress();
    await expect(runCreateService(api, plan, progress)).rejects.toMatchObject({ name: "PartialCreateError" });
    expect(progress.specialistsDone).toEqual(["m1"]);
    expect(api.setStaffServices).toHaveBeenNthCalledWith(1, "m1", ["old-m1", "new"]);
    state.failSpec = null;
    await runCreateService(api, plan, progress);
    expect(api.createService).toHaveBeenCalledTimes(1);
    expect(api.setStaffServices).toHaveBeenCalledTimes(3); // m1, m2(збій), m2
    expect(api.setStaffServices).toHaveBeenLastCalledWith("m2", ["old-m2", "new"]);
  });

  it("майстер уже має нову послугу -> без дубля id; без майстрів і закладів — лише POST + мережева ціна", async () => {
    const { api } = mk();
    api.getStaffProfile.mockResolvedValueOnce({ id: "m1", services: [{ id: "new", name: "n" }] } as never);
    await runCreateService(api, { ...plan, locationPrices: [], specialists: [plan.specialists[0]] }, newProgress());
    expect(api.setStaffServices).toHaveBeenCalledWith("m1", ["new"]);

    const b = mk();
    await runCreateService(b.api, { ...plan, locationPrices: [], specialists: [] }, newProgress());
    expect(b.api.setServicePrice).toHaveBeenCalledTimes(1);
    expect(b.api.getStaffProfile).not.toHaveBeenCalled();
  });

  it("пропущений майстер (specialistsSkipped) не призначається повторно; збій несе specialistId", async () => {
    const { api, state } = mk();
    state.failLoc = null;
    const progress = newProgress();
    const err = await runCreateService(api, plan, progress).catch((e) => e);
    expect(err).toBeInstanceOf(PartialCreateError);
    expect(err.specialistId).toBe("m2");
    progress.specialistsSkipped.push("m2");
    await runCreateService(api, plan, progress);
    expect(api.setStaffServices).toHaveBeenCalledTimes(2); // m1, m2(збій); повтор m2 пропущено
  });

  it("майстра не знайдено -> PartialCreateError", async () => {
    const { api } = mk();
    api.getStaffProfile.mockResolvedValueOnce(null as never);
    await expect(runCreateService(api, { ...plan, locationPrices: [] }, newProgress())).rejects.toBeInstanceOf(PartialCreateError);
  });
});
