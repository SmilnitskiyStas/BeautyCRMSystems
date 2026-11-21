import { BookingApiError } from "@beauty/booking-core";
import type { CatalogFetch } from "../catalog";
import { createProductOrdersApi, errorExtras } from "../productOrders";
import { order } from "./orderFixture";

const res = (status: number, body: unknown, headers: Record<string, string> = {}) => ({
  ok: status < 400,
  status,
  statusText: String(status),
  headers: { get: (n: string) => headers[n] ?? null },
  json: async () => {
    if (body === "NOT_JSON") throw new SyntaxError("bad json");
    return body;
  },
});
type Api = ReturnType<typeof createProductOrdersApi>;
const setup = (r: ReturnType<typeof res>) => {
  const fetch = jest.fn<ReturnType<CatalogFetch>, Parameters<CatalogFetch>>().mockResolvedValue(r);
  return { fetch, api: createProductOrdersApi({ baseUrl: "https://api.x", fetch }) };
};
// eslint-disable-next-line @typescript-eslint/no-explicit-any
const fail = (p: Promise<unknown>): Promise<any> => p.catch((x) => x);
const req = { locationId: "loc1", customer: { name: "Olena", phone: "0671234567" }, lines: [{ productId: "p1", quantity: 2 }], expectedTotal: 840 };
const K = "k".repeat(20);

describe("P4 / P5 error bodies carry the order (§33.7, §33.8)", () => {
  it.each([
    [409, "stock_unavailable", "refunded"],
    [409, "payment_in_progress", "pending_payment"],
    [409, "order_expired", "expired"],
    [409, "order_closed", "cancelled"],
    [422, "payment_attempts_exceeded", "pending_payment"],
  ])("pay %i %s exposes the order", async (status, code, st) => {
    const e = await fail(setup(res(status, { code, message: "x", order: order({ status: st }) })).api.pay("s", "T"));
    expect(e).toBeInstanceOf(BookingApiError);
    expect([e.status, e.code]).toEqual([status, code]);
    expect(errorExtras(e).order?.status).toBe(st);
  });

  it.each([["cannot_cancel", "paid"], ["payment_in_progress", "pending_payment"], ["order_expired", "expired"]])("cancel 409 %s exposes the order", async (code, st) => {
    const e = await fail(setup(res(409, { code, order: order({ status: st }) })).api.cancel("s", "T"));
    expect(e.code).toBe(code);
    expect(errorExtras(e).order?.status).toBe(st);
  });

  it("cancel success resolves to the cancelled order (incl. idempotent replay)", async () => {
    const o = await setup(res(200, { order: order({ status: "cancelled", closeReason: "client" }) }, { "Idempotent-Replayed": "true" })).api.cancel("s", "T");
    expect(o.status).toBe("cancelled");
  });

  it("pay idempotent replay (already paid) resolves to the paid order", async () => {
    expect((await setup(res(200, { order: order({ status: "paid" }) }, { "Idempotent-Replayed": "true" })).api.pay("s", "T")).status).toBe("paid");
  });

  it("402 with .13 total: kopecks preserved in the body order", async () => {
    const e = await fail(setup(res(402, { code: "payment_failed", order: order({ total: 100.13 }) })).api.pay("s", "T"));
    expect(errorExtras(e).order?.total).toBe(100.13);
  });
});

describe("P2 errors", () => {
  it.each([[422, "duplicate_line"], [422, "insufficient_stock"], [422, "product_unavailable"], [422, "idempotency_key_reused"], [422, "invalid_phone"], [404, "product_orders_disabled"], [403, "tenant_suspended"]])(
    "%i %s -> BookingApiError with that code",
    async (status, code) => {
      const e = await fail(setup(res(status, { code, message: "x", line: 1 })).api.createOrder("s", req, K));
      expect(e).toBeInstanceOf(BookingApiError);
      expect([e.status, e.code]).toEqual([status, code]);
    },
  );

  it("product_unavailable productIds are exposed", async () => {
    const e = await fail(setup(res(422, { code: "product_unavailable", productIds: ["a", "b"] })).api.createOrder("s", req, K));
    expect(errorExtras(e).productIds).toEqual(["a", "b"]);
  });

  it("price_changed total keeps kopecks", async () => {
    const e = await fail(setup(res(409, { code: "price_changed", total: 1039.01 })).api.createOrder("s", req, K));
    expect(errorExtras(e).total).toBe(1039.01);
  });

  it("expectedTotal is sent unchanged (with kopecks); only P2 sends Idempotency-Key", async () => {
    const a = setup(res(201, { publicToken: "T".repeat(43), order: order() }));
    await a.api.createOrder("s", { ...req, expectedTotal: 100.13 }, K);
    expect(JSON.parse(a.fetch.mock.calls[0][1].body!).expectedTotal).toBe(100.13);
    const b = setup(res(200, { order: order() }));
    await b.api.pay("s", "T");
    await b.api.cancel("s", "T");
    for (const call of b.fetch.mock.calls) expect(call[1].headers).not.toHaveProperty("Idempotency-Key");
  });

  it("empty publicToken in 201 is rejected as invalid_response", async () => {
    const e = await fail(setup(res(201, { publicToken: "", order: order() })).api.createOrder("s", req, K));
    expect(e.status).toBe(502);
  });

  it("5xx with non-JSON body: status kept, empty code (retryable class)", async () => {
    const e = await fail(setup(res(503, "NOT_JSON")).api.createOrder("s", req, K));
    expect(e).toBeInstanceOf(BookingApiError);
    expect(e.status).toBe(503);
    expect(e.code).toBe("");
  });

  it("426 body is parsed with its code on every order route", async () => {
    const calls: Array<(a: Api) => Promise<unknown>> = [(a) => a.createOrder("s", req, K), (a) => a.pay("s", "T"), (a) => a.cancel("s", "T"), (a) => a.getOrder("s", "T"), (a) => a.getCatalog("s", "l")];
    for (const call of calls) {
      const e = await fail(call(setup(res(426, { code: "upgrade_required" })).api));
      expect([e.status, e.code]).toEqual([426, "upgrade_required"]);
    }
  });
});

