import { beforeEach, describe, expect, it, vi } from "vitest";

type Init = { method?: string; body?: string; cache?: string; headers?: Record<string, string> };
const apiJson = vi.fn<(path: string, init?: Init) => Promise<unknown>>();
vi.mock("@/features/beauty-auth/session", () => ({
  apiJson: (path: string, init?: Init) => apiJson(path, init),
  getUser: () => ({ id: "u", role: "owner", specialistId: null }),
}));

import { httpBeautyApi } from "./http-client";

beforeEach(() => {
  apiJson.mockReset();
  apiJson.mockResolvedValue(undefined);
});
const last = () => apiJson.mock.calls[apiJson.mock.calls.length - 1]!;
const ref = { id: "p1", name: "Шампунь", unit: "pcs", purpose: "both", category: null, barcode: null, isActive: true };

describe("http §32: шляхи, методи, заголовки", () => {
  it("scope й no-store", async () => {
    apiJson.mockResolvedValueOnce({ locations: [{ id: "c", name: "Центр", timezone: "Europe/Kyiv", canManage: true, secret: 1 }], costs: "edit", canEditCatalog: true, expiringSoonDays: 14 });
    const s = await httpBeautyApi.getInventoryScope();
    expect(last()[0]).toBe("/api/beauty/inventory/scope");
    expect(last()[1]).toMatchObject({ cache: "no-store" });
    expect(s).toEqual({ locations: [{ id: "c", name: "Центр", isActive: true, timezone: "Europe/Kyiv", canManage: true }], costs: "edit", canEditCatalog: true, expiringSoonDays: 14 });
  });

  it("невідомий рівень цін -> none (fail-closed)", async () => {
    apiJson.mockResolvedValueOnce({ locations: [], costs: "root" });
    expect((await httpBeautyApi.getInventoryScope()).costs).toBe("none");
  });

  it("товари: параметри, take ≤ 200, порожні параметри відкидаються", async () => {
    apiJson.mockResolvedValueOnce({ items: [], hasMore: true, nextCursor: "abc" });
    const r = await httpBeautyApi.getInventoryProducts({ search: "шам", active: "all", category: "", cursor: "x", take: 999 });
    expect(last()[0]).toBe("/api/beauty/inventory/products?search=%D1%88%D0%B0%D0%BC&active=all&cursor=x&take=200");
    expect(r).toEqual({ items: [], hasMore: true, nextCursor: "abc" });
  });

  it("ціни †: поле є лише коли сервер його віддав (без права - відсутнє, не 0)", async () => {
    apiJson.mockResolvedValueOnce({ items: [{ id: "p1", name: "A", unit: "ml", purpose: "sale", supplier: { id: "s", name: "S" } }], hasMore: false });
    const noCost = (await httpBeautyApi.getInventoryProducts()).items[0]!;
    expect("purchasePrice" in noCost).toBe(false);
    apiJson.mockResolvedValueOnce({ items: [{ id: "p1", name: "A", unit: "ml", purpose: "sale", purchasePrice: null }], hasMore: false });
    const withCost = (await httpBeautyApi.getInventoryProducts()).items[0]!;
    expect(withCost.purchasePrice).toBeNull();
    apiJson.mockResolvedValueOnce({ items: [{ product: ref, locationId: "c", quantity: 3 }], hasMore: false, location: { id: "c", name: "Центр", timezone: "Europe/Kyiv", isActive: true } });
    const row = (await httpBeautyApi.getStock({ locationId: "c" })).items[0]!;
    expect("value" in row).toBe(false);
    expect(row).toMatchObject({ quantity: 3, isLow: false, expiredQuantity: 0 });
    apiJson.mockResolvedValueOnce({ items: [{ id: "b", product: ref, locationId: "c", quantity: 1, unitCost: 5, status: "expired", daysToExpiry: -2 }], hasMore: false });
    const b = (await httpBeautyApi.getBatches({ locationId: "c", status: "expired" })).items[0]!;
    expect(b).toMatchObject({ unitCost: 5, status: "expired", daysToExpiry: -2 });
  });

  it("залишки: locationId, фільтри й стан у query", async () => {
    apiJson.mockResolvedValueOnce({ items: [], hasMore: false, location: { id: "c", name: "Ц", timezone: "UTC", isActive: true } });
    await httpBeautyApi.getStock({ locationId: "c", state: "low", expiringWithinDays: 30, take: 50 });
    expect(last()[0]).toBe("/api/beauty/inventory/stock?locationId=c&state=low&expiringWithinDays=30&take=50");
    apiJson.mockResolvedValueOnce({ items: [], hasMore: false, locations: [{ id: "c", name: "Ц" }] });
    const sum = await httpBeautyApi.getStockSummary({ lowOnly: true });
    expect(last()[0]).toBe("/api/beauty/inventory/stock/summary?lowOnly=true");
    expect(sum.locations).toEqual([{ id: "c", name: "Ц", isActive: true }]);
  });

  it("операції: POST з Idempotency-Key і тілом без змін", async () => {
    apiJson.mockResolvedValue({ id: "op", kind: "receipt", movements: [] });
    const receipt = { locationId: "c", lines: [{ productId: "p1", quantity: 2, expiresOn: "2027-01-01" }] };
    await httpBeautyApi.createReceipt(receipt, "k-1");
    expect(last()[0]).toBe("/api/beauty/inventory/receipts");
    expect(last()[1]).toMatchObject({ method: "POST", headers: { "Idempotency-Key": "k-1" }, cache: "no-store" });
    expect(JSON.parse(last()[1]!.body!)).toEqual(receipt);
    await httpBeautyApi.createWriteOff({ locationId: "c", reason: "damaged", lines: [{ productId: "p1", quantity: 1 }] }, "k-2");
    expect(last()[0]).toBe("/api/beauty/inventory/write-offs");
    expect(last()[1]).toMatchObject({ headers: { "Idempotency-Key": "k-2" } });
    await httpBeautyApi.createTransfer({ fromLocationId: "c", toLocationId: "p", lines: [{ productId: "p1", quantity: 1, batchId: "b" }] }, "k-3");
    expect(last()[0]).toBe("/api/beauty/inventory/transfers");
    expect(last()[1]).toMatchObject({ headers: { "Idempotency-Key": "k-3" } });
  });

  it("налаштування закладу, постачальники, DELETE", async () => {
    await httpBeautyApi.setProductLocationSetting("a b", "c", { minStock: null, isEnabled: false });
    expect(last()[0]).toBe("/api/beauty/inventory/products/a%20b/locations/c");
    expect(last()[1]).toMatchObject({ method: "PUT" });
    expect(JSON.parse(last()[1]!.body!)).toEqual({ minStock: null, isEnabled: false });
    await httpBeautyApi.deleteProductLocationSetting("p1", "c");
    expect(last()[1]).toMatchObject({ method: "DELETE" });
    await httpBeautyApi.createSupplier({ name: "S" });
    expect(last()[0]).toBe("/api/beauty/inventory/suppliers");
    await httpBeautyApi.updateSupplier("s1", { name: "S" });
    expect(last()).toSatisfy((c: unknown[]) => c[0] === "/api/beauty/inventory/suppliers/s1" && (c[1] as Init).method === "PUT");
  });

  it("інвентаризація: expectedVersion у тілі, post повертає {stocktake, operation}", async () => {
    apiJson.mockResolvedValueOnce({ id: "st", status: "draft", version: 3, lines: [{ product: ref, expectedQuantity: 2, countedQuantity: null }] });
    const st = await httpBeautyApi.saveStocktakeLines("st", 2, [{ productId: "p1", countedQuantity: 4, surplusExpiresOn: null }]);
    expect(last()[0]).toBe("/api/beauty/inventory/stocktakes/st/lines");
    expect(JSON.parse(last()[1]!.body!)).toEqual({ expectedVersion: 2, lines: [{ productId: "p1", countedQuantity: 4, surplusExpiresOn: null }] });
    expect(st.version).toBe(3);
    apiJson.mockResolvedValueOnce({ stocktake: { id: "st", status: "posted", version: 4, lines: [] }, operation: null });
    const posted = await httpBeautyApi.postStocktake("st", 3);
    expect(last()[0]).toBe("/api/beauty/inventory/stocktakes/st/post");
    expect(JSON.parse(last()[1]!.body!)).toEqual({ expectedVersion: 3 });
    expect(posted).toMatchObject({ operation: null, stocktake: { status: "posted" } });
    await httpBeautyApi.cancelStocktake("st", 4);
    expect(last()[0]).toBe("/api/beauty/inventory/stocktakes/st/cancel");
  });

  it("журнал: діапазон і тип у query; сміття в відповіді не ламає мапінг", async () => {
    apiJson.mockResolvedValueOnce({ items: [{ id: "m", type: "bogus", quantity: "x" }, null], hasMore: false });
    const r = await httpBeautyApi.getMovements({ type: "write_off", from: "2026-10-01T00:00:00Z", to: "2026-10-14T23:59:59Z" });
    expect(last()[0]).toBe("/api/beauty/inventory/movements?type=write_off&from=2026-10-01T00%3A00%3A00Z&to=2026-10-14T23%3A59%3A59Z");
    expect(r.items[0]).toMatchObject({ id: "m", type: "receipt", quantity: 0, createdBy: null });
  });

  it("етап 2a: нові поля залишків адитивні (fallback для старого сервера)", async () => {
    apiJson.mockResolvedValueOnce({ location: { id: "c" }, items: [{ product: ref, locationId: "c", quantity: 0, shortageQuantity: 0.3, balance: -0.3 }, { product: ref, locationId: "c", quantity: 4 }], hasMore: false });
    const r = await httpBeautyApi.getStock({ locationId: "c" });
    expect(r.items[0]).toMatchObject({ shortageQuantity: 0.3, balance: -0.3 });
    expect(r.items[1]).toMatchObject({ shortageQuantity: 0, balance: 4 });
    apiJson.mockResolvedValueOnce({ items: [{ product: ref, totalQuantity: 3, totalShortage: 1, locations: [{ locationId: "c", quantity: 3, shortageQuantity: 1, balance: 2, isLow: true }] }], locations: [] });
    const sum = await httpBeautyApi.getStockSummary();
    expect(sum.items[0]).toMatchObject({ totalShortage: 1, locations: [{ shortageQuantity: 1, balance: 2 }] });
    apiJson.mockResolvedValueOnce({ items: [{ id: "m", type: "service_consumption", isShortage: true, batchId: null, quantity: -0.3 }], hasMore: false });
    expect((await httpBeautyApi.getMovements({ type: "service_consumption" })).items[0]).toMatchObject({ isShortage: true, batchId: null });
    apiJson.mockResolvedValueOnce({ items: [{ id: "o", kind: "service_consumption", appointmentId: "ap1" }], hasMore: false });
    expect((await httpBeautyApi.getOperations({ kind: "service_consumption" })).items[0]).toMatchObject({ appointmentId: "ap1" });
  });

  it("норми: шляхи, методи, тіло; DELETE з locationId лише для закладу", async () => {
    apiJson.mockResolvedValueOnce({ serviceId: "s1", serviceName: "Манікюр", network: { serviceId: "s1", locationId: null, lines: [{ product: ref, quantity: 2 }], updatedAt: "t", updatedBy: { id: "u", name: "Анна" } }, locations: [{ serviceId: "s1", locationId: "c", locationName: "Центр", lines: [] }] });
    const d = await httpBeautyApi.getServiceMaterials("s1");
    expect(last()[0]).toBe("/api/beauty/inventory/services/s1/materials");
    expect(d.network?.lines[0]).toMatchObject({ quantity: 2 });
    expect(d.locations[0]).toMatchObject({ locationId: "c", locationName: "Центр", lines: [] });
    apiJson.mockResolvedValueOnce({ serviceId: "s1", locationId: "c", lines: [], updatedAt: "t" });
    await httpBeautyApi.setServiceMaterials("s1", { locationId: "c", lines: [] });
    expect(last()[0]).toBe("/api/beauty/inventory/services/s1/materials");
    expect(last()[1]).toMatchObject({ method: "PUT", cache: "no-store" });
    expect(JSON.parse(last()[1]!.body!)).toEqual({ locationId: "c", lines: [] });
    await httpBeautyApi.deleteServiceMaterials("s1", null);
    expect(last()[0]).toBe("/api/beauty/inventory/services/s1/materials");
    expect(last()[1]).toMatchObject({ method: "DELETE" });
    await httpBeautyApi.deleteServiceMaterials("s1", "c");
    expect(last()[0]).toBe("/api/beauty/inventory/services/s1/materials?locationId=c");
    apiJson.mockResolvedValueOnce({ items: [] });
    await httpBeautyApi.getServiceMaterialsList();
    expect(last()[0]).toBe("/api/beauty/inventory/service-materials?includeInactive=false");
  });

  it("сповіщення: query, лічильник, read, read-all; ПД клієнтів не копіюються в DTO", async () => {
    apiJson.mockResolvedValueOnce({
      items: [{ id: "a1", kind: "shortage", status: "open", location: { id: "c", name: "Центр" }, product: ref, operationId: "op1", quantity: 0.3, createdAt: "t", clientName: "Не повинно", appointmentId: "ap", phone: "+380" }],
      hasMore: true,
      nextCursor: "n",
    });
    const r = await httpBeautyApi.getInventoryAlerts({ status: "open", kind: "shortage", locationId: "c", take: 500 });
    expect(last()[0]).toBe("/api/beauty/inventory/alerts?status=open&kind=shortage&locationId=c&take=200");
    expect(last()[1]).toMatchObject({ cache: "no-store" });
    expect(JSON.stringify(r)).not.toMatch(/Не повинно|appointmentId|380|phone|clientName/);
    expect(r.items[0]).toMatchObject({ kind: "shortage", operationId: "op1", batch: null });
    apiJson.mockResolvedValueOnce({ count: 7 });
    expect(await httpBeautyApi.getInventoryAlertsUnreadCount()).toBe(7);
    expect(last()[0]).toBe("/api/beauty/inventory/alerts/unread-count");
    apiJson.mockResolvedValueOnce({ id: "a1", kind: "low_stock", status: "read" });
    expect((await httpBeautyApi.markInventoryAlertRead("a1")).status).toBe("read");
    expect(last()[0]).toBe("/api/beauty/inventory/alerts/a1/read");
    expect(last()[1]).toMatchObject({ method: "POST" });
    apiJson.mockResolvedValueOnce({ updated: 4 });
    expect(await httpBeautyApi.markAllInventoryAlertsRead("c")).toBe(4);
    expect(last()[0]).toBe("/api/beauty/inventory/alerts/read-all?locationId=c");
    apiJson.mockResolvedValueOnce({ updated: 2 });
    expect(await httpBeautyApi.markAllInventoryAlertsRead()).toBe(2);
    expect(last()[0]).toBe("/api/beauty/inventory/alerts/read-all");
  });
});
