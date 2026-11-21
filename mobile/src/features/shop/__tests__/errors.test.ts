import { BookingApiError } from "@beauty/booking-core";
import { uk } from "@/i18n/uk";
import { isShopDisabled, shopCodeText, shopErrorText } from "../errors";

const E = uk.shop.errors;
// §33.11: усі публічні коди мають власний текст
const CODES = [
  [402, "payment_failed"], [403, "tenant_suspended"], [404, "not_found"], [404, "product_orders_disabled"], [404, "location_not_found"],
  [409, "price_changed"], [409, "stock_unavailable"], [409, "payment_in_progress"], [409, "order_expired"], [409, "order_closed"], [409, "cannot_cancel"],
  [422, "insufficient_stock"], [422, "product_unavailable"], [422, "order_limit_reached"], [422, "order_total_too_large"], [422, "payment_attempts_exceeded"],
  [422, "captcha_failed"], [422, "integrity_failed"], [422, "idempotency_key_required"], [422, "invalid_idempotency_key"],
] as const;

describe("shop error texts (§33.11)", () => {
  it.each(CODES)("%i %s has a dedicated text", (status, code) => {
    expect(shopErrorText(new BookingApiError(status, code, "english log"))).toBe(E[code]);
    expect(shopCodeText(code)).toBe(E[code]);
    expect(E[code]).not.toBe(E.generic);
  });

  it("never shows the server's english message", () => {
    expect(shopErrorText(new BookingApiError(402, "payment_failed", "Card payment failed."))).not.toContain("Card");
  });

  it("unknown code falls back to a generic text, not undefined/english", () => {
    const t = shopErrorText(new BookingApiError(409, "brand_new_code", "english"));
    expect(typeof t).toBe("string");
    expect(t).not.toBe("english");
    expect(shopCodeText("brand_new_code")).toBe(E.generic);
  });

  it("429 goes through core text (carries Retry-After seconds)", () => {
    expect(shopErrorText(new BookingApiError(429, "rate_limited", "x", 12))).toContain("12");
  });

  it("non-api error does not throw", () => {
    expect(typeof shopErrorText(new Error("boom"))).toBe("string");
    expect(typeof shopErrorText(undefined)).toBe("string");
  });

  it("isShopDisabled only for 404 product_orders_disabled", () => {
    expect(isShopDisabled(new BookingApiError(404, "product_orders_disabled"))).toBe(true);
    expect(isShopDisabled(new BookingApiError(404, "not_found"))).toBe(false);
    expect(isShopDisabled(new BookingApiError(409, "product_orders_disabled"))).toBe(false);
    expect(isShopDisabled(new Error("x"))).toBe(false);
  });
});
