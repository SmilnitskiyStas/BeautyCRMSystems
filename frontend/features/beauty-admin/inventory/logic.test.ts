import { describe, expect, it } from "vitest";
import { BeautyApiError } from "@/features/beauty-auth/errors";
import {
  ALERT_KIND_LABEL,
  alertBadgeText,
  alertSummary,
  balanceLabel,
  balanceOf,
  canBeNorm,
  daysBucketFor,
  isLowBalance,
  movementLabel,
  validateMaterialDraft,
  batchStatus,
  daysToExpiry,
  emptyLine,
  expiryLabel,
  fefoOrder,
  formatNumber,
  formatQuantity,
  formatSigned,
  humanizeInventoryError,
  isLow,
  isYmd,
  nextAttempt,
  parsePrice,
  parseQuantity,
  planFefo,
  shortageLines,
  stocktakeDifference,
  toProductRequest,
  validateLines,
  validateProduct,
  validateReceiptHeader,
  validateSupplier,
  validateWriteOffHeader,
  type ProductFormValues,
} from "./logic";

// Усі дати відносно фіксованого «сьогодні» (жодного реального годинника).
const TODAY = "2026-10-14";

describe("кількості: ціле для шт/уп, десяткова кома, межі", () => {
  it("шт і уп - лише цілі", () => {
    expect(parseQuantity("5", "pcs")).toEqual({ value: 5 });
    expect(parseQuantity("2,5", "pcs").error).toMatch(/цілою/);
    expect(parseQuantity("1.0", "pack").error).toMatch(/цілою/);
    expect(parseQuantity("2,5", "l")).toEqual({ value: 2.5 });
    expect(parseQuantity("0,125", "kg")).toEqual({ value: 0.125 });
  });
  it("більше нуля (для підрахунку - можна нуль), до 3 знаків, до 1 000 000", () => {
    expect(parseQuantity("0", "pcs").error).toBeTruthy();
    expect(parseQuantity("0", "pcs", { allowZero: true })).toEqual({ value: 0 });
    expect(parseQuantity("1,2345", "l").error).toMatch(/3 знаків/);
    expect(parseQuantity("1000001", "g").error).toMatch(/1 000 000/);
    expect(parseQuantity("abc", "g").error).toMatch(/числом/);
    expect(parseQuantity(" ", "g").error).toMatch(/Вкажіть/);
    expect(parseQuantity("-1", "g").error).toBeTruthy();
  });
  it("ціна: до 2 знаків, 0..1 000 000, порожнє = null", () => {
    expect(parsePrice("")).toEqual({ value: null });
    expect(parsePrice("320,50")).toEqual({ value: 320.5 });
    expect(parsePrice("1,234").error).toMatch(/2 знаків/);
    expect(parsePrice("2000000").error).toBeTruthy();
  });
  it("форматування: десяткова кома й одиниці", () => {
    expect(formatNumber(1.5)).toBe("1,5");
    expect(formatNumber(1234.25)).toBe("1 234,25");
    expect(formatQuantity(12, "pcs")).toBe("12 шт");
    expect(formatQuantity(0.5, "l")).toBe("0,5 л");
    expect(formatSigned(-2, "pack")).toBe("−2 уп");
    expect(formatSigned(3, "ml")).toBe("+3 мл");
  });
});

