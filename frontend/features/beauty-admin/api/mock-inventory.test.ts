import { describe, expect, it } from "vitest";
import { BeautyApiError } from "@/features/beauty-auth/errors";
import { DEMO_LOCATIONS } from "../../beauty-shared/demo-seed";
import { createMockInventory } from "./mock-inventory";
import type { StockRow } from "../inventory/types";

// Фіксований «зараз»: 14.10.2026 12:00 за Києвом. Жодного реального годинника.
const NOW = Date.parse("2026-10-14T09:00:00Z");

function setup(role = "owner") {
  let r = role;
  const inv = createMockInventory({ role: () => r, locations: () => DEMO_LOCATIONS, specialistLocationIds: () => ["c"], now: () => NOW, wait: false });
  return { inv, setRole: (x: string) => (r = x) };
}
let keyN = 0;
const key = () => `key-${++keyN}`;

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
const stock = async (inv: ReturnType<typeof setup>["inv"], locationId: string, search = "") => (await inv.getStock({ locationId, search, take: 200 })).items;
const find = (rows: StockRow[], part: string) => rows.find((r) => r.product.name.includes(part))!;

describe("права за роллю й scope", () => {
  it("owner: усі заклади, ціни edit, каталог; specialist: лише свій заклад, без операцій і цін", async () => {
    const { inv, setRole } = setup();
    const o = await inv.getInventoryScope();
    expect(o.locations).toHaveLength(DEMO_LOCATIONS.length);
    expect(o.costs).toBe("edit");
    expect(o.canEditCatalog).toBe(true);
    setRole("specialist");
    const s = await inv.getInventoryScope();
    expect(s.locations.map((l) => l.id)).toEqual(["c"]);
    expect(s.locations[0]!.canManage).toBe(false);
    expect(s.costs).toBe("none");
    await expectError(inv.createReceipt({ locationId: "c", lines: [] }, key()), 403, "forbidden_role");
    await expectError(inv.getStock({ locationId: "p" }), 404, "location_not_found");
    await expectError(inv.getSuppliers(), 403, "forbidden_role");
  });

  it("deputy: каталог і операції, але без цін; поля цін відсутні в JSON (не 0)", async () => {
    const { inv, setRole } = setup();
    setRole("deputy");
    const scope = await inv.getInventoryScope();
    expect(scope.costs).toBe("none");
    const products = (await inv.getInventoryProducts({ take: 50 })).items;
    expect(products.length).toBeGreaterThan(0);
    for (const p of products) expect("purchasePrice" in p).toBe(false);
    const rows = await stock(inv, "c");
    for (const r of rows) expect("value" in r).toBe(false);
    const batches = (await inv.getBatches({ locationId: "c" })).items;
    for (const b of batches) expect("unitCost" in b).toBe(false);
  });

  it("ціна без costs=edit -> 403 forbidden_role {permission}", async () => {
    const { inv, setRole } = setup();
    setRole("deputy");
    const e = await expectError(
      inv.createInventoryProduct({ name: "Новий", unit: "pcs", purpose: "both", purchasePrice: 10 }),
      403,
      "forbidden_role",
    );
    expect(e.permission).toBe("inventory.costs");
    const p = (await inv.getInventoryProducts({})).items[0]!;
    const rows = await stock(inv, "c");
    const line = { productId: p.id, quantity: 1, unitCost: 5 };
    await expectError(inv.createReceipt({ locationId: "c", lines: [line] }, key()), 403, "forbidden_role");
    void rows;
  });
});

