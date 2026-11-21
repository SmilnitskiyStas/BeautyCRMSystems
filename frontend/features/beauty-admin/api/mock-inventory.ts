import { BeautyApiError } from "@/features/beauty-auth/errors";
import { todayInZone } from "../../beauty-shared/zone-time";
import { MAX_CATEGORY_DEPTH, depthOf, moveProblem, pathOf } from "../inventory/categories";
import {
  batchStatus,
  daysToExpiry,
  fromMilli,
  isIntegerUnit,
  isLow,
  isYmd,
  planFefo,
  toMilli,
} from "../inventory/logic";
import type {
  Batch,
  BatchQuery,
  Category,
  CategoryRequest,
  CostsAccess,
  InventoryScope,
  LocationSetting,
  Movement,
  MovementQuery,
  MovementType,
  NamedRef,
  Operation,
  OperationKind,
  OperationQuery,
  OperationSummary,
  Page,
  Product,
  ProductDetail,
  ProductImage,
  ProductQuery,
  ProductRef,
  ProductRequest,
  ReceiptRequest,
  StockPage,
  StockQuery,
  StockRow,
  StockSummaryPage,
  StockSummaryQuery,
  StockUnit,
  Stocktake,
  StocktakeLineInput,
  StocktakePostResult,
  StocktakeQuery,
  StocktakeSummary,
  Supplier,
  SupplierQuery,
  SupplierRequest,
  TransferRequest,
  WriteOffReason,
  WriteOffRequest,
} from "../inventory/types";
import type { BeautyLocation } from "../types";

/**
 * Mock §32 (ADR-025): склад у демо-режимі. Відтворює правила контракту: FEFO (прострочені першими для списання, пропуск
 * прострочених для переміщення), інваріант `рівень = Σ партій = Σ рухів` (рівень виводиться з партій), `insufficient_stock` з `available`,
 * ідемпотентність `Idempotency-Key`, інвентаризацію з `version`, межі прав за роллю й закупівельні ціни лише за `inventory.costs`.
 * Час - лише `deps.now()` (у тестах фіксований); «сьогодні» береться за зоною закладу.
 */

const DAY = 86_400_000;
const WRITES_PER_MINUTE = 60;
export const EXPIRING_SOON_DAYS = 30;

export interface InventoryMockDeps {
  role: () => string;
  locations: () => BeautyLocation[];
  /** Заклади спеціаліста (для `inventory.view = own`). */
  specialistLocationIds?: () => string[];
  now?: () => number;
  /** `false` - без затримки (тести). */
  wait?: boolean;
  userName?: string;
}

const err = (status: number, code: string, body: Record<string, unknown> = {}, retry?: number) =>
  new BeautyApiError(status, code, code, retry, undefined, undefined, undefined, typeof body.permission === "string" ? body.permission : undefined, body);
const forbidden = (permission: string, required: string) => err(403, "forbidden_role", { permission, required });
const decimalsOk = (v: number, d: number) => Math.abs(Math.round(v * 10 ** d) - v * 10 ** d) < 1e-7;
const norm = (s: string) => s.trim().toLowerCase();

interface BatchRec {
  id: string;
  locationId: string;
  productId: string;
  operationId: string;
  sourceBatchId: string | null;
  supplierId: string | null;
  receivedAt: string;
  expiresOn: string | null;
  lotNumber: string | null;
  initialQuantity: number;
  quantity: number;
  unitCost: number | null;
}
interface MovementRec {
  seq: number;
  id: string;
  operationId: string;
  operationKind: OperationKind;
  type: MovementType;
  occurredAt: string;
  locationId: string;
  productId: string;
  batchId: string | null;
  /** §32.17: рух нестачі / погашення нестачі (без партії). */
  isShortage?: boolean;
  quantity: number;
  unitCost: number | null;
  reason: WriteOffReason | null;
  note: string | null;
  counterpartLocationId: string | null;
  stocktakeId: string | null;
}
interface OperationRec {
  seq: number;
  id: string;
  kind: OperationKind;
  locationId: string;
  toLocationId: string | null;
  reason: WriteOffReason | null;
  documentNumber: string | null;
  createdAt: string;
  supplierId: string | null;
  receivedOn: string | null;
  note: string | null;
  stocktakeId: string | null;
  appointmentId?: string | null;
}
interface CategoryRec {
  id: string;
  parentId: string | null;
  name: string;
  sortOrder: number;
  isArchived: boolean;
}
const CATEGORY_LIMIT = 500;
interface ProductRec {
  id: string;
  name: string;
  category: string | null;
  categoryId: string | null;
  unit: StockUnit;
  barcode: string | null;
  purpose: Product["purpose"];
  supplierId: string | null;
  purchasePrice: number | null;
  salePrice: number | null;
  minStock: number | null;
  trackExpiry: boolean;
  isActive: boolean;
  note: string | null;
  createdAt: string;
  updatedAt: string;
}
type SupplierRec = Omit<Supplier, "productCount">;
interface StocktakeRec {
  id: string;
  locationId: string;
  status: Stocktake["status"];
  note: string | null;
  version: number;
  createdAt: string;
  postedAt: string | null;
  cancelledAt: string | null;
  lines: { productId: string; counted: number | null; expected: number | null; expectedShortage?: number | null; surplusExpiresOn: string | null }[];
}