describe("терміни придатності від переданої дати", () => {
  it("daysToExpiry й статус партії: межі прострочено/закінчується", () => {
    expect(daysToExpiry("2026-10-14", TODAY)).toBe(0);
    expect(daysToExpiry("2026-10-13", TODAY)).toBe(-1);
    expect(daysToExpiry(null, TODAY)).toBeNull();
    const b = (expiresOn: string | null, quantity = 5) => ({ expiresOn, quantity });
    expect(batchStatus(b("2026-10-13"), TODAY, 30)).toBe("expired");
    expect(batchStatus(b("2026-10-14"), TODAY, 30)).toBe("expiring"); // сьогодні ще не прострочено
    expect(batchStatus(b("2026-11-13"), TODAY, 30)).toBe("expiring"); // рівно +30
    expect(batchStatus(b("2026-11-14"), TODAY, 30)).toBe("active"); // +31
    expect(batchStatus(b(null), TODAY, 30)).toBe("active");
    expect(batchStatus(b("2026-10-01", 0), TODAY, 30)).toBe("empty"); // порожня має пріоритет
  });
  it("підписи терміну не лише кольором", () => {
    expect(expiryLabel("2026-10-12", TODAY)).toBe("Прострочено 2 доби тому");
    expect(expiryLabel("2026-10-14", TODAY)).toBe("Спливає сьогодні");
    expect(expiryLabel("2026-10-15", TODAY)).toBe("Залишилось 1 доба");
    expect(expiryLabel("2026-10-19", TODAY)).toBe("Залишилось 5 діб");
    expect(expiryLabel(null, TODAY)).toBe("Без терміну");
  });
  it("isYmd: формат, роки 2000..2100, неіснуючі дати", () => {
    expect(isYmd("2026-02-28")).toBe(true);
    expect(isYmd("2026-02-30")).toBe(false);
    expect(isYmd("1999-12-31")).toBe(false);
    expect(isYmd("2026-1-1")).toBe(false);
  });
  it("isLow: поріг > 0 і залишок строго менший", () => {
    expect(isLow(4, 5)).toBe(true);
    expect(isLow(5, 5)).toBe(false);
    expect(isLow(0, 0)).toBe(false);
    expect(isLow(0, null)).toBe(false);
  });
});

describe("FEFO", () => {
  const batches = [
    { id: "b3", expiresOn: null, receivedAt: "2026-09-01T00:00:00Z", quantity: 5 },
    { id: "b2", expiresOn: "2026-12-01", receivedAt: "2026-09-02T00:00:00Z", quantity: 5 },
    { id: "b1", expiresOn: "2026-10-01", receivedAt: "2026-09-03T00:00:00Z", quantity: 5 }, // прострочена
    { id: "b4", expiresOn: "2026-12-01", receivedAt: "2026-09-01T00:00:00Z", quantity: 5 },
    { id: "b0", expiresOn: "2026-11-01", receivedAt: "2026-08-01T00:00:00Z", quantity: 0 }, // порожня
  ];
  it("порядок: expires_on ASC NULLS LAST, received_at, id; порожні відкинуто", () => {
    expect(fefoOrder(batches).map((b) => b.id)).toEqual(["b1", "b4", "b2", "b3"]);
  });
  it("списання бере прострочені першими й розходиться на кілька партій", () => {
    const p = planFefo(batches, 8, TODAY);
    expect(p.takes).toEqual([
      { batchId: "b1", quantity: 5 },
      { batchId: "b4", quantity: 3 },
    ]);
    expect(p.shortage).toBe(0);
  });
  it("переміщення (skipExpired) пропускає прострочені й рахує available без них", () => {
    const p = planFefo(batches, 8, TODAY, { skipExpired: true });
    expect(p.takes[0]).toEqual({ batchId: "b4", quantity: 5 });
    expect(p.available).toBe(15);
    const big = planFefo(batches, 16, TODAY, { skipExpired: true });
    expect(big.shortage).toBe(1);
  });
  it("дробові кількості без похибки плаваючої коми", () => {
    const p = planFefo([{ id: "x", expiresOn: null, receivedAt: "a", quantity: 0.3 }], 0.1 + 0.2, TODAY);
    expect(p.shortage).toBe(0);
  });
});

const product = (over: Partial<ProductFormValues> = {}): ProductFormValues => ({
  name: "Шампунь",
  category: "",
  unit: "pcs",
  barcode: "",
  purpose: "both",
  supplierId: "",
  purchasePrice: "",
  salePrice: "10",
  minStock: "",
  trackExpiry: false,
  isActive: true,
  note: "",
  ...over,
});