describe("каталог", () => {
  it("штрихкод унікальний (409), одиницю з рухами не змінити (422 unit_locked), цілі мін. залишки для шт", async () => {
    const { inv } = setup();
    await expectError(inv.createInventoryProduct({ name: "X", unit: "pcs", purpose: "both", barcode: "4820000000011" }), 409, "barcode_taken");
    await expectError(inv.createInventoryProduct({ name: "X", unit: "pcs", purpose: "both", minStock: 1.5 }), 422, "invalid_min_stock");
    await expectError(inv.createInventoryProduct({ name: " ", unit: "pcs", purpose: "both" }), 422, "invalid_name");
    const shampoo = (await inv.getInventoryProducts({ search: "Шампунь" })).items[0]!;
    expect(shampoo.hasMovements).toBe(true);
    await expectError(inv.updateInventoryProduct(shampoo.id, { name: shampoo.name, unit: "ml", purpose: "both" }), 422, "unit_locked");
    const created = await inv.createInventoryProduct({ name: "Новий товар", unit: "pcs", purpose: "sale", purchasePrice: 12.5 });
    expect(created.purchasePrice).toBe(12.5);
    const changed = await inv.updateInventoryProduct(created.id, { name: "Новий товар", unit: "ml", purpose: "sale" });
    expect(changed.unit).toBe("ml");
  });

  it("налаштування закладу: min_stock override змінює isLow, DELETE повертає мережеве", async () => {
    const { inv } = setup();
    const gloves = (await inv.getInventoryProducts({ search: "Рукавички" })).items[0]!;
    const before = find(await stock(inv, "c"), "Рукавички");
    expect(before.quantity).toBe(12);
    expect(before.isLow).toBe(true); // мережевий 20
    await inv.setProductLocationSetting(gloves.id, "c", { minStock: 5, isEnabled: true });
    expect(find(await stock(inv, "c"), "Рукавички").isLow).toBe(false);
    await inv.deleteProductLocationSetting(gloves.id, "c");
    expect(find(await stock(inv, "c"), "Рукавички").isLow).toBe(true);
    await expectError(inv.deleteProductLocationSetting(gloves.id, "c"), 404, "setting_not_found");
  });
});

describe("надходження", () => {
  it("створює нову партію, рівень зростає; ідемпотентність: повтор -> та сама операція, інше тіло -> 422", async () => {
    const { inv } = setup();
    const gel = (await inv.getInventoryProducts({ search: "Гель-лак" })).items[0]!;
    const before = find(await stock(inv, "c"), "Гель-лак").quantity;
    const k = key();
    const req = { locationId: "c", lines: [{ productId: gel.id, quantity: 4, unitCost: 200 }] };
    const op = await inv.createReceipt(req, k);
    expect(op.kind).toBe("receipt");
    expect(op.movements[0]).toMatchObject({ type: "receipt", quantity: 4, unitCost: 200 });
    expect(find(await stock(inv, "c"), "Гель-лак").quantity).toBe(before + 4);
    const again = await inv.createReceipt(req, k);
    expect(again.id).toBe(op.id);
    expect(find(await stock(inv, "c"), "Гель-лак").quantity).toBe(before + 4);
    await expectError(inv.createReceipt({ ...req, lines: [{ productId: gel.id, quantity: 5 }] }, k), 422, "idempotency_key_reused");
    await expectError(inv.createReceipt(req, ""), 422, "idempotency_key_required");
  });

  it("правила активності, терміну й кількості", async () => {
    const { inv } = setup();
    const shampoo = (await inv.getInventoryProducts({ search: "Шампунь" })).items[0]!;
    const gel = (await inv.getInventoryProducts({ search: "Гель-лак" })).items[0]!;
    const e1 = await expectError(inv.createReceipt({ locationId: "c", lines: [{ productId: shampoo.id, quantity: 1 }] }, key()), 422, "expiry_required");
    expect(e1.body).toMatchObject({ line: 0 });
    await expectError(inv.createReceipt({ locationId: "c", lines: [{ productId: shampoo.id, quantity: 1, expiresOn: "2026-10-13" }] }, key()), 422, "invalid_expiry");
    await expectError(inv.createReceipt({ locationId: "c", lines: [{ productId: gel.id, quantity: 1.5 }] }, key()), 422, "invalid_quantity");
    await expectError(inv.createReceipt({ locationId: "l", lines: [{ productId: gel.id, quantity: 1 }] }, key()), 422, "location_inactive");
    await expectError(inv.createReceipt({ locationId: "zzz", lines: [{ productId: gel.id, quantity: 1 }] }, key()), 404, "location_not_found");
    const inactive = (await inv.getInventoryProducts({ active: "false" })).items[0]!;
    await expectError(inv.createReceipt({ locationId: "c", lines: [{ productId: inactive.id, quantity: 1 }] }, key()), 422, "product_inactive");
    await inv.setProductLocationSetting(gel.id, "c", { minStock: null, isEnabled: false });
    await expectError(inv.createReceipt({ locationId: "c", lines: [{ productId: gel.id, quantity: 1 }] }, key()), 422, "product_disabled_for_location");
  });
});

