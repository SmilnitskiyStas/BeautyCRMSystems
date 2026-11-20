import { beforeEach, describe, expect, it, vi } from "vitest";

type Init = { method?: string; body?: string; cache?: string; headers?: Record<string, string> };
const apiJson = vi.fn<(path: string, init?: Init) => Promise<unknown>>();
vi.mock("@/features/beauty-auth/session", () => ({
  apiJson: (path: string, init?: Init) => apiJson(path, init),
  getUser: () => ({ id: "u", role: "owner", specialistId: null }),
}));

import { BeautyApiError } from "@/features/beauty-auth/errors";
import { httpBeautyApi } from "./http-client";

beforeEach(() => {
  apiJson.mockReset();
  apiJson.mockResolvedValue(undefined);
});
const last = () => apiJson.mock.calls[apiJson.mock.calls.length - 1]!;
const B = "/api/beauty/inventory/product-orders";

const summary = {
  id: "o1",
  number: 1042,
  status: "paid",
  location: { id: "c", name: "Центр", extra: "x" },
  customer: { name: "Олена", phone: "+380501234567", secret: 1 },
  lineCount: 2,
  itemCount: 3,
  total: 1250.5,
  currency: "UAH",
  createdAt: "2026-10-14T08:00:00Z",
  reservationExpiresAt: null,
  paidAt: "2026-10-14T08:01:00Z",
  fulfilledAt: null,
  closedAt: null,
  closeReason: null,
  paymentInProgress: false,
  refundInProgress: true,
};
const full = {
  ...summary,
  lines: [{ product: { id: "p1", name: "Шампунь", unit: "pcs", purpose: "sale", category: null, barcode: null, isActive: true }, name: "Шампунь (знімок)", unit: "pcs", quantity: 2, unitPrice: 450, lineTotal: 900, purchasePrice: 5 }],
  refundAmount: null,
  cancelNote: null,
  fulfilledBy: null,
  cancelledBy: { id: "u1", name: "Світлана" },
  clientId: "cl1",
  payment: { status: "paid", attempts: 2 },
  saleOperationId: null,
  returnOperationId: "op9",
  source: "android",
};

describe("http §33.13: список і лічильник", () => {
  it("список: параметри, take ≤ 200, порожні відкидаються, no-store", async () => {
    apiJson.mockResolvedValueOnce({ items: [summary], hasMore: true, nextCursor: "abc" });
    const r = await httpBeautyApi.getProductOrders({ status: "open", locationId: "c", from: "2026-10-01T00:00:00Z", to: "", number: 1042, cursor: "x", take: 999 });
    expect(last()[0]).toBe(`${B}?status=open&locationId=c&from=2026-10-01T00%3A00%3A00Z&number=1042&cursor=x&take=200`);
    expect(last()[1]).toMatchObject({ cache: "no-store" });
    expect(last()[1]?.method).toBeUndefined();
    expect(r.hasMore).toBe(true);
    expect(r.nextCursor).toBe("abc");
  });

  it("без параметрів - чистий шлях; мапінг summary без зайвих полів", async () => {
    apiJson.mockResolvedValueOnce({ items: [summary], hasMore: false });
    const r = await httpBeautyApi.getProductOrders();
    expect(last()[0]).toBe(B);
    expect(r).toEqual({
      items: [
        {
          id: "o1",
          number: 1042,
          status: "paid",
          location: { id: "c", name: "Центр" },
          customer: { name: "Олена", phone: "+380501234567", phoneMasked: null },
          lineCount: 2,
          itemCount: 3,
          total: 1250.5,
          currency: "UAH",
          createdAt: "2026-10-14T08:00:00Z",
          reservationExpiresAt: null,
          paidAt: "2026-10-14T08:01:00Z",
          fulfilledAt: null,
          closedAt: null,
          closeReason: null,
          paymentInProgress: false,
          refundInProgress: true,
        },
      ],
      hasMore: false,
    });
  });

  it("маска замість телефону; невідомий статус -> pending_payment (fail-safe, без видачі)", async () => {
    apiJson.mockResolvedValueOnce({ items: [{ ...summary, status: "zzz", customer: { name: "А", phoneMasked: "+••••••••••67" } }], hasMore: false });
    const [o] = (await httpBeautyApi.getProductOrders()).items;
    expect(o!.customer).toEqual({ name: "А", phone: null, phoneMasked: "+••••••••••67" });
    expect(o!.status).toBe("pending_payment");
  });

  it("counts", async () => {
    apiJson.mockResolvedValueOnce({ paid: 7 });
    expect(await httpBeautyApi.getProductOrderCounts()).toEqual({ paid: 7 });
    expect(last()[0]).toBe(`${B}/counts`);
    apiJson.mockResolvedValueOnce({});
    expect(await httpBeautyApi.getProductOrderCounts()).toEqual({ paid: 0 });
  });
});