describe("валідація форми товару", () => {
  it("назва, штрихкод, мін. залишок (ціле для шт), примітка", () => {
    expect(validateProduct(product(), { canEditCosts: true })).toEqual({});
    expect(validateProduct(product({ name: " " }), { canEditCosts: true }).name).toBeTruthy();
    expect(validateProduct(product({ barcode: "ab cd" }), { canEditCosts: true }).barcode).toBeTruthy();
    expect(validateProduct(product({ barcode: "4820-A_1.2" }), { canEditCosts: true }).barcode).toBeUndefined();
    expect(validateProduct(product({ minStock: "2,5" }), { canEditCosts: true }).minStock).toMatch(/цілою/);
    expect(validateProduct(product({ unit: "l", minStock: "2,5" }), { canEditCosts: true }).minStock).toBeUndefined();
    expect(validateProduct(product({ note: "x".repeat(501) }), { canEditCosts: true }).note).toBeTruthy();
  });
  it("ціна перевіряється лише з правом costs=edit", () => {
    expect(validateProduct(product({ purchasePrice: "abc" }), { canEditCosts: true }).purchasePrice).toBeTruthy();
    expect(validateProduct(product({ purchasePrice: "abc" }), { canEditCosts: false }).purchasePrice).toBeUndefined();
  });
  it("роздрібна ціна: обов'язкова для sale/both, не для salon_use, від'ємна/некоректна - помилка", () => {
    const o = { canEditCosts: false };
    expect(validateProduct(product({ purpose: "sale", salePrice: "" }), o).salePrice).toBeTruthy();
    expect(validateProduct(product({ purpose: "both", salePrice: " " }), o).salePrice).toBeTruthy();
    expect(validateProduct(product({ purpose: "salon_use", salePrice: "" }), o).salePrice).toBeUndefined();
    expect(validateProduct(product({ purpose: "both", salePrice: "-5" }), o).salePrice).toBeTruthy();
    expect(validateProduct(product({ purpose: "both", salePrice: "1,234" }), o).salePrice).toBeTruthy();
    expect(validateProduct(product({ purpose: "both", salePrice: "0" }), o).salePrice).toBeUndefined();
    expect(validateProduct(product({ purpose: "both", salePrice: "99,90" }), o).salePrice).toBeUndefined();
  });
  it("payload: salePrice числом для sale/both, null для salon_use, незалежно від costs", () => {
    expect(toProductRequest(product({ purpose: "both", salePrice: "99,9" }), { canEditCosts: false, isEdit: false })).toMatchObject({ salePrice: 99.9 });
    expect(toProductRequest(product({ purpose: "salon_use", salePrice: "5" }), { canEditCosts: true, isEdit: true })).toMatchObject({ salePrice: null });
  });
  it("помилка 422 sale_price_required має зрозуміле повідомлення", () => {
    expect(humanizeInventoryError(new BeautyApiError(422, "sale_price_required", "x"))).toMatch(/роздрібну ціну/);
  });
  it("тіло запиту: без права ціна не передається; isActive лише в редагуванні; порожнє -> null", () => {
    const withPrice = product({ purchasePrice: "12,5", category: " Гігієна ", minStock: "3" });
    expect(toProductRequest(withPrice, { canEditCosts: true, isEdit: false })).toMatchObject({ purchasePrice: 12.5, category: "Гігієна", minStock: 3 });
    const noRight = toProductRequest(withPrice, { canEditCosts: false, isEdit: false });
    expect("purchasePrice" in noRight).toBe(false);
    expect("isActive" in noRight).toBe(false);
    expect(toProductRequest(product(), { canEditCosts: false, isEdit: true })).toMatchObject({ isActive: true, barcode: null, supplierId: null, category: null });
  });
  it("постачальник: телефон, пошта, довжини", () => {
    const ok = { name: "ТОВ", contactName: "", phone: "+380 (44) 123-45-67", email: "a@b.ua", note: "" };
    expect(validateSupplier(ok)).toEqual({});
    expect(validateSupplier({ ...ok, phone: "tel: 123" }).phone).toBeTruthy();
    expect(validateSupplier({ ...ok, email: "abc" }).email).toBeTruthy();
    expect(validateSupplier({ ...ok, name: "" }).name).toBeTruthy();
  });
});