describe("списання: FEFO і нестача", () => {
  it("прострочені партії йдуть першими; рівень = Σ партій", async () => {
    const { inv } = setup();
    const dye = (await inv.getInventoryProducts({ search: "Фарба" })).items[0]!;
    // у «Центрі»: прострочена 6 шт (−5 діб) і 14 шт (+120 діб)
    const op = await inv.createWriteOff({ locationId: "c", reason: "expired", lines: [{ productId: dye.id, quantity: 8 }] }, key());
    expect(op.movements.map((m) => m.quantity)).toEqual([-6, -2]);
    expect(find(await stock(inv, "c"), "Фарба").quantity).toBe(12);
    const left = (await inv.getBatches({ locationId: "c", productId: dye.id })).items;
    expect(left.map((b) => b.quantity)).toEqual([12]);
  });

  it("явна партія; нестача -> 422 insufficient_stock з available, без змін", async () => {
    const { inv } = setup();
    const dye = (await inv.getInventoryProducts({ search: "Фарба" })).items[0]!;
    const batches = (await inv.getBatches({ locationId: "c", productId: dye.id })).items;
    const fresh = batches.find((b) => b.status === "active")!;
    const e = await expectError(inv.createWriteOff({ locationId: "c", reason: "lost", lines: [{ productId: dye.id, quantity: 15, batchId: fresh.id }] }, key()), 422, "insufficient_stock");
    expect(e.body?.lines).toEqual([{ line: 0, productId: dye.id, batchId: fresh.id, requested: 15, available: 14 }]);
    expect(find(await stock(inv, "c"), "Фарба").quantity).toBe(20);
    const e2 = await expectError(inv.createWriteOff({ locationId: "c", reason: "lost", lines: [{ productId: dye.id, quantity: 21 }] }, key()), 422, "insufficient_stock");
    expect((e2.body?.lines as { available: number }[])[0]!.available).toBe(20);
  });

  it("коментар для «Виправлення» й «Інше», дублікати рядків, 404 партії", async () => {
    const { inv } = setup();
    const gel = (await inv.getInventoryProducts({ search: "Гель-лак" })).items[0]!;
    await expectError(inv.createWriteOff({ locationId: "c", reason: "correction", lines: [{ productId: gel.id, quantity: 1 }] }, key()), 422, "note_required");
    await expectError(
      inv.createWriteOff({ locationId: "c", reason: "damaged", lines: [{ productId: gel.id, quantity: 1 }, { productId: gel.id, quantity: 1 }] }, key()),
      422,
      "duplicate_line",
    );
    await expectError(inv.createWriteOff({ locationId: "c", reason: "damaged", lines: [{ productId: gel.id, quantity: 1, batchId: "nope" }] }, key()), 404, "batch_not_found");
  });
});

describe("переміщення", () => {
  it("атомарне: нові партії зберігають термін і ціну, сума ніг = 0, прострочені пропускаються", async () => {
    const { inv } = setup();
    const dye = (await inv.getInventoryProducts({ search: "Фарба" })).items[0]!;
    const op = await inv.createTransfer({ fromLocationId: "c", toLocationId: "p", lines: [{ productId: dye.id, quantity: 10 }] }, key());
    const sum = op.movements.reduce((s, m) => s + m.quantity, 0);
    expect(sum).toBe(0);
    expect(op.movements.map((m) => m.type)).toEqual(["transfer_out", "transfer_in"]);
    // прострочена партія (6 шт) не чіпається: взято з непростроченої (14 -> 4)
    const src = (await inv.getBatches({ locationId: "c", productId: dye.id })).items;
    expect(src.map((b) => [b.status, b.quantity]).sort()).toEqual([["active", 4], ["expired", 6]].sort());
    const dst = (await inv.getBatches({ locationId: "p", productId: dye.id })).items;
    expect(dst).toHaveLength(1);
    expect(dst[0]).toMatchObject({ quantity: 10, sourceBatchId: expect.any(String), unitCost: 140 });
    expect(dst[0]!.expiresOn).toBe(src.find((b) => b.status === "active")!.expiresOn);
  });

  it("available без прострочених; явна прострочена партія -> batch_expired; same_location; неактивний заклад", async () => {
    const { inv } = setup();
    const dye = (await inv.getInventoryProducts({ search: "Фарба" })).items[0]!;
    const e = await expectError(inv.createTransfer({ fromLocationId: "c", toLocationId: "p", lines: [{ productId: dye.id, quantity: 15 }] }, key()), 422, "insufficient_stock");
    expect((e.body?.lines as { available: number }[])[0]!.available).toBe(14);
    const expired = (await inv.getBatches({ locationId: "c", productId: dye.id, status: "expired" })).items[0]!;
    await expectError(inv.createTransfer({ fromLocationId: "c", toLocationId: "p", lines: [{ productId: dye.id, quantity: 1, batchId: expired.id }] }, key()), 422, "batch_expired");
    await expectError(inv.createTransfer({ fromLocationId: "c", toLocationId: "c", lines: [{ productId: dye.id, quantity: 1 }] }, key()), 422, "same_location");
    await expectError(inv.createTransfer({ fromLocationId: "c", toLocationId: "l", lines: [{ productId: dye.id, quantity: 1 }] }, key()), 422, "location_inactive");
  });
});

