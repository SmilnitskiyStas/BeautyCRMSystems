import { describe, expect, it } from "vitest";
import { BeautyApiError } from "@/features/beauty-auth/errors";
import {
  ORDER_STATUS_FILTERS,
  ORDER_STATUS_VIEW,
  cancelButtonLabel,
  cancelExplanation,
  closeReasonLabel,
  customerPhone,
  humanizeOrderError,
  orderActions,
  orderIndicators,
  orderSizeLabel,
  orderStatusLabel,
  parseOrderNumber,
  phoneAriaLabel,
  saleMovementLabel,
  validateCancelReason,
} from "./orders";

const base = { status: "paid" as const, paymentInProgress: false, refundInProgress: false };

describe("замовлення: підписи", () => {
  it("кожен статус має підпис словами", () => {
    for (const s of ["pending_payment", "paid", "fulfilled", "cancelled", "expired", "refunded"] as const) expect(orderStatusLabel(s)).toBe(ORDER_STATUS_VIEW[s].label);
    expect(orderStatusLabel("paid")).toBe("Оплачено, до видачі");
  });
  it("чіпи: за замовчуванням open, є «Усі» і всі закриті статуси", () => {
    expect(ORDER_STATUS_FILTERS[0]).toEqual({ id: "open", label: "Оплачені до видачі" });
    expect(ORDER_STATUS_FILTERS.map((f) => f.id)).toEqual(["open", "all", "fulfilled", "cancelled", "refunded", "expired"]);
  });
  it("причина закриття: відомі - словами, невідомі - null", () => {
    expect(closeReasonLabel("staff")).toBe("Скасував заклад");
    expect(closeReasonLabel("whatever")).toBeNull();
    expect(closeReasonLabel(null)).toBeNull();
  });
  it("кількість позицій і штук", () => {
    expect(orderSizeLabel({ lineCount: 1, itemCount: 3 })).toBe("1 позиція, 3 шт");
    expect(orderSizeLabel({ lineCount: 2, itemCount: 5 })).toBe("2 позиції, 5 шт");
    expect(orderSizeLabel({ lineCount: 11, itemCount: 11 })).toBe("11 позицій, 11 шт");
  });
  it("індикатори в процесі", () => {
    expect(orderIndicators(base)).toEqual([]);
    expect(orderIndicators({ paymentInProgress: true, refundInProgress: true })).toEqual(["Оплата в процесі", "Повернення в процесі"]);
  });
  it("журнал руху: підписи sale / sale_return", () => {
    expect(saleMovementLabel("sale", null)).toBe("Продаж");
    expect(saleMovementLabel("sale_return", "po1")).toBe("Повернення продажу (за замовленням клієнта)");
  });
});

describe("замовлення: телефон", () => {
  it("повний, якщо сервер його віддав", () => {
    expect(customerPhone({ phone: "+380501234567", phoneMasked: null })).toEqual({ text: "+380501234567", masked: false });
    expect(phoneAriaLabel({ phone: "+380501234567", phoneMasked: null })).toBeUndefined();
  });
  it("маска, коли повного немає; для скрінрідера - слова", () => {
    expect(customerPhone({ phone: null, phoneMasked: "+••••••••••67" })).toEqual({ text: "+••••••••••67", masked: true });
    expect(phoneAriaLabel({ phone: null, phoneMasked: "+••••••••••67" })).toBe("Телефон приховано, закінчується на 67");
  });
  it("нічого немає - тире", () => {
    expect(customerPhone({ phone: null, phoneMasked: null }).text).toBe("—");
  });
});

describe("замовлення: пошук за номером", () => {
  it.each([
    ["1042", 1042],
    ["№1042", 1042],
    ["# 1042", 1042],
    ["  7 ", 7],
    ["", null],
    ["   ", null],
  ])("%j -> %j", (input, out) => expect(parseOrderNumber(input)).toBe(out));
  it.each(["abc", "10.5", "-3", "0", "1234567890", "12 34"])("%j некоректний", (input) => expect(parseOrderNumber(input)).toBe("invalid"));
});

describe("замовлення: доступні дії", () => {
  it("без права manage дій немає", () => {
    expect(orderActions(base, false)).toEqual({ fulfill: false, cancel: false, reconcile: false });
  });
  it("paid: видати й скасувати", () => {
    expect(orderActions(base, true)).toEqual({ fulfill: true, cancel: true, reconcile: false });
  });
  it("pending_payment: лише скасувати; видачі немає", () => {
    expect(orderActions({ ...base, status: "pending_payment" }, true)).toEqual({ fulfill: false, cancel: true, reconcile: false });
  });
  it("оплата в процесі: перевірити оплату, без скасування", () => {
    expect(orderActions({ ...base, status: "pending_payment", paymentInProgress: true }, true)).toEqual({ fulfill: false, cancel: false, reconcile: true });
  });
  it("повернення в процесі: нічого", () => {
    expect(orderActions({ ...base, refundInProgress: true }, true)).toEqual({ fulfill: false, cancel: false, reconcile: false });
  });
  it.each(["fulfilled", "cancelled", "expired", "refunded"] as const)("%s: дій немає", (status) => {
    expect(orderActions({ ...base, status }, true)).toEqual({ fulfill: false, cancel: false, reconcile: false });
  });
});

describe("замовлення: скасування", () => {
  it("пояснення для оплаченого й неоплаченого", () => {
    expect(cancelExplanation({ status: "paid", total: 1250.5, currency: "UAH" })).toMatch(/повернено клієнту повністю, товар повернеться на склад/);
    expect(cancelExplanation({ status: "pending_payment", total: 10, currency: "UAH" })).toMatch(/резерв товару буде знято/);
    expect(cancelButtonLabel({ status: "paid", total: 760, currency: "UAH" })).toBe("Скасувати й повернути 760 ₴");
    expect(cancelButtonLabel({ status: "pending_payment", total: 760, currency: "UAH" })).toBe("Скасувати замовлення");
  });
  it("причина ≤ 300 символів", () => {
    expect(validateCancelReason("a".repeat(300))).toBeNull();
    expect(validateCancelReason("a".repeat(301))).toMatch(/до 300/);
    expect(validateCancelReason("  ")).toBeNull();
  });
});

describe("замовлення: помилки", () => {
  const e = (status: number, code: string) => new BeautyApiError(status, code, code);
  it.each([
    ["product_order_not_found", /не знайдено/],
    ["order_not_paid", /не оплачене/],
    ["order_fulfilled", /вже видано/],
    ["order_closed", /вже закрите/],
    ["payment_in_progress", /Оплата ще обробляється/],
    ["refund_in_progress", /Повернення коштів/],
    ["nothing_to_reconcile", /нічого/],
    ["refund_failed", /повернути кошти/],
    ["invalid_reason", /до 300/],
  ])("%s", (code, re) => expect(humanizeOrderError(e(409, code))).toMatch(re));
  it("rate_limited і невідоме", () => {
    expect(humanizeOrderError(e(429, "rate_limited"))).toMatch(/Забагато/);
    expect(humanizeOrderError(e(400, "zzz"), "Фолбек")).toBe("Фолбек");
  });
});
