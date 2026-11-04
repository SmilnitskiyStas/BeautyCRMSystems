import { beforeEach, describe, expect, it } from "vitest";
import { BeautyApiError } from "@/features/beauty-auth/errors";
import { newProgress, runCreateService } from "../service-create";
import { mockBeautyApi } from "./mock-client";

beforeEach(() => {
  process.env.NEXT_PUBLIC_MOCK_ROLE = "owner";
});

describe("mock: створення послуги", () => {
  it("послуга з'являється в каталозі, прайсі й собівартості; ціна мережі оновлюється", async () => {
    const created = await mockBeautyApi.createService({ name: " Тест-послуга ", description: "", category: "", durationMinutes: 40, isActive: true });
    expect(created).toMatchObject({ name: "Тест-послуга", category: null, durationMinutes: 40 });
    await mockBeautyApi.setServicePrice(created.id, 321);
    expect((await mockBeautyApi.getServices()).some((s) => s.id === created.id)).toBe(true);
    expect((await mockBeautyApi.getPriceList()).find((p) => p.serviceId === created.id)).toMatchObject({ price: 321, durationMinutes: 40 });
    expect((await mockBeautyApi.getServiceCosts()).some((r) => r.serviceId === created.id)).toBe(true);
  });

  it("ліміти бекенду: назва 1..200, тривалість 5..720, ціна 0..1 000 000", async () => {
    const mk = (name: string, durationMinutes: number) => ({ name, description: "", category: "", durationMinutes, isActive: true });
    await expect(mockBeautyApi.createService(mk("   ", 30))).rejects.toMatchObject({ status: 422, code: "invalid_name" });
    await expect(mockBeautyApi.createService(mk("x".repeat(201), 30))).rejects.toMatchObject({ code: "invalid_name" });
    await expect(mockBeautyApi.createService(mk("Ліміт", 4))).rejects.toMatchObject({ code: "invalid_duration" });
    await expect(mockBeautyApi.createService(mk("Ліміт", 721))).rejects.toMatchObject({ code: "invalid_duration" });
    await expect(mockBeautyApi.createService(mk("Ліміт", 30.5))).rejects.toMatchObject({ code: "invalid_duration" });
    const lo = await mockBeautyApi.createService(mk("x".repeat(200), 5));
    const hi = await mockBeautyApi.createService(mk("Верхня межа", 720));
    expect(hi.durationMinutes).toBe(720);
    await expect(mockBeautyApi.setServicePrice(lo.id, 1_000_001)).rejects.toMatchObject({ status: 422 });
    await expect(mockBeautyApi.setServicePrice(lo.id, -1)).rejects.toMatchObject({ status: 422 });
    await mockBeautyApi.setServicePrice(lo.id, 1_000_000);
    await mockBeautyApi.setServicePrice(lo.id, 0);
  });

  it("ціна закладу: невідомий заклад -> 404 location_not_found; specialist -> 403", async () => {
    const created = await mockBeautyApi.createService({ name: "Ще одна", description: "", category: "", durationMinutes: 30, isActive: true });
    await expect(mockBeautyApi.setServicePrice(created.id, 100, "nope")).rejects.toMatchObject({ status: 404, code: "location_not_found" });
    process.env.NEXT_PUBLIC_MOCK_ROLE = "specialist";
    await expect(mockBeautyApi.createService({ name: "X", description: "", category: "", durationMinutes: 30, isActive: true })).rejects.toBeInstanceOf(BeautyApiError);
  });

  it("повний потік: майстер зберігає наявні послуги й отримує нову", async () => {
    const [master] = await mockBeautyApi.getStaff();
    const before = master.services.map((s) => s.id);
    await runCreateService(
      mockBeautyApi,
      {
        input: { name: "Потік", description: "", category: "", durationMinutes: 50, isActive: true },
        networkPrice: 700,
        locationPrices: [],
        specialists: [{ id: master.id, name: master.name }],
      },
      newProgress(),
    );
    const after = (await mockBeautyApi.getStaffProfile(master.id))!.services.map((s) => s.id);
    expect(after).toEqual(expect.arrayContaining(before));
    expect(after.length).toBe(before.length + 1);
  });
});
