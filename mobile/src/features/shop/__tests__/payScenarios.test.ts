import { BookingApiError } from "@beauty/booking-core";
import { productOrderSchema, type PublicProductOrder } from "@/api/productOrders";
import { order as rawOrder } from "@/api/__tests__/orderFixture";
import { POLL_DELAYS_MS, payWithReconcile } from "../pay";

const o = (over: Record<string, unknown> = {}): PublicProductOrder => productOrderSchema.parse(rawOrder(over));
const inProgress = o({ paymentInProgress: true, canPay: false });
const net = () => new BookingApiError(0, "api_unreachable", "network");
const err = (status: number, code: string, order?: unknown) => new BookingApiError(status, code, "x", undefined, { code, ...(order ? { order } : {}) });
const mk = (pay: jest.Mock, getOrder: jest.Mock, delays?: number[]) => ({ api: { pay, getOrder }, sleep: jest.fn().mockResolvedValue(undefined), delays });

describe("pay: §33.7 / §33.10 D scenarios", () => {
  it("poll schedule is 5/10/20/40 s, then 45 s tail (~2 min total) before the single retry", () => {
    expect(POLL_DELAYS_MS.slice(0, 4)).toEqual([5000, 10000, 20000, 40000]);
    expect(POLL_DELAYS_MS.reduce((a, b) => a + b, 0)).toBe(120_000);
  });

  it("default delays are used when none injected", async () => {
    const pay = jest.fn().mockRejectedValueOnce(net()).mockResolvedValueOnce(o({ status: "paid", canPay: false }));
    const d = mk(pay, jest.fn().mockResolvedValue(inProgress));
    await payWithReconcile(d, "s", "T");
    expect(d.sleep.mock.calls.map((c) => c[0])).toEqual(POLL_DELAYS_MS);
  });

  it.each([
    [409, "order_expired", "expired"],
    [409, "stock_unavailable", "refunded"],
    [409, "order_closed", "cancelled"],
    [422, "payment_attempts_exceeded", "pending_payment"],
  ])("%i %s is final: order from body, no P3, no P4 retry, no polling", async (status, code, st) => {
    const pay = jest.fn().mockRejectedValue(err(status, code, rawOrder({ status: st, canPay: false })));
    const getOrder = jest.fn();
    const d = mk(pay, getOrder);
    const r = await payWithReconcile(d, "s", "T");
    expect(r.errorCode).toBe(code);
    expect(r.order?.status).toBe(st);
    expect(pay).toHaveBeenCalledTimes(1);
    expect(getOrder).not.toHaveBeenCalled();
    expect(d.sleep).not.toHaveBeenCalled();
  });

  it("402 payment_failed: no retry, UI can offer pay again via order.canPay", async () => {
    const pay = jest.fn().mockRejectedValue(err(402, "payment_failed", rawOrder({ paymentAttemptsLeft: 3 })));
    const r = await payWithReconcile(mk(pay, jest.fn()), "s", "T");
    expect(pay).toHaveBeenCalledTimes(1);
    expect(r.order?.canPay).toBe(true);
  });

  it("final error without order body gives order=null and code", async () => {
    const pay = jest.fn().mockRejectedValue(new BookingApiError(404, "not_found", "x"));
    expect(await payWithReconcile(mk(pay, jest.fn()), "s", "T")).toEqual({ order: null, errorCode: "not_found" });
  });

  it("error with empty code falls back to http_<status>", async () => {
    const pay = jest.fn().mockRejectedValue(new BookingApiError(400, ""));
    expect((await payWithReconcile(mk(pay, jest.fn()), "s", "T")).errorCode).toBe("http_400");
  });

  it("5xx on P4 is treated like a network error (P3 first)", async () => {
    const pay = jest.fn().mockRejectedValue(new BookingApiError(503, ""));
    const getOrder = jest.fn().mockResolvedValue(o({ status: "paid", canPay: false }));
    const r = await payWithReconcile(mk(pay, getOrder), "s", "T");
    expect(getOrder).toHaveBeenCalledTimes(1);
    expect(pay).toHaveBeenCalledTimes(1);
    expect(r.order?.status).toBe("paid");
  });

  it("P3 not found while checking -> errorCode not_found (caller drops local order)", async () => {
    const r = await payWithReconcile(mk(jest.fn().mockRejectedValue(net()), jest.fn().mockRejectedValue(new BookingApiError(404, "not_found"))), "s", "T");
    expect(r).toEqual({ order: null, errorCode: "not_found" });
  });

  it("still in progress after the whole schedule: EXACTLY one extra P4 (never a loop)", async () => {
    const pay = jest.fn().mockRejectedValue(net());
    const getOrder = jest.fn().mockResolvedValue(inProgress);
    const d = mk(pay, getOrder, [5, 10, 20, 40, 45]);
    const r = await payWithReconcile(d, "s", "T");
    expect(pay).toHaveBeenCalledTimes(2);
    expect(d.sleep).toHaveBeenCalledTimes(5);
    expect(r.order).not.toBeNull();
  });

  it("retry P4 answers payment_in_progress again -> final P3 check, no second retry", async () => {
    const pay = jest.fn().mockRejectedValueOnce(net()).mockRejectedValue(err(409, "payment_in_progress"));
    const getOrder = jest.fn().mockResolvedValue(inProgress);
    const r = await payWithReconcile(mk(pay, getOrder, [1]), "s", "T");
    expect(pay).toHaveBeenCalledTimes(2);
    expect(r.order?.paymentInProgress).toBe(true);
  });

  it("retry P4 fails with stock_unavailable -> reported as final", async () => {
    const body = rawOrder({ status: "refunded", closeReason: "stock_unavailable", refundAmount: 840, canPay: false });
    const pay = jest.fn().mockRejectedValueOnce(net()).mockRejectedValue(err(409, "stock_unavailable", body));
    const r = await payWithReconcile(mk(pay, jest.fn().mockResolvedValue(inProgress), [1]), "s", "T");
    expect(r.errorCode).toBe("stock_unavailable");
    expect(r.order?.status).toBe("refunded");
  });

  it("P3 flips to a final status mid-poll (e.g. expired) -> stops polling, no P4 retry", async () => {
    const pay = jest.fn().mockRejectedValue(net());
    const getOrder = jest.fn().mockResolvedValueOnce(inProgress).mockResolvedValueOnce(inProgress).mockResolvedValue(o({ status: "expired", canPay: false }));
    const d = mk(pay, getOrder, [1, 2, 3]);
    const r = await payWithReconcile(d, "s", "T");
    expect(r.order?.status).toBe("expired");
    expect(pay).toHaveBeenCalledTimes(1);
    expect(d.sleep).toHaveBeenCalledTimes(2);
  });

  it("non-BookingApiError thrown by P4 is treated as unknown outcome (P3 first)", async () => {
    const pay = jest.fn().mockRejectedValue(new TypeError("Network request failed"));
    const getOrder = jest.fn().mockResolvedValue(o({ status: "paid", canPay: false }));
    const r = await payWithReconcile(mk(pay, getOrder), "s", "T");
    expect(getOrder).toHaveBeenCalledTimes(1);
    expect(r.order?.status).toBe("paid");
  });
});