export function createMockInventory(deps: InventoryMockDeps) {
  const now = deps.now ?? (() => Date.now());
  const me: NamedRef = { id: "mock-user", name: deps.userName ?? "Світлана Коваленко" };
  const iso = (ms = now()) => new Date(ms).toISOString();
  const wait = async <T>(v: T): Promise<T> => {
    if (deps.wait !== false) await new Promise((r) => setTimeout(r, 120));
    return structuredClone(v);
  };

  // ---------- права за роллю (пресети §32.2) ----------
  const role = () => deps.role();
  const viewAll = () => role() !== "specialist";
  const manageLevel = (): "none" | "own" | "all" => (role() === "specialist" ? "none" : "all");
  const costs = (): CostsAccess => (role() === "owner" || role() === "admin" ? "edit" : "none");
  const requireManage = () => {
    if (manageLevel() === "none") throw forbidden("inventory.manage", "own");
  };
  const requireCatalog = () => {
    if (manageLevel() !== "all") throw forbidden("inventory.manage", "all");
  };
  const allLocations = () => deps.locations();
  const scopeLocations = (): BeautyLocation[] => {
    if (viewAll()) return allLocations();
    const mine = new Set(deps.specialistLocationIds?.() ?? []);
    return allLocations().filter((l) => mine.has(l.id) && l.isActive !== false);
  };
  const locationOf = (id: string | undefined): BeautyLocation => {
    const l = scopeLocations().find((x) => x.id === id);
    if (!l) throw err(404, "location_not_found");
    return l;
  };
  const tzOf = (l: BeautyLocation) => l.timezone ?? "Europe/Kyiv";
  const todayOf = (l: BeautyLocation) => todayInZone(tzOf(l), new Date(now()));
  const locationById = (id: string) => allLocations().find((l) => l.id === id);
  const todayFor = (locationId: string) => {
    const l = locationById(locationId);
    return l ? todayOf(l) : todayInZone("Europe/Kyiv", new Date(now()));
  };

  // ---------- стан ----------
  let seq = 0;
  const nextId = (p: string) => `${p}${++seq}`;
  const suppliers: SupplierRec[] = [];
  const products: ProductRec[] = [];
  const categories: CategoryRec[] = [];
  const settings = new Map<string, LocationSetting>();
  const images = new Map<string, ProductImage[]>();
  const batches: BatchRec[] = [];
  const movements: MovementRec[] = [];
  const operations: OperationRec[] = [];
  const stocktakes: StocktakeRec[] = [];
  const idem = new Map<string, { hash: string; opId: string }>();
  const writes: number[] = [];

  const settingKey = (p: string, l: string) => `${p}|${l}`;
  const productById = (id: string) => products.find((p) => p.id === id);
  const supplierRef = (id: string | null): NamedRef | null => {
    const s = suppliers.find((x) => x.id === id);
    return s ? { id: s.id, name: s.name } : null;
  };
  const productRef = (p: ProductRec): ProductRef => ({ id: p.id, name: p.name, unit: p.unit, purpose: p.purpose, category: p.category, barcode: p.barcode, isActive: p.isActive });
  /** §32.17: нестача пари (заклад, товар); у демо - лише сид. */
  const shortages = new Map<string, number>();
  const shortageOf = (locationId: string, productId: string) => shortages.get(`${locationId}|${productId}`) ?? 0;
  const levelOf = (locationId: string, productId: string) =>
    fromMilli(batches.filter((b) => b.locationId === locationId && b.productId === productId).reduce((s, b) => s + toMilli(b.quantity), 0));
  const hasMovements = (productId: string) => movements.some((m) => m.productId === productId);
  const effectiveMin = (p: ProductRec, locationId: string) => {
    const s = settings.get(settingKey(p.id, locationId));
    return s && s.minStock !== null ? s.minStock : p.minStock;
  };
  const enabledAt = (p: ProductRec, locationId: string) => settings.get(settingKey(p.id, locationId))?.isEnabled !== false;

  function rateLimit() {
    const t = now();
    while (writes.length && t - writes[0]! > 60_000) writes.shift();
    if (writes.length >= WRITES_PER_MINUTE) throw err(429, "rate_limited", {}, 60);
    writes.push(t);
  }

  // ---------- дерево категорій ----------
  const categoryById = (id: string) => categories.find((c) => c.id === id);
  const toCategory = (c: CategoryRec): Category => ({
    id: c.id,
    parentId: c.parentId,
    name: c.name,
    sortOrder: c.sortOrder,
    isArchived: c.isArchived,
    productCount: products.filter((p) => p.categoryId === c.id).length,
    childCount: categories.filter((x) => x.parentId === c.id).length,
  });
  const nextSortOrder = (parentId: string | null) => Math.max(0, ...categories.filter((c) => c.parentId === parentId).map((c) => c.sortOrder)) + 1;
  function checkCategoryName(raw: string): string {
    const name = (raw ?? "").trim();
    if (name.length < 1 || name.length > 100) throw err(422, "invalid_name");
    return name;
  }
  function assertCategoryNameFree(name: string, parentId: string | null, exceptId: string | null) {
    if (categories.some((c) => c.id !== exceptId && c.parentId === parentId && norm(c.name) === norm(name))) throw err(409, "category_name_taken");
  }

  // ---------- DTO ----------
  const toProduct = (p: ProductRec): Product => ({
    id: p.id,
    name: p.name,
    category: p.category,
    categoryId: p.categoryId,
    categoryPath: p.categoryId ? pathOf(categories, p.categoryId) || null : null,
    unit: p.unit,
    barcode: p.barcode,
    purpose: p.purpose,
    supplier: supplierRef(p.supplierId),
    ...(costs() !== "none" ? { purchasePrice: p.purchasePrice } : {}),
    salePrice: p.salePrice,
    minStock: p.minStock,
    trackExpiry: p.trackExpiry,
    isActive: p.isActive,
    note: p.note,
    hasMovements: hasMovements(p.id),
    imageUrl: (images.get(p.id) ?? [])[0]?.thumbUrl ?? null,
    createdAt: p.createdAt,
    updatedAt: p.updatedAt,
  });
  const toBatch = (b: BatchRec): Batch => {
    const today = todayFor(b.locationId);
    return {
      id: b.id,
      product: productRef(productById(b.productId)!),
      locationId: b.locationId,
      receivedAt: b.receivedAt,
      expiresOn: b.expiresOn,
      lotNumber: b.lotNumber,
      initialQuantity: b.initialQuantity,
      quantity: b.quantity,
      ...(costs() !== "none" ? { unitCost: b.unitCost } : {}),
      supplier: supplierRef(b.supplierId),
      operationId: b.operationId,
      sourceBatchId: b.sourceBatchId,
      status: batchStatus(b, today, EXPIRING_SOON_DAYS),
      daysToExpiry: daysToExpiry(b.expiresOn, today),
    };
  };
  const toMovement = (m: MovementRec): Movement => ({
    id: m.id,
    operationId: m.operationId,
    operationKind: m.operationKind,
    type: m.type,
    occurredAt: m.occurredAt,
    locationId: m.locationId,
    product: productRef(productById(m.productId)!),
    batchId: m.batchId,
    quantity: m.quantity,
    isShortage: m.isShortage === true,
    ...(costs() !== "none" ? { unitCost: m.unitCost } : {}),
    reason: m.reason,
    note: m.note,
    counterpartLocationId: m.counterpartLocationId,
    stocktakeId: m.stocktakeId,
    createdBy: me,
  });
  const totalCost = (op: OperationRec): number | null => {
    const legs = movements.filter((m) => m.operationId === op.id && m.unitCost !== null && m.type !== "transfer_in");
    return legs.length === 0 ? null : Math.round(legs.reduce((s, m) => s + Math.abs(m.quantity) * (m.unitCost ?? 0), 0) * 100) / 100;
  };
  const inScope = (locationId: string) => scopeLocations().some((l) => l.id === locationId);
  const toSummary = (op: OperationRec): OperationSummary => ({
    id: op.id,
    kind: op.kind,
    locationId: op.locationId,
    toLocationId: op.toLocationId,
    reason: op.reason,
    documentNumber: op.documentNumber,
    createdAt: op.createdAt,
    createdBy: me,
    productCount: new Set(movements.filter((m) => m.operationId === op.id).map((m) => m.productId)).size,
    appointmentId: op.appointmentId ?? null,
    ...(costs() !== "none" ? { totalCost: totalCost(op) } : {}),
  });
  const toOperation = (op: OperationRec): Operation => ({
    ...toSummary(op),
    supplier: supplierRef(op.supplierId),
    receivedOn: op.receivedOn,
    note: op.note,
    stocktakeId: op.stocktakeId,
    movements: movements.filter((m) => m.operationId === op.id && inScope(m.locationId)).sort((a, b) => a.seq - b.seq).map(toMovement),
  });

  function page<T>(items: T[], q: { cursor?: string; take?: number }): Page<T> {
    const size = Math.min(200, Math.max(1, q.take ?? 50));
    const start = q.cursor === undefined ? 0 : Number(q.cursor);
    if (!Number.isInteger(start) || start < 0) throw err(422, "invalid_cursor");
    const slice = items.slice(start, start + size);
    const more = start + size < items.length;
    return { items: slice, hasMore: more, ...(more ? { nextCursor: String(start + size) } : {}) };
  }
  const byName = <T extends { name: string; id: string }>(a: T, b: T) => a.name.toLowerCase().localeCompare(b.name.toLowerCase(), "uk") || a.id.localeCompare(b.id);

  // ---------- внутрішні операції ----------
  function newOperation(o: Partial<OperationRec> & Pick<OperationRec, "kind" | "locationId">, at = now()): OperationRec {
    const op: OperationRec = {
      seq: ++seq,
      id: `op${seq}`,
      toLocationId: null,
      reason: null,
      documentNumber: null,
      createdAt: iso(at),
      supplierId: null,
      receivedOn: null,
      note: null,
      stocktakeId: null,
      ...o,
    };
    operations.push(op);
    return op;
  }
  function pushMovement(op: OperationRec, m: Omit<MovementRec, "seq" | "id" | "operationId" | "operationKind" | "occurredAt" | "note" | "stocktakeId"> & { note?: string | null }, at = now()) {
    seq += 1;
    movements.push({ seq, id: `mv${seq}`, operationId: op.id, operationKind: op.kind, occurredAt: iso(at), note: op.note, stocktakeId: op.stocktakeId, ...m });
  }
  function addBatch(op: OperationRec, p: ProductRec, locationId: string, qty: number, extra: Partial<BatchRec> = {}, at = now()): BatchRec {
    const b: BatchRec = {
      id: nextId("b"),
      locationId,
      productId: p.id,
      operationId: op.id,
      sourceBatchId: null,
      supplierId: p.supplierId,
      receivedAt: iso(at),
      expiresOn: null,
      lotNumber: null,
      initialQuantity: qty,
      quantity: qty,
      unitCost: p.purchasePrice,
      ...extra,
    };
    batches.push(b);
    return b;
  }

  /** Надходження без перевірок прав (сид). */
  function seedReceipt(locationId: string, lines: { p: ProductRec; qty: number; expiresOn?: string | null; lot?: string }[], daysAgo: number) {
    const at = now() - daysAgo * DAY;
    const op = newOperation({ kind: "receipt", locationId, receivedOn: null }, at);
    for (const l of lines) {
      const b = addBatch(op, l.p, locationId, l.qty, { expiresOn: l.expiresOn ?? null, lotNumber: l.lot ?? null, receivedAt: iso(at) }, at);
      pushMovement(op, { type: "receipt", locationId, productId: l.p.id, batchId: b.id, quantity: l.qty, unitCost: b.unitCost, reason: null, counterpartLocationId: null }, at);
    }
  }

  // ---------- сид ----------
  (function seed() {
    const t = iso(now() - 60 * DAY);
    const mkS = (name: string, contactName: string | null, phone: string | null, isActive = true) =>
      suppliers.push({ id: nextId("s"), name, contactName, phone, email: null, note: null, isActive });
    mkS("Бʼюті Опт", "Андрій Мельник", "+380 44 123 45 67");
    mkS("Pro Salon Supply", "Ірина Савчук", "+380 50 111 22 33");
    mkS("Старий постачальник", null, null, false);
    const mkP = (name: string, category: string | null, unit: StockUnit, purpose: Product["purpose"], o: Partial<ProductRec> = {}) => {
      const p: ProductRec = {
        id: nextId("p"),
        name,
        category,
        categoryId: null,
        unit,
        barcode: null,
        purpose,
        supplierId: suppliers[0]!.id,
        purchasePrice: null,
        salePrice: null,
        minStock: null,
        trackExpiry: false,
        isActive: true,
        note: null,
        createdAt: t,
        updatedAt: t,
        ...o,
      };
      products.push(p);
      return p;
    };
    const mkC = (name: string, parentId: string | null, sortOrder = 0) => {
      const c: CategoryRec = { id: nextId("c"), parentId, name, sortOrder, isArchived: false };
      categories.push(c);
      return c;
    };
    const hair = mkC("Волосся", null, 1);
    const dyes = mkC("Фарби", hair.id, 1);
    const ammoniaFree = mkC("Безаміачні", dyes.id);
    const shampoos = mkC("Шампуні", hair.id, 2);
    const nails = mkC("Нігті", null, 2);
    const gelPolish = mkC("Гель-лаки", nails.id);
    const shampoo = mkP("Шампунь професійний 500 мл", "Догляд за волоссям", "pcs", "both", { barcode: "4820000000011", purchasePrice: 320, salePrice: 450, minStock: 6, trackExpiry: true });
    const dye = mkP("Фарба для волосся 60 мл", "Фарбування", "pcs", "salon_use", { purchasePrice: 140, minStock: 10, trackExpiry: true, supplierId: suppliers[1]!.id });
    const gel = mkP("Гель-лак (база)", "Нігті", "pcs", "both", { barcode: "4820000000028", purchasePrice: 210, salePrice: 299.9, minStock: 4, supplierId: suppliers[1]!.id });
    const disinfect = mkP("Дезінфікуючий розчин", "Гігієна", "l", "salon_use", { purchasePrice: 95, minStock: 2.5, trackExpiry: true });
    const gloves = mkP("Рукавички одноразові (уп. 100 шт)", "Гігієна", "pack", "salon_use", { purchasePrice: 180, minStock: 20 });
    const mask = mkP("Маска для обличчя", "Косметологія", "ml", "sale", { purchasePrice: 4.5, salePrice: 9, minStock: 1000, trackExpiry: true, supplierId: suppliers[1]!.id });
    shampoo.categoryId = shampoos.id;
    dye.categoryId = ammoniaFree.id;
    gel.categoryId = gelPolish.id;
    mkP("Віск для депіляції", "Депіляція", "g", "salon_use", { purchasePrice: 0.6, minStock: 0 });
    mkP("Знятий з виробництва крем", "Косметологія", "pcs", "sale", { isActive: false });

    const day = (n: number) => todayInZone("Europe/Kyiv", new Date(now() + n * DAY));
    const C = "c";
    const P = "p";
    seedReceipt(C, [
      { p: shampoo, qty: 8, expiresOn: day(20), lot: "SH-0412" },
      { p: shampoo, qty: 10, expiresOn: day(200), lot: "SH-0613" },
      { p: dye, qty: 6, expiresOn: day(-5), lot: "DY-0101" },
      { p: dye, qty: 14, expiresOn: day(120), lot: "DY-0203" },
      { p: gel, qty: 3 },
      { p: disinfect, qty: 4.5, expiresOn: day(90) },
      { p: gloves, qty: 12 },
      { p: mask, qty: 2400, expiresOn: day(300) },
    ], 40);
    seedReceipt(P, [
      { p: shampoo, qty: 3, expiresOn: day(60) },
      { p: gel, qty: 9 },
      { p: gloves, qty: 30 },
    ], 25);
    // нестача (§32.17): автосписання «Дезінфікуючий розчин» у закладі P без залишку -> сальдо −0,3 л
    const sop = newOperation({ kind: "service_consumption", locationId: P, appointmentId: "demo-appt" }, now() - 2 * DAY);
    pushMovement(sop, { type: "service_consumption", locationId: P, productId: disinfect.id, batchId: null, isShortage: true, quantity: -0.3, unitCost: null, reason: null, counterpartLocationId: null }, now() - 2 * DAY);
    shortages.set(`${P}|${disinfect.id}`, 0.3);
    // списання: відображається в журналі рухів
    const wo = newOperation({ kind: "write_off", locationId: C, reason: "damaged", note: "Розбита пляшка" }, now() - 10 * DAY);
    const sb = batches.find((b) => b.locationId === C && b.productId === shampoo.id && b.lotNumber === "SH-0412")!;
    sb.quantity -= 2;
    pushMovement(wo, { type: "write_off", locationId: C, productId: shampoo.id, batchId: sb.id, quantity: -2, unitCost: sb.unitCost, reason: "damaged", counterpartLocationId: null }, now() - 10 * DAY);
  })();

  // ---------- ідемпотентність ----------
  function replay(key: string, request: unknown): Operation | null {
    if (!key || key.length > 128 || !/^[\x20-\x7e]+$/.test(key)) throw err(422, key ? "invalid_idempotency_key" : "idempotency_key_required");
    const found = idem.get(key);
    if (!found) return null;
    if (found.hash !== JSON.stringify(request)) throw err(422, "idempotency_key_reused");
    return toOperation(operations.find((o) => o.id === found.opId)!);
  }

  // ---------- валідація рядків ----------
  function checkQuantity(q: unknown, unit: StockUnit, line: number, allowZero = false) {
    if (typeof q !== "number" || !Number.isFinite(q) || q > 1_000_000 || q < 0 || (!allowZero && q === 0) || !decimalsOk(q, 3) || (isIntegerUnit(unit) && !Number.isInteger(q))) {
      throw err(422, "invalid_quantity", { line });
    }
  }
  function checkNote(note: string | undefined) {
    if (note !== undefined && note.trim().length > 500) throw err(422, "invalid_note");
  }
  function checkCostsRight(hasCostFields: boolean) {
    if (hasCostFields && costs() !== "edit") throw forbidden("inventory.costs", "edit");
  }

  // ---------- публічний API ----------
  const api = {
    async getInventoryScope(): Promise<InventoryScope> {
      return wait({
        locations: scopeLocations().map((l) => ({ id: l.id, name: l.name, isActive: l.isActive !== false, timezone: tzOf(l), canManage: manageLevel() !== "none" })),
        costs: costs(),
        canEditCatalog: manageLevel() === "all",
        expiringSoonDays: EXPIRING_SOON_DAYS,
      });
    },

    async getInventoryProducts(q: ProductQuery = {}): Promise<Page<Product>> {
      const active = q.active ?? "true";
      const s = norm(q.search ?? "");
      const list = products
        .filter((p) => (active === "all" ? true : active === "true" ? p.isActive : !p.isActive))
        .filter((p) => !s || norm(p.name).includes(s) || norm(p.barcode ?? "").includes(s))
        .filter((p) => !q.category || p.category === q.category)
        .filter((p) => !q.purpose || p.purpose === q.purpose)
        .filter((p) => !q.supplierId || p.supplierId === q.supplierId)
        .sort(byName);
      return wait(page(list.map(toProduct), q));
    },

    async getInventoryProduct(id: string): Promise<ProductDetail> {
      const p = productById(id);
      if (!p) throw err(404, "product_not_found");
      const locationSettings = scopeLocations()
        .map((l) => settings.get(settingKey(id, l.id)))
        .filter((x): x is LocationSetting => x !== undefined);
      return wait({ ...toProduct(p), locationSettings, images: images.get(id) ?? [] });
    },

    // Фото (§33.22): у демо файл показується через object URL (або прозорий gif поза браузером), без обробки.
    async addProductImage(productId: string, file: File): Promise<ProductImage> {
      requireCatalog();
      rateLimit();
      if (!productById(productId)) throw err(404, "product_not_found");
      if (file.size > 10 * 1024 * 1024) throw err(413, "file_too_large");
      if (!["image/jpeg", "image/png", "image/webp"].includes(file.type)) throw err(415, "unsupported_image_type");
      const list = images.get(productId) ?? [];
      if (list.length >= 8) throw err(422, "too_many_images", { limit: 8 });
      const url = typeof URL.createObjectURL === "function" ? URL.createObjectURL(file) : "data:image/gif;base64,R0lGODlhAQABAAAAACw=";
      const img: ProductImage = { id: nextId("img"), url, thumbUrl: url, width: 800, height: 800, sizeBytes: file.size, sortOrder: list.length, createdAt: iso() };
      images.set(productId, [...list, img]);
      return wait(img);
    },

    async deleteProductImage(productId: string, imageId: string): Promise<void> {
      requireCatalog();
      rateLimit();
      if (!productById(productId)) throw err(404, "product_not_found");
      const list = images.get(productId) ?? [];
      if (!list.some((i) => i.id === imageId)) throw err(404, "product_image_not_found");
      images.set(productId, list.filter((i) => i.id !== imageId).map((i, n) => ({ ...i, sortOrder: n })));
      return wait(undefined);
    },

    async reorderProductImages(productId: string, imageIds: string[]): Promise<ProductImage[]> {
      requireCatalog();
      rateLimit();
      if (!productById(productId)) throw err(404, "product_not_found");
      const list = images.get(productId) ?? [];
      const same = imageIds.length === list.length && new Set(imageIds).size === list.length && imageIds.every((id) => list.some((i) => i.id === id));
      if (!same) throw err(422, "invalid_image_order");
      const next = imageIds.map((id, n) => ({ ...list.find((i) => i.id === id)!, sortOrder: n }));
      images.set(productId, next);
      return wait(next);
    },

    async getInventoryCategories(includeArchived = false): Promise<Category[]> {
      return wait(categories.filter((c) => includeArchived || !c.isArchived).map(toCategory));
    },

    async createInventoryCategory(r: CategoryRequest): Promise<Category> {
      requireCatalog();
      rateLimit();
      const name = checkCategoryName(r.name);
      const parentId = r.parentId ?? null;
      if (parentId !== null) {
        const parent = categoryById(parentId);
        if (!parent) throw err(422, "category_not_found");
        if (parent.isArchived) throw err(422, "category_archived");
        if (depthOf(categories, parentId) >= MAX_CATEGORY_DEPTH) throw err(422, "category_depth");
      }
      if (categories.length >= CATEGORY_LIMIT) throw err(422, "category_limit");
      assertCategoryNameFree(name, parentId, null);
      const c: CategoryRec = { id: nextId("c"), parentId, name, sortOrder: r.sortOrder ?? nextSortOrder(parentId), isArchived: false };
      categories.push(c);
      return wait(toCategory(c));
    },

    async updateInventoryCategory(id: string, r: { name: string; sortOrder?: number }): Promise<Category> {
      requireCatalog();
      rateLimit();
      const c = categoryById(id);
      if (!c) throw err(404, "category_not_found");
      const name = checkCategoryName(r.name);
      assertCategoryNameFree(name, c.parentId, c.id);
      c.name = name;
      if (r.sortOrder !== undefined) c.sortOrder = r.sortOrder;
      return wait(toCategory(c));
    },

    async moveInventoryCategory(id: string, r: { parentId?: string | null; sortOrder?: number }): Promise<Category> {
      requireCatalog();
      rateLimit();
      const c = categoryById(id);
      if (!c) throw err(404, "category_not_found");
      const parentId = r.parentId ?? null;
      if (parentId !== null) {
        const parent = categoryById(parentId);
        if (!parent) throw err(422, "category_not_found");
        if (parent.isArchived) throw err(422, "category_archived");
      }
      const problem = moveProblem(categories, id, parentId);
      if (problem) throw err(422, problem);
      assertCategoryNameFree(c.name, parentId, c.id);
      c.parentId = parentId;
      c.sortOrder = r.sortOrder ?? nextSortOrder(parentId);
      return wait(toCategory(c));
    },

    async archiveInventoryCategory(id: string): Promise<Category> {
      requireCatalog();
      rateLimit();
      const c = categoryById(id);
      if (!c) throw err(404, "category_not_found");
      c.isArchived = true;
      return wait(toCategory(c));
    },

    async unarchiveInventoryCategory(id: string): Promise<Category> {
      requireCatalog();
      rateLimit();
      const c = categoryById(id);
      if (!c) throw err(404, "category_not_found");
      const parent = c.parentId ? categoryById(c.parentId) : undefined;
      if (parent?.isArchived) throw err(422, "category_archived");
      c.isArchived = false;
      return wait(toCategory(c));
    },

    async deleteInventoryCategory(id: string): Promise<void> {
      requireCatalog();
      rateLimit();
      const c = categoryById(id);
      if (!c) throw err(404, "category_not_found");
      if (categories.some((x) => x.parentId === id) || products.some((p) => p.categoryId === id)) throw err(409, "category_not_empty");
      categories.splice(categories.indexOf(c), 1);
      return wait(undefined);
    },

    async createInventoryProduct(r: ProductRequest): Promise<Product> {
      requireCatalog();
      rateLimit();
      checkCostsRight(r.purchasePrice !== undefined && r.purchasePrice !== null);
      const p: ProductRec = {
        id: nextId("p"),
        name: "",
        category: null,
        categoryId: null,
        unit: r.unit,
        barcode: null,
        purpose: r.purpose,
        supplierId: null,
        purchasePrice: null,
        salePrice: null,
        minStock: null,
        trackExpiry: r.trackExpiry === true,
        isActive: true,
        note: null,
        createdAt: iso(),
        updatedAt: iso(),
      };
      applyProduct(p, r, false);
      products.push(p);
      return wait(toProduct(p));
    },

    async updateInventoryProduct(id: string, r: ProductRequest): Promise<Product> {
      requireCatalog();
      rateLimit();
      const p = productById(id);
      if (!p) throw err(404, "product_not_found");
      checkCostsRight(r.purchasePrice !== undefined && r.purchasePrice !== null);
      applyProduct(p, r, true);
      return wait(toProduct(p));
    },

    async setProductLocationSetting(productId: string, locationId: string, input: { minStock: number | null; isEnabled: boolean }): Promise<LocationSetting> {
      requireManage();
      rateLimit();
      const p = productById(productId);
      if (!p) throw err(404, "product_not_found");
      locationOf(locationId);
      if (input.minStock !== null) checkMinStock(input.minStock, p.unit);
      const s: LocationSetting = { locationId, minStock: input.minStock, isEnabled: input.isEnabled, updatedAt: iso() };
      settings.set(settingKey(productId, locationId), s);
      return wait(s);
    },

    async deleteProductLocationSetting(productId: string, locationId: string): Promise<void> {
      requireManage();
      rateLimit();
      if (!productById(productId)) throw err(404, "product_not_found");
      locationOf(locationId);
      if (!settings.delete(settingKey(productId, locationId))) throw err(404, "setting_not_found");
      return wait(undefined);
    },

    async getSuppliers(q: SupplierQuery = {}): Promise<Page<Supplier>> {
      requireManage();
      const active = q.active ?? "true";
      const s = norm(q.search ?? "");
      const list = suppliers
        .filter((x) => (active === "all" ? true : active === "true" ? x.isActive : !x.isActive))
        .filter((x) => !s || norm(x.name).includes(s))
        .sort(byName)
        .map((x) => ({ ...x, productCount: products.filter((p) => p.supplierId === x.id).length }));
      return wait(page(list, q));
    },

    async createSupplier(r: SupplierRequest): Promise<Supplier> {
      requireCatalog();
      rateLimit();
      const rec: SupplierRec = { id: nextId("s"), name: "", contactName: null, phone: null, email: null, note: null, isActive: true };
      applySupplier(rec, r, false);
      suppliers.push(rec);
      return wait({ ...rec, productCount: 0 });
    },

    async updateSupplier(id: string, r: SupplierRequest): Promise<Supplier> {
      requireCatalog();
      rateLimit();
      const rec = suppliers.find((x) => x.id === id);
      if (!rec) throw err(404, "supplier_not_found");
      applySupplier(rec, r, true);
      return wait({ ...rec, productCount: products.filter((p) => p.supplierId === id).length });
    },

    async getStock(q: StockQuery): Promise<StockPage> {
      if (!q.locationId) throw err(422, "location_required");
      const loc = locationOf(q.locationId);
      const today = todayOf(loc);
      const s = norm(q.search ?? "");
      const rows: StockRow[] = products
        .filter((p) => p.isActive || levelOf(loc.id, p.id) > 0)
        .filter((p) => enabledAt(p, loc.id) || levelOf(loc.id, p.id) > 0)
        .filter((p) => !s || norm(p.name).includes(s) || norm(p.barcode ?? "").includes(s))
        .filter((p) => !q.category || p.category === q.category)
        .filter((p) => !q.purpose || p.purpose === q.purpose)
        .sort(byName)
        .map((p): StockRow => {
          const own = batches.filter((b) => b.locationId === loc.id && b.productId === p.id && b.quantity > 0);
          const quantity = levelOf(loc.id, p.id);
          const shortage = shortageOf(loc.id, p.id);
          const balance = fromMilli(toMilli(quantity) - toMilli(shortage));
          const min = effectiveMin(p, loc.id);
          const expiries = own.map((b) => b.expiresOn).filter((d): d is string => d !== null).sort();
          const sumBy = (st: "expired" | "expiring") => fromMilli(own.filter((b) => batchStatus(b, today, q.expiringWithinDays ?? EXPIRING_SOON_DAYS) === st).reduce((a, b) => a + toMilli(b.quantity), 0));
          const priced = own.filter((b) => b.unitCost !== null);
          return {
            product: productRef(p),
            locationId: loc.id,
            quantity,
            minStock: min,
            shortageQuantity: shortage,
            balance,
            isLow: isLow(balance, min),
            isEnabled: enabledAt(p, loc.id),
            nearestExpiresOn: expiries[0] ?? null,
            expiredQuantity: sumBy("expired"),
            expiringQuantity: sumBy("expiring"),
            ...(costs() !== "none"
              ? { value: Math.round(priced.reduce((a, b) => a + b.quantity * (b.unitCost ?? 0), 0) * 100) / 100, valueIncomplete: priced.length < own.length }
              : {}),
          };
        })
        .filter((r) => {
          const st = q.state ?? "all";
          if (st === "low") return r.isLow;
          if (st === "in_stock") return r.quantity > 0;
          if (st === "out_of_stock") return r.quantity === 0;
          return true;
        })
        .filter((r) => q.expiringWithinDays === undefined || r.expiredQuantity > 0 || r.expiringQuantity > 0);
      return wait({ location: { id: loc.id, name: loc.name, timezone: tzOf(loc), isActive: loc.isActive !== false }, ...page(rows, q) });
    },

    async getStockSummary(q: StockSummaryQuery = {}): Promise<StockSummaryPage> {
      const locs = scopeLocations();
      const s = norm(q.search ?? "");
      const rows = products
        .filter((p) => p.isActive || locs.some((l) => levelOf(l.id, p.id) > 0 || shortageOf(l.id, p.id) > 0))
        .filter((p) => !s || norm(p.name).includes(s) || norm(p.barcode ?? "").includes(s))
        .filter((p) => !q.category || p.category === q.category)
        .filter((p) => !q.purpose || p.purpose === q.purpose)
        .sort(byName)
        .map((p) => {
          const per = locs.map((l) => {
            const quantity = levelOf(l.id, p.id);
            const shortageQuantity = shortageOf(l.id, p.id);
            const balance = fromMilli(toMilli(quantity) - toMilli(shortageQuantity));
            return { locationId: l.id, quantity, shortageQuantity, balance, isLow: isLow(balance, effectiveMin(p, l.id)) };
          });
          const own = batches.filter((b) => b.productId === p.id && b.quantity > 0 && locs.some((l) => l.id === b.locationId));
          const priced = own.filter((b) => b.unitCost !== null);
          return {
            product: productRef(p),
            totalQuantity: fromMilli(per.reduce((a, x) => a + toMilli(x.quantity), 0)),
            totalShortage: fromMilli(per.reduce((a, x) => a + toMilli(x.shortageQuantity), 0)),
            isLowAnywhere: per.some((x) => x.isLow),
            locations: per,
            ...(costs() !== "none"
              ? { value: Math.round(priced.reduce((a, b) => a + b.quantity * (b.unitCost ?? 0), 0) * 100) / 100, valueIncomplete: priced.length < own.length }
              : {}),
          };
        })
        .filter((r) => !q.lowOnly || r.isLowAnywhere);
      return wait({ locations: locs.map((l) => ({ id: l.id, name: l.name, isActive: l.isActive !== false })), ...page(rows, q) });
    },

    async getBatches(q: BatchQuery = {}): Promise<Page<Batch>> {
      if (q.locationId) locationOf(q.locationId);
      const status = q.status ?? "active";
      const list = batches
        .filter((b) => inScope(b.locationId) && (!q.locationId || b.locationId === q.locationId) && (!q.productId || b.productId === q.productId))
        .map(toBatch)
        .filter((b) => (status === "all" ? true : status === "active" ? b.quantity > 0 : b.status === status))
        .filter((b) => q.expiringWithinDays === undefined || (b.daysToExpiry !== null && b.daysToExpiry <= q.expiringWithinDays));
      const sorted = [...list].sort((a, b) => {
        const e = (a.expiresOn ?? "9999") < (b.expiresOn ?? "9999") ? -1 : (a.expiresOn ?? "9999") > (b.expiresOn ?? "9999") ? 1 : 0;
        return e || a.receivedAt.localeCompare(b.receivedAt) || a.locationId.localeCompare(b.locationId) || a.id.localeCompare(b.id);
      });
      return wait(page(sorted, q));
    },

    async getMovements(q: MovementQuery = {}): Promise<Page<Movement>> {
      if (q.locationId) locationOf(q.locationId);
      const to = q.to ? Date.parse(q.to) : now();
      const from = q.from ? Date.parse(q.from) : to - 30 * DAY;
      if (Number.isNaN(to) || Number.isNaN(from) || from > to || to - from > 366 * DAY) throw err(422, "invalid_range");
      const list = movements
        .filter((m) => inScope(m.locationId) && (!q.locationId || m.locationId === q.locationId))
        .filter((m) => (!q.productId || m.productId === q.productId) && (!q.type || m.type === q.type) && (!q.operationId || m.operationId === q.operationId))
        .filter((m) => Date.parse(m.occurredAt) >= from && Date.parse(m.occurredAt) <= to)
        .sort((a, b) => b.seq - a.seq)
        .map(toMovement);
      return wait(page(list, q));
    },

    async getOperations(q: OperationQuery = {}): Promise<Page<OperationSummary>> {
      if (q.locationId) locationOf(q.locationId);
      const from = q.from ? Date.parse(q.from) : -Infinity;
      const to = q.to ? Date.parse(q.to) : Infinity;
      const list = operations
        .filter((o) => inScope(o.locationId) || (o.toLocationId !== null && inScope(o.toLocationId)))
        .filter((o) => (!q.kind || o.kind === q.kind) && (!q.locationId || o.locationId === q.locationId || o.toLocationId === q.locationId))
        .filter((o) => Date.parse(o.createdAt) >= from && Date.parse(o.createdAt) <= to)
        .sort((a, b) => b.seq - a.seq)
        .map(toSummary);
      return wait(page(list, q));
    },

    async getOperation(id: string): Promise<Operation> {
      const op = operations.find((o) => o.id === id);
      if (!op || !(inScope(op.locationId) || (op.toLocationId !== null && inScope(op.toLocationId)))) throw err(404, "operation_not_found");
      return wait(toOperation(op));
    },

    async createReceipt(r: ReceiptRequest, key: string): Promise<Operation> {
      requireManage();
      const replayed = replay(key, r);
      if (replayed) return wait(replayed);
      rateLimit();
      checkCostsRight(r.lines.some((l) => l.unitCost !== undefined));
      if (r.lines.length < 1 || r.lines.length > 100) throw err(422, "invalid_lines");
      checkNote(r.note);
      const loc = locationOf(r.locationId);
      const today = todayOf(loc);
      if (r.receivedOn !== undefined && (!isYmd(r.receivedOn) || r.receivedOn > today)) throw err(422, "invalid_date");
      if ((r.documentNumber ?? "").length > 64) throw err(422, "invalid_document_number");
      r.lines.forEach((l, i) => {
        const p = productById(l.productId);
        if (p) checkQuantity(l.quantity, p.unit, i);
        if (l.unitCost !== undefined && (l.unitCost < 0 || l.unitCost > 1_000_000 || !decimalsOk(l.unitCost, 2))) throw err(422, "invalid_price", { line: i });
        if (l.expiresOn !== undefined && !isYmd(l.expiresOn)) throw err(422, "invalid_date", { line: i });
        if ((l.lotNumber ?? "").length > 64) throw err(422, "invalid_lot_number", { line: i });
      });
      r.lines.forEach((l, i) => {
        const p = productById(l.productId);
        if (!p) throw err(404, "product_not_found", { line: i });
        if (!p.isActive) throw err(422, "product_inactive", { line: i });
        if (!enabledAt(p, loc.id)) throw err(422, "product_disabled_for_location", { line: i });
        if (l.expiresOn !== undefined && l.expiresOn < today) throw err(422, "invalid_expiry", { line: i });
        if (p.trackExpiry && !l.expiresOn) throw err(422, "expiry_required", { line: i });
      });
      if (loc.isActive === false) throw err(422, "location_inactive");
      if (r.supplierId !== undefined) {
        const s = suppliers.find((x) => x.id === r.supplierId);
        if (!s) throw err(404, "supplier_not_found");
        if (!s.isActive) throw err(422, "supplier_inactive");
      }
      const op = newOperation({ kind: "receipt", locationId: loc.id, supplierId: r.supplierId ?? null, documentNumber: r.documentNumber?.trim() || null, receivedOn: r.receivedOn ?? null, note: r.note?.trim() || null });
      for (const l of r.lines) {
        const p = productById(l.productId)!;
        const b = addBatch(op, p, loc.id, l.quantity, {
          expiresOn: l.expiresOn ?? null,
          lotNumber: l.lotNumber?.trim() || null,
          supplierId: r.supplierId ?? p.supplierId,
          unitCost: l.unitCost !== undefined ? l.unitCost : p.purchasePrice,
        });
        pushMovement(op, { type: "receipt", locationId: loc.id, productId: p.id, batchId: b.id, quantity: l.quantity, unitCost: b.unitCost, reason: null, counterpartLocationId: null });
      }
      idem.set(key, { hash: JSON.stringify(r), opId: op.id });
      return wait(toOperation(op));
    },

    async createWriteOff(r: WriteOffRequest, key: string): Promise<Operation> {
      requireManage();
      const replayed = replay(key, r);
      if (replayed) return wait(replayed);
      rateLimit();
      if (r.lines.length < 1 || r.lines.length > 100) throw err(422, "invalid_lines");
      if (!["expired", "damaged", "lost", "internal_use", "correction", "other"].includes(r.reason)) throw err(422, "invalid_reason");
      if ((r.reason === "correction" || r.reason === "other") && !r.note?.trim()) throw err(422, "note_required");
      checkNote(r.note);
      const loc = locationOf(r.locationId);
      const today = todayOf(loc);
      assertNoDuplicates(r.lines);
      const remaining = new Map<string, number>(batches.filter((b) => b.locationId === loc.id).map((b) => [b.id, toMilli(b.quantity)]));
      const plan: { p: ProductRec; batch: BatchRec; qty: number }[] = [];
      const shortages: { line: number; productId: string; batchId?: string; requested: number; available: number }[] = [];
      r.lines.forEach((l, i) => {
        const p = productById(l.productId);
        if (!p) throw err(404, "product_not_found", { line: i });
        checkQuantity(l.quantity, p.unit, i);
        if (l.batchId) {
          const b = batches.find((x) => x.id === l.batchId && x.locationId === loc.id && x.productId === p.id);
          if (!b) throw err(404, "batch_not_found", { line: i });
          const left = remaining.get(b.id)!;
          if (left < toMilli(l.quantity)) shortages.push({ line: i, productId: p.id, batchId: b.id, requested: l.quantity, available: fromMilli(left) });
          else {
            remaining.set(b.id, left - toMilli(l.quantity));
            plan.push({ p, batch: b, qty: l.quantity });
          }
          return;
        }
        const pool = batches.filter((b) => b.locationId === loc.id && b.productId === p.id).map((b) => ({ ...b, quantity: fromMilli(remaining.get(b.id)!) }));
        const f = planFefo(pool, l.quantity, today);
        if (f.shortage > 0) shortages.push({ line: i, productId: p.id, requested: l.quantity, available: f.available });
        else
          for (const t of f.takes) {
            remaining.set(t.batchId, remaining.get(t.batchId)! - toMilli(t.quantity));
            plan.push({ p, batch: batches.find((b) => b.id === t.batchId)!, qty: t.quantity });
          }
      });
      if (shortages.length > 0) throw err(422, "insufficient_stock", { lines: shortages });
      const op = newOperation({ kind: "write_off", locationId: loc.id, reason: r.reason, note: r.note?.trim() || null });
      for (const t of plan) {
        t.batch.quantity = fromMilli(toMilli(t.batch.quantity) - toMilli(t.qty));
        pushMovement(op, { type: "write_off", locationId: loc.id, productId: t.p.id, batchId: t.batch.id, quantity: -t.qty, unitCost: t.batch.unitCost, reason: r.reason, counterpartLocationId: null });
      }
      idem.set(key, { hash: JSON.stringify(r), opId: op.id });
      return wait(toOperation(op));
    },

    async createTransfer(r: TransferRequest, key: string): Promise<Operation> {
      requireManage();
      const replayed = replay(key, r);
      if (replayed) return wait(replayed);
      rateLimit();
      if (r.lines.length < 1 || r.lines.length > 100) throw err(422, "invalid_lines");
      checkNote(r.note);
      const from = locationOf(r.fromLocationId);
      const to = locationOf(r.toLocationId);
      if (from.id === to.id) throw err(422, "same_location");
      if (to.isActive === false) throw err(422, "location_inactive");
      const today = todayOf(from);
      assertNoDuplicates(r.lines);
      const remaining = new Map<string, number>(batches.filter((b) => b.locationId === from.id).map((b) => [b.id, toMilli(b.quantity)]));
      const plan: { p: ProductRec; batch: BatchRec; qty: number }[] = [];
      const shortages: { line: number; productId: string; batchId?: string; requested: number; available: number }[] = [];
      r.lines.forEach((l, i) => {
        const p = productById(l.productId);
        if (!p) throw err(404, "product_not_found", { line: i });
        checkQuantity(l.quantity, p.unit, i);
        if (!enabledAt(p, to.id)) throw err(422, "product_disabled_for_location", { line: i });
        if (l.batchId) {
          const b = batches.find((x) => x.id === l.batchId && x.locationId === from.id && x.productId === p.id);
          if (!b) throw err(404, "batch_not_found", { line: i });
          if (b.expiresOn !== null && b.expiresOn < today) throw err(422, "batch_expired", { line: i });
          const left = remaining.get(b.id)!;
          if (left < toMilli(l.quantity)) shortages.push({ line: i, productId: p.id, batchId: b.id, requested: l.quantity, available: fromMilli(left) });
          else {
            remaining.set(b.id, left - toMilli(l.quantity));
            plan.push({ p, batch: b, qty: l.quantity });
          }
          return;
        }
        const pool = batches.filter((b) => b.locationId === from.id && b.productId === p.id).map((b) => ({ ...b, quantity: fromMilli(remaining.get(b.id)!) }));
        const f = planFefo(pool, l.quantity, today, { skipExpired: true });
        if (f.shortage > 0) shortages.push({ line: i, productId: p.id, requested: l.quantity, available: f.available });
        else
          for (const t of f.takes) {
            remaining.set(t.batchId, remaining.get(t.batchId)! - toMilli(t.quantity));
            plan.push({ p, batch: batches.find((b) => b.id === t.batchId)!, qty: t.quantity });
          }
      });
      if (shortages.length > 0) throw err(422, "insufficient_stock", { lines: shortages });
      const op = newOperation({ kind: "transfer", locationId: from.id, toLocationId: to.id, note: r.note?.trim() || null });
      for (const t of plan) {
        t.batch.quantity = fromMilli(toMilli(t.batch.quantity) - toMilli(t.qty));
        pushMovement(op, { type: "transfer_out", locationId: from.id, productId: t.p.id, batchId: t.batch.id, quantity: -t.qty, unitCost: t.batch.unitCost, reason: null, counterpartLocationId: to.id });
        const nb = addBatch(op, t.p, to.id, t.qty, {
          sourceBatchId: t.batch.id,
          expiresOn: t.batch.expiresOn,
          lotNumber: t.batch.lotNumber,
          unitCost: t.batch.unitCost,
          supplierId: t.batch.supplierId,
          receivedAt: t.batch.receivedAt,
        });
        pushMovement(op, { type: "transfer_in", locationId: to.id, productId: t.p.id, batchId: nb.id, quantity: t.qty, unitCost: nb.unitCost, reason: null, counterpartLocationId: from.id });
      }
      idem.set(key, { hash: JSON.stringify(r), opId: op.id });
      return wait(toOperation(op));
    },

    async getStocktakes(q: StocktakeQuery = {}): Promise<Page<StocktakeSummary>> {
      if (q.locationId) locationOf(q.locationId);
      const list = stocktakes
        .filter((s) => inScope(s.locationId) && (!q.locationId || s.locationId === q.locationId) && (!q.status || s.status === q.status))
        .sort((a, b) => b.createdAt.localeCompare(a.createdAt) || b.id.localeCompare(a.id, "en", { numeric: true }))
        .map(stocktakeSummary);
      return wait(page(list, q));
    },

    async createStocktake(input: { locationId: string; note?: string; productIds?: string[] }): Promise<Stocktake> {
      requireManage();
      rateLimit();
      checkNote(input.note);
      const loc = locationOf(input.locationId);
      const open = stocktakes.find((s) => s.locationId === loc.id && s.status === "draft");
      if (open) throw err(409, "stocktake_in_progress", { stocktakeId: open.id });
      const ids = input.productIds ?? products.filter((p) => (p.isActive && enabledAt(p, loc.id)) || levelOf(loc.id, p.id) > 0).map((p) => p.id);
      if (ids.length > 2000) throw err(422, "too_many_lines");
      for (const id of ids) if (!productById(id)) throw err(404, "product_not_found");
      const rec: StocktakeRec = {
        id: nextId("st"),
        locationId: loc.id,
        status: "draft",
        note: input.note?.trim() || null,
        version: 1,
        createdAt: iso(),
        postedAt: null,
        cancelledAt: null,
        lines: ids.map((productId) => ({ productId, counted: null, expected: null, surplusExpiresOn: null })),
      };
      stocktakes.push(rec);
      return wait(stocktakeDto(rec));
    },

    async getStocktake(id: string): Promise<Stocktake> {
      return wait(stocktakeDto(stocktakeOf(id)));
    },

    async saveStocktakeLines(id: string, expectedVersion: number, lines: StocktakeLineInput[]): Promise<Stocktake> {
      requireManage();
      rateLimit();
      const st = stocktakeOf(id);
      if (typeof expectedVersion !== "number") throw err(422, "expected_version_required");
      if (st.status !== "draft") throw err(409, "stocktake_closed");
      if (st.version !== expectedVersion) throw err(409, "stocktake_version_conflict", { currentVersion: st.version });
      if (lines.length < 1 || lines.length > 500) throw err(422, "invalid_lines");
      const seen = new Set<string>();
      lines.forEach((l, i) => {
        const p = productById(l.productId);
        if (!p) throw err(404, "product_not_found", { line: i });
        if (seen.has(l.productId)) throw err(422, "duplicate_line", { line: i });
        seen.add(l.productId);
        if (l.countedQuantity !== null) checkQuantity(l.countedQuantity, p.unit, i, true);
        if (l.surplusExpiresOn && !isYmd(l.surplusExpiresOn)) throw err(422, "invalid_date", { line: i });
      });
      for (const l of lines) {
        const cur = st.lines.find((x) => x.productId === l.productId);
        if (cur) {
          cur.counted = l.countedQuantity;
          cur.surplusExpiresOn = l.surplusExpiresOn ?? null;
        } else st.lines.push({ productId: l.productId, counted: l.countedQuantity, expected: null, surplusExpiresOn: l.surplusExpiresOn ?? null });
      }
      st.version += 1;
      return wait(stocktakeDto(st));
    },

    async postStocktake(id: string, expectedVersion: number): Promise<StocktakePostResult> {
      requireManage();
      rateLimit();
      const st = stocktakeOf(id);
      if (st.status !== "draft") throw err(409, "stocktake_closed");
      if (st.version !== expectedVersion) throw err(409, "stocktake_version_conflict", { currentVersion: st.version });
      const counted = st.lines.filter((l) => l.counted !== null);
      if (counted.length === 0) throw err(422, "stocktake_empty");
      const loc = locationOf(st.locationId);
      const today = todayOf(loc);
      // перевірка до змін: надлишок товару з терміном потребує дати
      counted.forEach((l, i) => {
        const p = productById(l.productId)!;
        const diff = toMilli(l.counted!) - toMilli(levelOf(loc.id, p.id));
        if (diff > 0 && p.trackExpiry && !l.surplusExpiresOn) throw err(422, "expiry_required", { line: i });
      });
      let op: OperationRec | null = null;
      for (const l of counted) {
        const p = productById(l.productId)!;
        const level = levelOf(loc.id, p.id);
        l.expected = level;
        const sh = shortageOf(loc.id, p.id);
        l.expectedShortage = sh;
        if (sh > 0) {
          op ??= newOperation({ kind: "stocktake", locationId: loc.id, stocktakeId: st.id, note: st.note });
          pushMovement(op, { type: "stocktake_adjust", locationId: loc.id, productId: p.id, batchId: null, isShortage: true, quantity: sh, unitCost: null, reason: null, counterpartLocationId: null });
          shortages.delete(`${loc.id}|${p.id}`);
        }
        const diff = toMilli(l.counted!) - toMilli(level);
        if (diff === 0) continue;
        op ??= newOperation({ kind: "stocktake", locationId: loc.id, stocktakeId: st.id, note: st.note });
        if (diff > 0) {
          const b = addBatch(op, p, loc.id, fromMilli(diff), { expiresOn: l.surplusExpiresOn });
          pushMovement(op, { type: "stocktake_adjust", locationId: loc.id, productId: p.id, batchId: b.id, quantity: fromMilli(diff), unitCost: b.unitCost, reason: null, counterpartLocationId: null });
        } else {
          const f = planFefo(batches.filter((b) => b.locationId === loc.id && b.productId === p.id), fromMilli(-diff), today);
          for (const t of f.takes) {
            const b = batches.find((x) => x.id === t.batchId)!;
            b.quantity = fromMilli(toMilli(b.quantity) - toMilli(t.quantity));
            pushMovement(op, { type: "stocktake_adjust", locationId: loc.id, productId: p.id, batchId: b.id, quantity: -t.quantity, unitCost: b.unitCost, reason: null, counterpartLocationId: null });
          }
        }
      }
      st.status = "posted";
      st.postedAt = iso();
      st.version += 1;
      return wait({ stocktake: stocktakeDto(st), operation: op ? toOperation(op) : null });
    },

    async cancelStocktake(id: string, expectedVersion: number): Promise<Stocktake> {
      requireManage();
      rateLimit();
      const st = stocktakeOf(id);
      if (st.status !== "draft") throw err(409, "stocktake_closed");
      if (st.version !== expectedVersion) throw err(409, "stocktake_version_conflict", { currentVersion: st.version });
      st.status = "cancelled";
      st.cancelledAt = iso();
      st.version += 1;
      return wait(stocktakeDto(st));
    },
  };

  // ---------- допоміжні, що використовують стан ----------
  function stocktakeOf(id: string): StocktakeRec {
    const st = stocktakes.find((s) => s.id === id);
    if (!st || !inScope(st.locationId)) throw err(404, "stocktake_not_found");
    return st;
  }
  const stocktakeSummary = (s: StocktakeRec): StocktakeSummary => ({
    id: s.id,
    locationId: s.locationId,
    status: s.status,
    createdAt: s.createdAt,
    createdBy: me,
    postedAt: s.postedAt,
    lineCount: s.lines.length,
    countedCount: s.lines.filter((l) => l.counted !== null).length,
  });
  function stocktakeDto(s: StocktakeRec): Stocktake {
    return {
      ...stocktakeSummary(s),
      note: s.note,
      version: s.version,
      postedBy: s.postedAt ? me : null,
      cancelledAt: s.cancelledAt,
      lines: s.lines
        .map((l) => {
          const p = productById(l.productId)!;
          const expected = s.status === "draft" ? levelOf(s.locationId, p.id) : l.expected;
          return {
            product: productRef(p),
            expectedQuantity: expected,
            expectedShortage: s.status === "draft" ? shortageOf(s.locationId, p.id) : (l.expectedShortage ?? 0),
            countedQuantity: l.counted,
            difference: l.counted === null || expected === null ? null : fromMilli(toMilli(l.counted) - (toMilli(expected) - toMilli(s.status === "draft" ? shortageOf(s.locationId, p.id) : (l.expectedShortage ?? 0)))),
            surplusExpiresOn: l.surplusExpiresOn,
          };
        })
        .sort((a, b) => byName(a.product, b.product)),
    };
  }
  function assertNoDuplicates(lines: { productId: string; batchId?: string }[]) {
    const seenKeys = new Set<string>();
    lines.forEach((l, i) => {
      const k = `${l.productId}|${l.batchId ?? ""}`;
      if (seenKeys.has(k)) throw err(422, "duplicate_line", { line: i });
      seenKeys.add(k);
    });
  }
  function checkMinStock(v: number, unit: StockUnit) {
    if (v < 0 || v > 1_000_000 || !decimalsOk(v, 3) || (isIntegerUnit(unit) && !Number.isInteger(v))) throw err(422, "invalid_min_stock");
  }
  function applyProduct(p: ProductRec, r: ProductRequest, isUpdate: boolean) {
    const name = (r.name ?? "").trim();
    if (name.length < 1 || name.length > 200) throw err(422, "invalid_name");
    // Дерево: `categoryId` відсутнє = без змін; `null` = очистити. Архівну категорію можна лише лишити як є.
    let categoryId = p.categoryId;
    if (r.categoryId !== undefined) {
      if (r.categoryId === null) categoryId = null;
      else {
        const c = categoryById(r.categoryId);
        if (!c) throw err(422, "category_not_found");
        if (c.isArchived && r.categoryId !== p.categoryId) throw err(422, "category_archived");
        categoryId = r.categoryId;
      }
    }
    if (!["pcs", "ml", "g", "l", "kg", "pack"].includes(r.unit)) throw err(422, "invalid_unit");
    if (isUpdate && r.unit !== p.unit && hasMovements(p.id)) throw err(422, "unit_locked");
    const barcode = (r.barcode ?? "").trim();
    if (barcode && !/^[0-9A-Za-z._-]{1,64}$/.test(barcode)) throw err(422, "invalid_barcode");
    if (!["sale", "salon_use", "both"].includes(r.purpose)) throw err(422, "invalid_purpose");
    if (r.purchasePrice !== undefined && r.purchasePrice !== null && (r.purchasePrice < 0 || r.purchasePrice > 1_000_000 || !decimalsOk(r.purchasePrice, 2))) throw err(422, "invalid_price");
    const sold = r.purpose !== "salon_use";
    const salePrice = sold ? (r.salePrice ?? null) : null;
    if (sold && salePrice === null) throw err(422, "sale_price_required");
    if (salePrice !== null && (salePrice < 0 || salePrice > 1_000_000 || !decimalsOk(salePrice, 2))) throw err(422, "invalid_price");
    if (r.minStock !== undefined && r.minStock !== null) checkMinStock(r.minStock, r.unit);
    if ((r.note ?? "").trim().length > 500) throw err(422, "invalid_note");
    if (r.supplierId && !suppliers.some((s) => s.id === r.supplierId)) throw err(404, "supplier_not_found");
    if (barcode && products.some((x) => x.id !== p.id && x.barcode === barcode)) throw err(409, "barcode_taken");
    p.name = name;
    p.categoryId = categoryId;
    p.unit = r.unit;
    p.barcode = barcode || null;
    p.purpose = r.purpose;
    p.supplierId = r.supplierId || null;
    if (r.purchasePrice !== undefined && r.purchasePrice !== null) p.purchasePrice = r.purchasePrice;
    p.salePrice = salePrice;
    p.minStock = r.minStock ?? null;
    if (r.trackExpiry !== undefined && r.trackExpiry !== null) p.trackExpiry = r.trackExpiry;
    if (isUpdate && r.isActive !== undefined && r.isActive !== null) p.isActive = r.isActive;
    p.note = (r.note ?? "").trim() || null;
    p.updatedAt = iso();
  }
  function applySupplier(s: SupplierRec, r: SupplierRequest, isUpdate: boolean) {
    const name = (r.name ?? "").trim();
    if (name.length < 1 || name.length > 200) throw err(422, "invalid_name");
    if ((r.contactName ?? "").trim().length > 200) throw err(422, "invalid_contact_name");
    const phone = (r.phone ?? "").trim();
    if (phone && (phone.length > 32 || !/^[0-9+()\-. ]+$/.test(phone))) throw err(422, "invalid_phone");
    const email = (r.email ?? "").trim();
    if (email && (email.length > 320 || !email.includes("@"))) throw err(422, "invalid_email");
    if ((r.note ?? "").trim().length > 500) throw err(422, "invalid_note");
    if (suppliers.some((x) => x.id !== s.id && x.name.toLowerCase() === name.toLowerCase())) throw err(409, "supplier_name_taken");
    s.name = name;
    s.contactName = (r.contactName ?? "").trim() || null;
    s.phone = phone || null;
    s.email = email || null;
    s.note = (r.note ?? "").trim() || null;
    if (isUpdate && r.isActive !== undefined && r.isActive !== null) s.isActive = r.isActive;
  }

  peeks.set(api, () => {
    const out: AlertSeed = { lows: [], expiring: [], shortages: [] };
    for (const l of allLocations().filter((x) => x.isActive !== false)) {
      const today = todayOf(l);
      for (const p of products.filter((x) => x.isActive && enabledAt(x, l.id))) {
        const quantity = levelOf(l.id, p.id);
        const balance = fromMilli(toMilli(quantity) - toMilli(shortageOf(l.id, p.id)));
        const min = effectiveMin(p, l.id);
        const has = batches.some((b) => b.locationId === l.id && b.productId === p.id) || shortageOf(l.id, p.id) > 0;
        if (has && min !== null && isLow(balance, min)) out.lows.push({ location: { id: l.id, name: l.name }, product: productRef(p), balance, threshold: min });
      }
      for (const b of batches.filter((x) => x.locationId === l.id && x.quantity > 0 && x.expiresOn)) {
        const days = daysToExpiry(b.expiresOn, today);
        if (days !== null && days <= EXPIRING_SOON_DAYS) out.expiring.push({ location: { id: l.id, name: l.name }, product: productRef(productById(b.productId)!), batchId: b.id, expiresOn: b.expiresOn!, lotNumber: b.lotNumber, quantity: b.quantity, days });
      }
    }
    for (const [key, qty] of shortages) {
      const [lid, pid] = key.split("|") as [string, string];
      const m = movements.find((x) => x.isShortage && x.locationId === lid && x.productId === pid && x.type === "service_consumption");
      const l = locationById(lid);
      if (m && l) out.shortages.push({ location: { id: l.id, name: l.name }, product: productRef(productById(pid)!), operationId: m.operationId, quantity: qty });
    }
    return out;
  });
  return api;
}

/** Знімок для демо-сповіщень (§32.19): те, що воркер знайшов би в поточному стані складу, без урахування ролі. */
export interface AlertSeed {
  lows: { location: NamedRef; product: ProductRef; balance: number; threshold: number }[];
  expiring: { location: NamedRef; product: ProductRef; batchId: string; expiresOn: string; lotNumber: string | null; quantity: number; days: number }[];
  shortages: { location: NamedRef; product: ProductRef; operationId: string; quantity: number }[];
}
const peeks = new WeakMap<object, () => AlertSeed>();
export const peekInventory = (api: object): AlertSeed => peeks.get(api)?.() ?? { lows: [], expiring: [], shortages: [] };

export type MockInventory = ReturnType<typeof createMockInventory>;
