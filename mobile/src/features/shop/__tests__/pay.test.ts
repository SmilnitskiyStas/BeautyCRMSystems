import { BookingApiError } from "@beauty/booking-core";
import { productOrderSchema, type PublicProductOrder } from "@/api/productOrders";
import { order as rawOrder } from "@/api/__tests__/orderFixture";
import { payWithReconcile } from "../pay";

const o = (over: Record<string, unknown> = {}): PublicProductOrder => productOrderSchema.parse(rawOrder(over));
const inProgress = o({ paymentInProgress: true, canPay: false });
const net = () => new BookingApiError(0, "api_unreachable", "network");

function deps(pay: jest.Mock, getOrder: jest.Mock) {
  const sleep = jest.fn().mockResolvedValue(undefined);
  return { api: { pay, getOrder }, sleep, delays: [5, 10, 20, 40, 45] };
}

describe("payWithReconcile", () => {
  it("success: one P4, no P3", async () => {
    const pay = jest.fn().mockResolvedValue(o({ status: "paid", canPay: false }));
    const getOrder = jest.fn();
    const r = await payWithReconcile(deps(pay, getOrder), "s", "T");
    expect(r.order?.status).toBe("paid");
    expect(r.errorCode).toBeNull();
    expect(getOrder).not.toHaveBeenCalled();
  });

  it("402 payment_failed returns the order from the error body", async () => {
    const pay = jest.fn().mockRejectedValue(new BookingApiError(402, "payment_failed", "x", undefined, { code: "payment_failed", order: rawOrder({ paymentAttemptsLeft: 4 }) }));
    const r = await payWithReconcile(deps(pay, jest.fn()), "s", "T");
    expect(r.errorCode).toBe("payment_failed");
    expect(r.order?.paymentAttemptsLeft).toBe(4);
  });

  it("network error: P3 BEFORE any new P4; paid -> done without retrying P4", async () => {
    const pay = jest.fn().mockRejectedValue(net());
    const getOrder = jest.fn().mockResolvedValue(o({ status: "paid", canPay: false }));
    const r = await payWithReconcile(deps(pay, getOrder), "s", "T");
    expect(getOrder).toHaveBeenCalledTimes(1);
    expect(pay).toHaveBeenCalledTimes(1);
    expect(r.order?.status).toBe("paid");
  });

  it("network error and order still payable -> no blind retry, UI offers Pay again (canPay)", async () => {
    const pay = jest.fn().mockRejectedValue(net());
    const getOrder = jest.fn().mockResolvedValue(o());
    const r = await payWithReconcile(deps(pay, getOrder), "s", "T");
    expect(pay).toHaveBeenCalledTimes(1);
    expect(r.order?.canPay).toBe(true);
  });

  it("in progress: polls with 5/10/20/40/45 s delays, then exactly one more P4", async () => {
    const pay = jest.fn().mockRejectedValueOnce(net()).mockResolvedValueOnce(o({ status: "paid", canPay: false }));
    const getOrder = jest.fn().mockResolvedValue(inProgress);
    const d = deps(pay, getOrder);
    const r = await payWithReconcile(d, "s", "T");
    expect(d.sleep.mock.calls.map((c) => c[0])).toEqual([5, 10, 20, 40, 45]);
    expect(pay).toHaveBeenCalledTimes(2);
    expect(r.order?.status).toBe("paid");
  });

  it("409 payment_in_progress -> polls until paid", async () => {
    const pay = jest.fn().mockRejectedValue(new BookingApiError(409, "payment_in_progress", "x", undefined, {}));
    const getOrder = jest.fn().mockResolvedValueOnce(inProgress).mockResolvedValueOnce(o({ status: "paid", canPay: false }));
    const d = deps(pay, getOrder);
    const r = await payWithReconcile(d, "s", "T");
    expect(d.sleep).toHaveBeenCalledTimes(1);
    expect(pay).toHaveBeenCalledTimes(1);
    expect(r.order?.status).toBe("paid");
  });

  it("P3 failing too -> error code, no order", async () => {
    const r = await payWithReconcile(deps(jest.fn().mockRejectedValue(net()), jest.fn().mockRejectedValue(net())), "s", "T");
    expect(r).toEqual({ order: null, errorCode: "api_unreachable" });
  });
});