describe("інвентаризація", () => {
  it("чернетка: одна на заклад; version; нестача за FEFO, надлишок - нова партія; проведена незмінна", async () => {
    const { inv } = setup();
    const gloves = (await inv.getInventoryProducts({ search: "Рукавички" })).items[0]!;
    const gel = (await inv.getInventoryProducts({ search: "Гель-лак" })).items[0]!;
    const st = await inv.createStocktake({ locationId: "c", productIds: [gloves.id, gel.id] });
    expect(st).toMatchObject({ status: "draft", version: 1, lineCount: 2, countedCount: 0 });
    const dup = await expectError(inv.createStocktake({ locationId: "c" }), 409, "stocktake_in_progress");
    expect(dup.body).toMatchObject({ stocktakeId: st.id });
    await expectError(inv.postStocktake(st.id, 1), 422, "stocktake_empty");
    const saved = await inv.saveStocktakeLines(st.id, 1, [
      { productId: gloves.id, countedQuantity: 10 },
      { productId: gel.id, countedQuantity: 5 },
    ]);
    expect(saved.version).toBe(2);
    expect(saved.lines.find((l) => l.product.id === gloves.id)).toMatchObject({ expectedQuantity: 12, countedQuantity: 10, difference: -2 });
    const conflict = await expectError(inv.saveStocktakeLines(st.id, 1, [{ productId: gel.id, countedQuantity: 1 }]), 409, "stocktake_version_conflict");
    expect(conflict.body).toMatchObject({ currentVersion: 2 });
    const res = await inv.postStocktake(st.id, 2);
    expect(res.stocktake.status).toBe("posted");
    expect(res.operation?.kind).toBe("stocktake");
    expect(res.operation?.movements.map((m) => m.quantity).sort()).toEqual([-2, 2]);
    const rows = await stock(inv, "c");
    expect(find(rows, "Рукавички").quantity).toBe(10);
    expect(find(rows, "Гель-лак").quantity).toBe(5);
    await expectError(inv.postStocktake(st.id, 3), 409, "stocktake_closed");
    await expectError(inv.saveStocktakeLines(st.id, 3, [{ productId: gel.id, countedQuantity: 1 }]), 409, "stocktake_closed");
    // нова чернетка дозволена після проведення
    expect((await inv.createStocktake({ locationId: "c", productIds: [gel.id] })).status).toBe("draft");
  });

  it("нуль = без операції; expiry_required для надлишку товару з терміном; скасування без рухів", async () => {
    const { inv } = setup();
    const gel = (await inv.getInventoryProducts({ search: "Гель-лак" })).items[0]!;
    const shampoo = (await inv.getInventoryProducts({ search: "Шампунь" })).items[0]!;
    const a = await inv.createStocktake({ locationId: "p", productIds: [gel.id] });
    await inv.saveStocktakeLines(a.id, 1, [{ productId: gel.id, countedQuantity: 9 }]);
    const none = await inv.postStocktake(a.id, 2);
    expect(none.operation).toBeNull();
    const b = await inv.createStocktake({ locationId: "p", productIds: [shampoo.id] });
    await inv.saveStocktakeLines(b.id, 1, [{ productId: shampoo.id, countedQuantity: 10 }]);
    await expectError(inv.postStocktake(b.id, 2), 422, "expiry_required");
    const withDate = await inv.saveStocktakeLines(b.id, 2, [{ productId: shampoo.id, countedQuantity: 10, surplusExpiresOn: "2027-01-01" }]);
    const posted = await inv.postStocktake(b.id, withDate.version);
    expect(posted.operation?.movements[0]).toMatchObject({ quantity: 7 });
    const c = await inv.createStocktake({ locationId: "p", productIds: [gel.id] });
    const cancelled = await inv.cancelStocktake(c.id, 1);
    expect(cancelled.status).toBe("cancelled");
    await expectError(inv.cancelStocktake(c.id, 2), 409, "stocktake_closed");
  });
});

