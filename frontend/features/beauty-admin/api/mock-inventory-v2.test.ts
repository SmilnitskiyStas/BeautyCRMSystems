import { describe, expect, it } from "vitest";
import { BeautyApiError } from "@/features/beauty-auth/errors";
import { DEMO_LOCATIONS } from "../../beauty-shared/demo-seed";
import { createMockInventory } from "./mock-inventory";
import { createMockInventoryV2 } from "./mock-inventory-v2";

const NOW = Date.parse("2026-10-14T09:00:00Z");
const SERVICES = [
  { id: "s1", name: "Манікюр" },
  { id: "s2", name: "Стрижка" },
];

function setup(role = "owner") {
  let r = role;
  const inv = createMockInventory({ role: () => r, locations: () => DEMO_LOCATIONS, specialistLocationIds: () => ["c"], now: () => NOW, wait: false });
  const v2 = createMockInventoryV2({ role: () => r, services: () => SERVICES, locations: () => DEMO_LOCATIONS, specialistLocationIds: () => ["c"], inventory: inv, now: () => NOW });
  return { inv, v2, setRole: (x: string) => (r = x) };
}
async function expectError(p: Promise<unknown>, status: number, code: string) {
  const e = await p.then(
    () => null,
    (x: unknown) => x,
  );
  expect(e).toBeInstanceOf(BeautyApiError);
  expect((e as BeautyApiError).status).toBe(status);
  expect((e as BeautyApiError).code).toBe(code);
  return e as BeautyApiError;
}
const pid = async (inv: ReturnType<typeof setup>["inv"], name: string) => (await inv.getInventoryProducts({ active: "all", take: 200 })).items.find((p) => p.name.startsWith(name))!.id;

describe("нестача в залишках (§32.17)", () => {
  it("сид: нестача дезінфектанта в закладі P - сальдо від'ємне, isLow за сальдо, рух isShortage", async () => {
    const { inv } = setup();
    const row = (await inv.getStock({ locationId: "p", take: 200 })).items.find((r) => r.product.name.startsWith("Дезінфікуючий"))!;
    expect(row).toMatchObject({ quantity: 0, shortageQuantity: 0.3, balance: -0.3, isLow: true });
    const sum = (await inv.getStockSummary({ take: 200 })).items.find((r) => r.product.name.startsWith("Дезінфікуючий"))!;
    expect(sum.totalShortage).toBe(0.3);
    const mv = (await inv.getMovements({ type: "service_consumption", take: 50 })).items;
    expect(mv[0]).toMatchObject({ isShortage: true, batchId: null, quantity: -0.3 });
    const ops = (await inv.getOperations({ kind: "service_consumption" })).items;
    expect(ops[0]!.appointmentId).toBeTruthy();
  });
});

describe("норми матеріалів mock (§32.16)", () => {
  it("мережевий набір і override: override замінює, порожній override дозволено, порожня мережа - ні", async () => {
    const { inv, v2 } = setup();
    const gel = await pid(inv, "Гель");
    const net = await v2.setServiceMaterials("s2", { locationId: null, lines: [{ productId: gel, quantity: 2 }] });
    expect(net.lines[0]!.quantity).toBe(2);
    await v2.setServiceMaterials("s2", { locationId: "c", lines: [] });
    const d = await v2.getServiceMaterials("s2");
    expect(d.network?.lines).toHaveLength(1);
    expect(d.locations).toEqual([expect.objectContaining({ locationId: "c", lines: [] })]);
    await expectError(v2.setServiceMaterials("s2", { locationId: null, lines: [] }), 422, "invalid_lines");
    await v2.deleteServiceMaterials("s2", "c");
    await expectError(v2.deleteServiceMaterials("s2", "c"), 404, "materials_not_found");
  });

  it("валідація: для продажу, неактивний, дубль, ціле для pcs, 404 послуга/товар/заклад", async () => {
    const { inv, v2 } = setup();
    const mask = await pid(inv, "Маска");
    const gel = await pid(inv, "Гель");
    const old = await pid(inv, "Знятий");
    const e = await expectError(v2.setServiceMaterials("s1", { locationId: null, lines: [{ productId: gel, quantity: 1 }, { productId: mask, quantity: 1 }] }), 422, "product_not_for_salon_use");
    expect(e.body?.line).toBe(1);
    await expectError(v2.setServiceMaterials("s1", { locationId: null, lines: [{ productId: old, quantity: 1 }] }), 422, "product_inactive");
    await expectError(v2.setServiceMaterials("s1", { locationId: null, lines: [{ productId: gel, quantity: 1 }, { productId: gel, quantity: 1 }] }), 422, "duplicate_line");
    await expectError(v2.setServiceMaterials("s1", { locationId: null, lines: [{ productId: gel, quantity: 1.5 }] }), 422, "invalid_quantity");
    await expectError(v2.setServiceMaterials("nope", { locationId: null, lines: [{ productId: gel, quantity: 1 }] }), 404, "service_not_found");
    await expectError(v2.setServiceMaterials("s1", { locationId: "zzz", lines: [] }), 404, "location_not_found");
    await expectError(v2.setServiceMaterials("s1", { locationId: null, lines: [{ productId: "nope", quantity: 1 }] }), 404, "product_not_found");
  });

  it("запис лише owner/admin (deputy, specialist -> 403 forbidden_role); читання дозволено", async () => {
    const { v2, setRole } = setup();
    for (const r of ["deputy", "specialist"]) {
      setRole(r);
      await expectError(v2.setServiceMaterials("s1", { locationId: "c", lines: [] }), 403, "forbidden_role");
      await expectError(v2.deleteServiceMaterials("s1", null), 403, "forbidden_role");
      expect((await v2.getServiceMaterials("s1")).serviceId).toBe("s1");
    }
  });

  it("specialist бачить лише свої заклади в override; список - лише послуги з наборами", async () => {
    const { inv, v2, setRole } = setup();
    const gel = await pid(inv, "Гель");
    await v2.setServiceMaterials("s2", { locationId: "c", lines: [{ productId: gel, quantity: 1 }] });
    await v2.setServiceMaterials("s2", { locationId: "p", lines: [] });
    setRole("specialist");
    expect((await v2.getServiceMaterials("s2")).locations.map((l) => l.locationId)).toEqual(["c"]);
    setRole("owner");
    const list = await v2.getServiceMaterialsList();
    expect(list.map((x) => x.serviceId)).toEqual(expect.arrayContaining(["s1", "s2"]));
  });
});