describe("валідація рядків операцій", () => {
  const units = { pcs1: "pcs", ml1: "ml" } as const;
  const ctx = { unitOf: (id: string) => (units as Record<string, "pcs" | "ml">)[id], trackExpiry: (id: string) => id === "ml1" };
  const line = (over: Partial<ReturnType<typeof emptyLine>>) => ({ ...emptyLine("k"), ...over });

  it("надходження: товар, кількість, термін (обов'язковий для trackExpiry, не в минулому), ціна", () => {
    const opts = { mode: "receipt", today: TODAY, canEditCosts: true } as const;
    expect(validateLines([line({})], ctx, opts)[0]?.productId).toBeTruthy();
    expect(validateLines([line({ productId: "pcs1", quantity: "2,5" })], ctx, opts)[0]?.quantity).toMatch(/цілою/);
    expect(validateLines([line({ productId: "ml1", quantity: "10" })], ctx, opts)[0]?.expiresOn).toMatch(/Вкажіть/);
    expect(validateLines([line({ productId: "ml1", quantity: "10", expiresOn: "2026-10-13" })], ctx, opts)[0]?.expiresOn).toMatch(/минув/);
    expect(validateLines([line({ productId: "ml1", quantity: "10", expiresOn: TODAY })], ctx, opts)).toEqual({});
    expect(validateLines([line({ productId: "pcs1", quantity: "1", unitCost: "x" })], ctx, opts)[0]?.unitCost).toBeTruthy();
    expect(validateLines([line({ productId: "pcs1", quantity: "1", unitCost: "x" })], ctx, { ...opts, canEditCosts: false })).toEqual({});
  });
  it("списання/переміщення: повтор (товар, партія) заборонено", () => {
    const opts = { mode: "stock", today: TODAY } as const;
    const r = validateLines([line({ productId: "pcs1", quantity: "1", batchId: "b" }), line({ productId: "pcs1", quantity: "1", batchId: "b" })], ctx, opts);
    expect(r[0]).toBeUndefined();
    expect(r[1]?.productId).toMatch(/уже додано/);
    expect(validateLines([line({ productId: "pcs1", quantity: "1" }), line({ productId: "pcs1", quantity: "1", batchId: "b" })], ctx, opts)).toEqual({});
  });
  it("шапка надходження: дата не в майбутньому й не старіша за рік", () => {
    const h = (receivedOn: string) => validateReceiptHeader({ receivedOn, documentNumber: "", note: "" }, TODAY);
    expect(h("")).toEqual({});
    expect(h(TODAY)).toEqual({});
    expect(h("2026-10-15").receivedOn).toBeTruthy();
    expect(h("2025-10-14")).toEqual({});
    expect(h("2025-10-13").receivedOn).toBeTruthy();
  });
  it("шапка списання: причина обов'язкова, коментар для «виправлення» й «іншого»", () => {
    expect(validateWriteOffHeader({ reason: "", note: "" }).reason).toBeTruthy();
    expect(validateWriteOffHeader({ reason: "damaged", note: "" })).toEqual({});
    expect(validateWriteOffHeader({ reason: "correction", note: " " }).note).toMatch(/коментар/);
    expect(validateWriteOffHeader({ reason: "other", note: "ok" })).toEqual({});
  });
});

describe("інвентаризація й ідемпотентність", () => {
  it("різниця «підраховано - очікувано» без похибки; null = не рахували", () => {
    expect(stocktakeDifference(2.1, 2.4)).toBe(0.3);
    expect(stocktakeDifference(5, 3)).toBe(-2);
    expect(stocktakeDifference(null, 3)).toBe(3);
    expect(stocktakeDifference(5, null)).toBeNull();
  });
  it("ключ ідемпотентності зберігається, поки тіло те саме, і змінюється з іншим тілом", () => {
    let n = 0;
    const make = () => `k${++n}`;
    const a = nextAttempt(null, { x: 1 }, make);
    expect(nextAttempt(a, { x: 1 }, make)).toBe(a);
    const b = nextAttempt(a, { x: 2 }, make);
    expect(b.key).not.toBe(a.key);
  });
});

