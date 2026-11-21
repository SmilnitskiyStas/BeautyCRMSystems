import { productOrderSchema, type PublicProductOrder } from "@/api/productOrders";
import { order as raw } from "@/api/__tests__/orderFixture";
import { uk } from "@/i18n/uk";
import { canReorder, formatLeft, statusText } from "../status";

const o = (over: Record<string, unknown> = {}): PublicProductOrder => productOrderSchema.parse(raw(over));
// мінімальний t: підставляє {{k}} у uk-рядок за ключем "a.b.c"
const t = (key: string, opts: Record<string, unknown> = {}) => {
  const s = key.split(".").reduce<unknown>((a, k) => (a as Record<string, unknown>)[k], uk) as string;
  return s.replace(/\{\{(\w+)\}\}/g, (_, k) => String(opts[k] ?? ""));
};

describe("statusText (§33.9)", () => {
  it("pending_payment without payment in progress shows the reserve time", () => {
    expect(statusText(o(), t)).toMatch(/^Очікує оплати\. Товар зарезервовано до \d{2}:\d{2}$/);
  });
  it("pending_payment with paymentInProgress shows 'processing'", () => {
    expect(statusText(o({ paymentInProgress: true, canPay: false }), t)).toBe(uk.shop.status.processing);
  });
  it("paid shows place and order number", () => {
    const s = statusText(o({ status: "paid", location: { id: "l", name: "Salon", address: "Street 1" } }), t);
    expect(s).toContain("Salon, Street 1");
    expect(s).toContain("№1042");
  });
  it("cancelled client vs staff", () => {
    expect(statusText(o({ status: "cancelled", closeReason: "client" }), t)).toBe(uk.shop.status.cancelledClient);
    expect(statusText(o({ status: "cancelled", closeReason: "staff" }), t)).toBe(uk.shop.status.cancelledStaff);
  });
  it("expired", () => {
    expect(statusText(o({ status: "expired", closeReason: "expired" }), t)).toBe(uk.shop.status.expired);
  });
  it("refunded: staff vs stock_unavailable, amount from refundAmount", () => {
    expect(statusText(o({ status: "refunded", closeReason: "staff", refundAmount: 840 }), t)).toContain("840");
    const s = statusText(o({ status: "refunded", closeReason: "stock_unavailable", refundAmount: 840.5 }), t);
    expect(s).toContain("Товар закінчився");
    expect(s).toContain("840.50");
  });
  it("fulfilled without date does not throw", () => {
    expect(() => statusText(o({ status: "fulfilled" }), t)).not.toThrow();
  });
});

describe("canReorder", () => {
  it.each([
    ["expired", "expired", true],
    ["cancelled", "client", true],
    ["cancelled", "staff", true],
    ["refunded", "stock_unavailable", true],
    ["refunded", "staff", false],
    ["paid", null, false],
    ["fulfilled", null, false],
    ["pending_payment", null, false],
  ])("%s/%s -> %s", (status, closeReason, expected) => {
    expect(canReorder(o({ status, closeReason }))).toBe(expected);
  });
});

describe("formatLeft", () => {
  const at = "2026-11-20T10:15:00Z";
  const base = Date.parse(at);
  it("m:ss countdown", () => {
    expect(formatLeft(at, base - 65_000)).toBe("1:05");
    expect(formatLeft(at, base - 15 * 60_000)).toBe("15:00");
  });
  it("0:00 after expiry", () => {
    expect(formatLeft(at, base + 1000)).toBe("0:00");
  });
});