describe("P1 / P3", () => {
  it("catalog 404 product_orders_disabled is surfaced with its code", async () => {
    const e = await fail(setup(res(404, { code: "product_orders_disabled" })).api.getCatalog("s", "loc1"));
    expect([e.status, e.code]).toEqual([404, "product_orders_disabled"]);
  });

  it("catalog parses, keeps kopecks, ignores unknown fields", async () => {
    const body = {
      location: { id: "l", name: "S", address: null }, currency: "UAH", reservationMinutes: 15, maxLines: 20, maxQuantityPerLine: 10, truncated: false, extra: 1,
      items: [{ id: "a", name: "A", unit: "pcs", price: 650.5, categoryPath: null, inStock: false, extra: 2 }],
    };
    const c = await setup(res(200, body)).api.getCatalog("s", "loc1");
    expect(c.items[0].price).toBe(650.5);
    expect(c).not.toHaveProperty("extra");
  });

  it("catalog path encodes slug and location id", async () => {
    const { api, fetch } = setup(res(404, { code: "location_not_found" }));
    await fail(api.getCatalog("a/b", "x y"));
    expect(fetch.mock.calls[0][0]).toBe("https://api.x/api/public/a%2Fb/locations/x%20y/products");
  });

  it("P3 404 not_found (unknown token) is distinguishable", async () => {
    const e = await fail(setup(res(404, { code: "not_found" })).api.getOrder("s", "T"));
    expect([e.status, e.code]).toEqual([404, "not_found"]);
  });

  it("order with an unknown status is rejected (502)", async () => {
    const e = await fail(setup(res(200, order({ status: "weird" }))).api.getOrder("s", "T"));
    expect(e.status).toBe(502);
  });

  it("errorExtras tolerates non-api errors and malformed extras", () => {
    expect(errorExtras(new Error("x"))).toEqual({});
    expect(errorExtras(new BookingApiError(409, "x", "m", undefined, { total: "not-a-number" }))).toEqual({});
    expect(errorExtras(new BookingApiError(409, "x", "m"))).toEqual({});
  });
});

describe("timeouts (§33.10 D: P4 30 s)", () => {
  beforeEach(() => jest.useFakeTimers());
  afterEach(() => jest.useRealTimers());

  const hanging = () =>
    jest.fn().mockImplementation(
      (_u: string, init: { signal: AbortSignal }) => new Promise((_r, rej) => init.signal.addEventListener("abort", () => rej(new Error("aborted")))),
    );

  it("P4 is aborted after 30 s, not 10 s; result is retryable api_unreachable", async () => {
    const api = createProductOrdersApi({ baseUrl: "https://api.x", fetch: hanging() });
    const p = fail(api.pay("s", "T"));
    let settled = false;
    void p.then(() => (settled = true));
    await jest.advanceTimersByTimeAsync(10_001);
    expect(settled).toBe(false);
    await jest.advanceTimersByTimeAsync(20_000);
    const e = await p;
    expect([e.status, e.code]).toEqual([0, "api_unreachable"]);
  });

  it("other routes use the 10 s default", async () => {
    const api = createProductOrdersApi({ baseUrl: "https://api.x", fetch: hanging() });
    const p = fail(api.getOrder("s", "T"));
    await jest.advanceTimersByTimeAsync(10_001);
    expect((await p).status).toBe(0);
  });
});