describe("коди помилок §32.8", () => {
  const e = (status: number, code: string, body?: Record<string, unknown>) => new BeautyApiError(status, code, code, undefined, undefined, undefined, undefined, undefined, body);
  it("власні тексти складу замість однойменних кодів інших розділів", () => {
    expect(humanizeInventoryError(e(422, "invalid_note"))).toBe("Коментар: до 500 символів.");
    expect(humanizeInventoryError(e(422, "invalid_reason"))).toBe("Оберіть причину зі списку.");
    expect(humanizeInventoryError(e(422, "invalid_range"))).toMatch(/366/);
    expect(humanizeInventoryError(e(422, "insufficient_stock"))).toBe("Недостатньо товару на залишку.");
    expect(humanizeInventoryError(e(422, "batch_expired"))).toMatch(/прострочена/);
    expect(humanizeInventoryError(e(409, "barcode_taken"))).toBe("Товар з таким штрихкодом уже є.");
    expect(humanizeInventoryError(e(409, "stocktake_version_conflict"))).toMatch(/Оновіть/);
    expect(humanizeInventoryError(e(403, "module_disabled"))).toMatch(/«Склад»/);
  });
  it("індекс рядка з extras.line (0-based) -> «Рядок N»", () => {
    expect(humanizeInventoryError(e(422, "expiry_required", { line: 2 }))).toBe("Рядок 3: Вкажіть термін придатності.");
    expect(humanizeInventoryError(e(422, "invalid_expiry"))).toBe("Термін придатності вже минув.");
  });
  it("невідомий код -> загальний словник за статусом", () => {
    expect(humanizeInventoryError(e(429, "whatever"))).toMatch(/Зачекайте/);
  });
  it("shortageLines розбирає extras.lines і відкидає сміття", () => {
    const err = e(422, "insufficient_stock", { lines: [{ line: 0, productId: "p", requested: 5, available: 2 }, { nope: 1 }] });
    expect(shortageLines(err)).toEqual([{ line: 0, productId: "p", requested: 5, available: 2 }]);
    expect(shortageLines(e(422, "other"))).toEqual([]);
  });
});

describe("етап 2a: сальдо й нестача (§32.17)", () => {
  it("сальдо = залишок − нестача без похибки; може бути від'ємним", () => {
    expect(balanceOf(0, 0.3)).toBe(-0.3);
    expect(balanceOf(1000, 30)).toBe(970);
    expect(balanceOf(0.3, 0.1)).toBe(0.2);
  });
  it("isLow за сальдо, а не за залишком", () => {
    expect(isLowBalance(-0.3, 2.5)).toBe(true);
    expect(isLowBalance(5, 2.5)).toBe(false);
    expect(isLowBalance(-1, null)).toBe(false);
    expect(isLowBalance(-1, 0)).toBe(false);
  });
  it("мінус позначено текстом, не лише знаком", () => {
    expect(balanceLabel(-0.3, "l")).toBe("−0,3 л (у мінусі)");
    expect(balanceLabel(4, "l")).toBe("4 л");
  });
  it("ярлики рухів нестачі й погашення", () => {
    expect(movementLabel("service_consumption", true)).toMatch(/Нестача/);
    expect(movementLabel("service_consumption", false)).toBe("Витрата на послугу");
    for (const t of ["receipt", "transfer_in", "stocktake_adjust"] as const) expect(movementLabel(t, true)).toBe("Погашення нестачі");
    expect(movementLabel("receipt", false)).toBe("Надходження");
  });
  it("інвентаризація: різниця до сальдо (expected − expectedShortage)", () => {
    expect(stocktakeDifference(0, 5, 0.3)).toBe(5.3);
    expect(stocktakeDifference(10, 10, null)).toBe(0);
    expect(stocktakeDifference(10, null, 2)).toBeNull();
  });
});

describe("етап 2a: бейдж і сповіщення (§32.19)", () => {
  it("«99+» вище 99", () => {
    expect(alertBadgeText(5)).toBe("5");
    expect(alertBadgeText(99)).toBe("99");
    expect(alertBadgeText(100)).toBe("99+");
    expect(alertBadgeText(1000)).toBe("99+");
  });
  it("поріг терміну: 0 = прострочено, найменший поріг ≥ днів, інакше немає", () => {
    const t = [30, 14, 7];
    expect(daysBucketFor(-1, t)).toBe(0);
    expect(daysBucketFor(0, t)).toBe(7);
    expect(daysBucketFor(5, t)).toBe(7);
    expect(daysBucketFor(8, t)).toBe(14);
    expect(daysBucketFor(30, t)).toBe(30);
    expect(daysBucketFor(31, t)).toBeNull();
  });
  const base = {
    id: "a",
    status: "open" as const,
    location: { id: "c", name: "Центр" },
    product: { id: "p", name: "Гель", unit: "l" as const, purpose: "both" as const, category: null, barcode: null, isActive: true },
    batch: null,
    operationId: null,
    threshold: null,
    expiresOn: null,
    daysBucket: null,
    createdAt: "2026-10-14T09:00:00Z",
    readAt: null,
    readBy: null,
    resolvedAt: null,
  };
  it("опис без ПД: низький залишок у мінусі, термін, нестача", () => {
    expect(alertSummary({ ...base, kind: "low_stock", quantity: -0.3, threshold: 2.5 })).toContain("(у мінусі)");
    expect(alertSummary({ ...base, kind: "expiring", quantity: 6, daysBucket: 0, expiresOn: "2026-10-01", batch: { id: "b", expiresOn: "2026-10-01", lotNumber: "L1" } })).toMatch(/Термін минув.*партія L1/);
    expect(alertSummary({ ...base, kind: "shortage", quantity: 0.3 })).toContain("Не вистачило");
    expect(ALERT_KIND_LABEL.shortage).toBe("Нестача");
  });
});