describe("інваріанти й журнал", () => {
  it("після операцій рівень = Σ партій = Σ рухів для кожної пари (заклад, товар)", async () => {
    const { inv } = setup();
    const dye = (await inv.getInventoryProducts({ search: "Фарба" })).items[0]!;
    const gel = (await inv.getInventoryProducts({ search: "Гель-лак" })).items[0]!;
    await inv.createReceipt({ locationId: "p", lines: [{ productId: gel.id, quantity: 3 }] }, key());
    await inv.createWriteOff({ locationId: "c", reason: "damaged", lines: [{ productId: dye.id, quantity: 7 }] }, key());
    await inv.createTransfer({ fromLocationId: "c", toLocationId: "k", lines: [{ productId: dye.id, quantity: 5 }, { productId: gel.id, quantity: 1 }] }, key());
    const moves = (await inv.getMovements({ from: "2026-01-01T00:00:00Z", to: "2026-10-14T09:00:00Z", take: 200 })).items;
    for (const loc of ["c", "p", "k"]) {
      for (const row of await stock(inv, loc)) {
        const batches = (await inv.getBatches({ locationId: loc, productId: row.product.id, status: "all" })).items;
        const sumBatches = Math.round(batches.reduce((s, b) => s + b.quantity, 0) * 1000) / 1000;
        const sumMoves = Math.round(moves.filter((m) => m.locationId === loc && m.product.id === row.product.id).reduce((s, m) => s + m.quantity, 0) * 1000) / 1000;
        expect(row.quantity).toBe(sumBatches);
        expect(row.quantity).toBe(sumMoves);
        expect(row.quantity).toBeGreaterThanOrEqual(0);
      }
    }
  });

  it("журнал: найновіші першими, діапазон > 366 діб -> invalid_range, курсор", async () => {
    const { inv } = setup();
    const moves = (await inv.getMovements({ from: "2026-01-01T00:00:00Z", take: 2 }));
    expect(moves.items).toHaveLength(2);
    expect(moves.hasMore).toBe(true);
    const next = await inv.getMovements({ from: "2026-01-01T00:00:00Z", take: 2, cursor: moves.nextCursor });
    expect(next.items[0]!.id).not.toBe(moves.items[0]!.id);
    await expectError(inv.getMovements({ from: "2025-01-01T00:00:00Z", to: "2026-10-14T00:00:00Z" }), 422, "invalid_range");
    await expectError(inv.getMovements({ cursor: "-5" }), 422, "invalid_cursor");
  });

  it("зведення по закладах і фільтр низького залишку; партії: статуси від дати закладу", async () => {
    const { inv } = setup();
    const sum = await inv.getStockSummary({ take: 50 });
    expect(sum.locations.map((l) => l.id)).toContain("c");
    const gloves = sum.items.find((r) => r.product.name.includes("Рукавички"))!;
    expect(gloves.totalQuantity).toBe(42);
    expect(gloves.isLowAnywhere).toBe(true);
    const low = await inv.getStockSummary({ lowOnly: true });
    expect(low.items.every((r) => r.isLowAnywhere)).toBe(true);
    const expired = (await inv.getBatches({ status: "expired" })).items;
    expect(expired).toHaveLength(1);
    expect(expired[0]!.daysToExpiry).toBe(-5);
    const soon = (await inv.getBatches({ status: "expiring" })).items.map((b) => b.daysToExpiry);
    expect(soon).toContain(20);
  });

  it("429 rate_limited після 60 записів за хвилину", async () => {
    const { inv } = setup();
    const gel = (await inv.getInventoryProducts({ search: "Гель-лак" })).items[0]!;
    for (let i = 0; i < 60; i++) await inv.createReceipt({ locationId: "c", lines: [{ productId: gel.id, quantity: 1 }] }, key());
    const e = await expectError(inv.createReceipt({ locationId: "c", lines: [{ productId: gel.id, quantity: 1 }] }, key()), 429, "rate_limited");
    expect(e.retryAfterSeconds).toBe(60);
  });
});
