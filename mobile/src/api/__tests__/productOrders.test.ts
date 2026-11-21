import { BookingApiError } from "@beauty/booking-core";
import type { CatalogFetch } from "../catalog";
import { createProductOrdersApi, errorExtras } from "../productOrders";
import { order } from "./orderFixture";

const res = (status: number, body: unknown, headers: Record<string, string> = {}) => ({
  ok: status < 400,
  status,
  statusText: String(status),
  headers: { get: (n: string) => headers[n] ?? null },
  json: async () => body,
});

const setup = (r: ReturnType<typeof res>) => {
  const fetch = jest.fn<ReturnType<CatalogFetch>, Parameters<CatalogFetch>>().mockResolvedValue(r);
  return { fetch, api: createProductOrdersApi({ baseUrl: "https://api.x/", fetch }) };
};
const req = { locationId: "loc1", customer: { name: "Olena", phone: "0671234567" }, lines: [{ productId: "p1", quantity: 2 }], expectedTotal: 840 };

describe("productOrders api", () => {
  it("createOrder sends Idempotency-Key and returns token + order; unknown fields are ignored", async () => {
    const { api, fetch } = setup(res(201, { publicToken: "T".repeat(43), order: order() }));
    const r = await api.createOrder("salon-x", req, "6f1c2f7e-8b4a-4e3d-9c21-5a7b8c9d0e1f");
    const [url, init] = fetch.mock.calls[0];
    expect(url).toBe("https://api.x/api/public/salon-x/product-orders");
    expect(init.method).toBe("POST");
    expect(init.headers["Idempotency-Key"]).toBe("6f1c2f7e-8b4a-4e3d-9c21-5a7b8c9d0e1f");
    expect(JSON.parse(init.body!)).toEqual(req);
    expect(r.publicToken).toHaveLength(43);
    expect(r.replayed).toBe(false);
    expect(r.order).not.toHaveProperty("futureField");
  });

  it("idempotent replay (200 + Idempotent-Replayed) is flagged", async () => {
    const { api } = setup(res(200, { publicToken: "T".repeat(43), order: order() }, { "Idempotent-Replayed": "true" }));
    expect((await api.createOrder("s", req, "k".repeat(20))).replayed).toBe(true);
  });

  it("402 payment_failed keeps the order in error details", async () => {
    const { api } = setup(res(402, { code: "payment_failed", message: "x", order: order({ paymentAttemptsLeft: 4 }) }));
    const e = await api.pay("s", "TOKEN").catch((x) => x);
    expect(e).toBeInstanceOf(BookingApiError);
    expect(e.status).toBe(402);
    expect(e.code).toBe("payment_failed");
    expect(errorExtras(e).order?.paymentAttemptsLeft).toBe(4);
  });

  it("409 price_changed exposes total; 422 insufficient_stock exposes productIds", async () => {
    const a = await setup(res(409, { code: "price_changed", total: 900.5 })).api.createOrder("s", req, "k".repeat(20)).catch((x) => x);
    expect(errorExtras(a).total).toBe(900.5);
    const b = await setup(res(422, { code: "insufficient_stock", productIds: ["p1"] })).api.createOrder("s", req, "k".repeat(20)).catch((x) => x);
    expect(errorExtras(b).productIds).toEqual(["p1"]);
  });

  it("429 carries Retry-After; 426 is a plain error (upgrade guard sits in the fetch wrapper)", async () => {
    const a = await setup(res(429, { code: "rate_limited" }, { "Retry-After": "12" })).api.getOrder("s", "T").catch((x) => x);
    expect(a.retryAfterSeconds).toBe(12);
    const b = await setup(res(426, { code: "upgrade_required" })).api.getCatalog("s", "loc1").catch((x) => x);
    expect(b.status).toBe(426);
  });

  it("network failure -> status 0 api_unreachable; invalid body -> 502", async () => {
    const fetch = jest.fn().mockRejectedValue(new TypeError("net"));
    const e = await createProductOrdersApi({ baseUrl: "https://api.x", fetch }).pay("s", "T").catch((x) => x);
    expect(e.status).toBe(0);
    const bad = await setup(res(200, { nope: 1 })).api.getOrder("s", "T").catch((x) => x);
    expect(bad.status).toBe(502);
  });

  it("token only in the path (encoded), never in the body", async () => {
    const { api, fetch } = setup(res(200, { order: order({ status: "paid" }) }));
    await api.pay("s", "ab-_c");
    const [url, init] = fetch.mock.calls[0];
    expect(url).toBe("https://api.x/api/public/s/product-orders/ab-_c/pay");
    expect(init.body).toBe("{}");
  });
});