describe("етап 2a: норми матеріалів (§32.16)", () => {
  const mk = (id: string, unit: "pcs" | "ml", purpose: "sale" | "salon_use" | "both", isActive = true) => [id, { id, name: id, unit, purpose, category: null, barcode: null, isActive }] as const;
  const products = new Map([mk("gel", "pcs", "both"), mk("dye", "ml", "salon_use"), mk("mask", "ml", "sale"), mk("old", "ml", "both", false)]);
  it("лише salon_use|both і активні", () => {
    expect(canBeNorm(products.get("gel")!)).toBe(true);
    expect(canBeNorm(products.get("mask")!)).toBe(false);
    expect(canBeNorm(products.get("old")!)).toBe(false);
  });
  it("порожній набір: дозволено лише для override закладу", () => {
    expect(validateMaterialDraft([], products, false).errors.form).toBeTruthy();
    expect(validateMaterialDraft([], products, true)).toMatchObject({ lines: [], errors: { lines: {} } });
  });
  it("валідний набір; цілі для pcs, дробові для ml", () => {
    expect(validateMaterialDraft([{ productId: "gel", quantity: "2" }, { productId: "dye", quantity: "12,5" }], products, false).lines).toEqual([
      { productId: "gel", quantity: 2 },
      { productId: "dye", quantity: 12.5 },
    ]);
    const bad = validateMaterialDraft([{ productId: "gel", quantity: "1,5" }], products, false);
    expect(bad.lines).toBeUndefined();
    expect(bad.errors.lines[0]).toMatch(/цілою/);
  });
  it("помилки за рядками: не обрано, дубль, для продажу, нуль, понад 100 000", () => {
    const r = validateMaterialDraft(
      [
        { productId: "", quantity: "1" },
        { productId: "dye", quantity: "1" },
        { productId: "dye", quantity: "1" },
        { productId: "mask", quantity: "1" },
        { productId: "gel", quantity: "0" },
      ],
      products,
      false,
    );
    expect(Object.keys(r.errors.lines).sort()).toEqual(["0", "2", "3", "4"]);
    expect(validateMaterialDraft([{ productId: "dye", quantity: "100001" }], products, false).errors.lines[0]).toBeTruthy();
  });
  it("не більше 50 рядків", () => {
    const many = Array.from({ length: 51 }, () => ({ productId: "dye", quantity: "1" }));
    expect(validateMaterialDraft(many, products, true).errors.form).toMatch(/50/);
  });
  it("коди 2a мапляться в тексти (purpose_locked, unit_locked, product_not_for_salon_use)", () => {
    expect(humanizeInventoryError(new BeautyApiError(422, "purpose_locked", "x"))).toMatch(/нормах матеріалів/);
    expect(humanizeInventoryError(new BeautyApiError(422, "unit_locked", "x"))).toMatch(/норми/);
    expect(humanizeInventoryError(new BeautyApiError(422, "product_not_for_salon_use", "x", undefined, undefined, undefined, undefined, undefined, { line: 1 }))).toMatch(/^Рядок 2:.*лише для продажу/);
    expect(humanizeInventoryError(new BeautyApiError(404, "alert_not_found", "x"))).toBe("Сповіщення не знайдено.");
    expect(humanizeInventoryError(new BeautyApiError(404, "materials_not_found", "x"))).toBe("Для цього закладу норм немає.");
  });
});