describe("http §33.13: деталі й дії", () => {
  it("деталь: мапінг рядків, оплати, clientId; закупівельних цін немає", async () => {
    apiJson.mockResolvedValueOnce(full);
    const o = await httpBeautyApi.getProductOrder("o 1/2");
    expect(last()[0]).toBe(`${B}/o%201%2F2`);
    expect(o.lines).toEqual([
      { product: { id: "p1", name: "Шампунь", unit: "pcs", purpose: "sale", category: null, barcode: null, isActive: true }, name: "Шампунь (знімок)", unit: "pcs", quantity: 2, unitPrice: 450, lineTotal: 900 },
    ]);
    expect(JSON.stringify(o)).not.toContain("purchasePrice");
    expect(o.payment).toEqual({ status: "paid", attempts: 2 });
    expect(o.clientId).toBe("cl1");
    expect(o.cancelledBy).toEqual({ id: "u1", name: "Світлана" });
    expect(o.returnOperationId).toBe("op9");
    expect(o.source).toBe("android");
  });

  it("деталь без payment/clientId -> null", async () => {
    apiJson.mockResolvedValueOnce({ ...summary, lines: [] });
    const o = await httpBeautyApi.getProductOrder("o1");
    expect(o.payment).toBeNull();
    expect(o.clientId).toBeNull();
    expect(o.source).toBe("web");
  });

  it("fulfill: POST з порожнім тілом", async () => {
    apiJson.mockResolvedValueOnce({ ...full, status: "fulfilled" });
    const o = await httpBeautyApi.fulfillProductOrder("o1");
    expect(last()[0]).toBe(`${B}/o1/fulfill`);
    expect(last()[1]).toMatchObject({ method: "POST", body: "{}", cache: "no-store" });
    expect(o.status).toBe("fulfilled");
  });

  it("cancel: причина обрізається; без причини - {}; відповідь {order, refundAmount}", async () => {
    apiJson.mockResolvedValueOnce({ order: { ...full, status: "refunded", refundAmount: 900 }, refundAmount: 900 });
    const r = await httpBeautyApi.cancelProductOrder("o1", "  Клієнт передумав ");
    expect(last()[0]).toBe(`${B}/o1/cancel`);
    expect(last()[1]).toMatchObject({ method: "POST", body: JSON.stringify({ reason: "Клієнт передумав" }) });
    expect(r.refundAmount).toBe(900);
    expect(r.order.status).toBe("refunded");
    apiJson.mockResolvedValueOnce({ order: full, refundAmount: 0 });
    await httpBeautyApi.cancelProductOrder("o1", "   ");
    expect(last()[1]?.body).toBe("{}");
    apiJson.mockResolvedValueOnce({ order: full, refundAmount: 0 });
    await httpBeautyApi.cancelProductOrder("o1");
    expect(last()[1]?.body).toBe("{}");
  });

  it("reconcile-payment: POST", async () => {
    apiJson.mockResolvedValueOnce(full);
    await httpBeautyApi.reconcileProductOrderPayment("o1");
    expect(last()[0]).toBe(`${B}/o1/reconcile-payment`);
    expect(last()[1]).toMatchObject({ method: "POST", body: "{}" });
  });

  it("помилки сервера прокидаються як є (402 refund_failed, 409 order_closed)", async () => {
    apiJson.mockRejectedValueOnce(new BeautyApiError(402, "refund_failed", "x"));
    await expect(httpBeautyApi.cancelProductOrder("o1")).rejects.toMatchObject({ status: 402, code: "refund_failed" });
    apiJson.mockRejectedValueOnce(new BeautyApiError(409, "order_closed", "x"));
    await expect(httpBeautyApi.fulfillProductOrder("o1")).rejects.toMatchObject({ status: 409, code: "order_closed" });
  });
});

describe("http §33.14: журнал руху", () => {
  it("sale_return і productOrderId мапляться", async () => {
    apiJson.mockResolvedValueOnce({ items: [{ id: "m1", type: "sale_return", operationKind: "sale_return", productOrderId: "po1", product: {}, quantity: 1 }], hasMore: false });
    const [m] = (await httpBeautyApi.getMovements({ type: "sale_return" })).items;
    expect(last()[0]).toBe("/api/beauty/inventory/movements?type=sale_return");
    expect(m!.type).toBe("sale_return");
    expect(m!.operationKind).toBe("sale_return");
    expect(m!.productOrderId).toBe("po1");
  });
  it("операції: productOrderId", async () => {
    apiJson.mockResolvedValueOnce({ items: [{ id: "op1", kind: "sale", productOrderId: "po1" }], hasMore: false });
    const [o] = (await httpBeautyApi.getOperations({ kind: "sale_return" })).items;
    expect(last()[0]).toBe("/api/beauty/inventory/operations?kind=sale_return");
    expect(o!.productOrderId).toBe("po1");
  });
});