describe("сповіщення mock (§32.19)", () => {
  it("три види, активні = open|read, лічильник = open у scope", async () => {
    const { v2 } = setup();
    const all = (await v2.getInventoryAlerts({ status: "all", take: 200 })).items;
    expect(new Set(all.map((a) => a.kind))).toEqual(new Set(["low_stock", "expiring", "shortage"]));
    const active = (await v2.getInventoryAlerts({})).items;
    expect(active.every((a) => a.status !== "resolved")).toBe(true);
    expect(await v2.getInventoryAlertsUnreadCount()).toBe(active.filter((a) => a.status === "open").length);
    const expired = all.find((a) => a.kind === "expiring" && a.daysBucket === 0);
    expect(expired?.batch?.lotNumber).toBe("DY-0101");
  });

  it("read ідемпотентний, read-all лише open у закладі; після - лічильник 0", async () => {
    const { v2 } = setup();
    const first = (await v2.getInventoryAlerts({ status: "open" })).items[0]!;
    const r1 = await v2.markInventoryAlertRead(first.id);
    expect(r1.status).toBe("read");
    const r2 = await v2.markInventoryAlertRead(first.id);
    expect(r2.readAt).toBe(r1.readAt);
    const updated = await v2.markAllInventoryAlertsRead("p");
    expect((await v2.getInventoryAlerts({ status: "open", locationId: "p" })).items).toHaveLength(0);
    expect(updated).toBeGreaterThanOrEqual(0);
    await v2.markAllInventoryAlertsRead();
    expect(await v2.getInventoryAlertsUnreadCount()).toBe(0);
  });

  it("scope: specialist не бачить сповіщень інших закладів; 404 alert_not_found; фільтри й invalid_*", async () => {
    const { v2, setRole } = setup();
    const other = (await v2.getInventoryAlerts({ status: "all", locationId: "p", take: 200 })).items[0]!;
    setRole("specialist");
    expect((await v2.getInventoryAlerts({ status: "all", take: 200 })).items.every((a) => a.location.id === "c")).toBe(true);
    await expectError(v2.markInventoryAlertRead(other.id), 404, "alert_not_found");
    await expectError(v2.getInventoryAlerts({ locationId: "p" }), 404, "location_not_found");
    await expectError(v2.getInventoryAlerts({ status: "zzz" as never }), 422, "invalid_alert_status");
    await expectError(v2.getInventoryAlerts({ kind: "zzz" as never }), 422, "invalid_alert_kind");
  });

  it("DTO без ПД клієнтів", async () => {
    const { v2 } = setup();
    const json = JSON.stringify((await v2.getInventoryAlerts({ status: "all", take: 200 })).items);
    expect(json).not.toMatch(/clientId|clientName|phone|appointmentId|specialist/i);
  });
});
