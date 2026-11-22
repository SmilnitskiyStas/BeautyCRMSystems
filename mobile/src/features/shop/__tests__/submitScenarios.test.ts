import { BookingApiError } from "@beauty/booking-core";
import { productOrderSchema } from "@/api/productOrders";
import { order as rawOrder } from "@/api/__tests__/orderFixture";
import { PENDING_TTL_MS, createLocalStore } from "@/storage/tokenStore";
import { createOrderSubmitter } from "../submit";

const req = (over: Record<string, unknown> = {}) => ({ locationId: "loc1", customer: { name: "Olena", phone: "0671234567" }, lines: [{ productId: "p1", quantity: 2 }], expectedTotal: 840, ...over });
const created = (replayed = false) => ({ publicToken: "SECRET-ORDER-TOKEN", order: productOrderSchema.parse(rawOrder()), replayed });

function setup(createOrder: jest.Mock, nowRef = { t: Date.parse("2026-11-20T10:00:00Z") }) {
  const secureMap = new Map<string, string>();
  const plainMap = new Map<string, string>();
  const store = createLocalStore(
    { getItemAsync: async (k) => secureMap.get(k) ?? null, setItemAsync: async (k, v) => void secureMap.set(k, v), deleteItemAsync: async (k) => void secureMap.delete(k) },
    { getItem: async (k) => plainMap.get(k) ?? null, setItem: async (k, v) => void plainMap.set(k, v), removeItem: async (k) => void plainMap.delete(k) },
  );
  let n = 0;
  const submitter = createOrderSubmitter({ api: { createOrder }, store, generateKey: () => `00000000-0000-4000-8000-${String(++n).padStart(12, "0")}`, now: () => new Date(nowRef.t) });
  // Читати pending-ключ на тому ж фіксованому годиннику, що й submitter: дефолт Date.now() робить тести залежними від реальної дати (TTL 24 год).
  const pendingKey = () => store.getOrderPendingKey(nowRef.t);
  return { submitter, store, secureMap, nowRef, pendingKey };
}
const keys = (m: jest.Mock) => m.mock.calls.map((c) => c[2] as string);

describe("P2 idempotency (§33.6, §33.10 C)", () => {
  it("lost response (timeout) then retry: SAME key and SAME body", async () => {
    const createOrder = jest.fn().mockRejectedValueOnce(new BookingApiError(0, "api_unreachable")).mockResolvedValue(created(true));
    const { submitter } = setup(createOrder);
    await expect(submitter.submit("s", req(), "S")).rejects.toBeInstanceOf(BookingApiError);
    await submitter.submit("s", req(), "S");
    const k = keys(createOrder);
    expect(k[1]).toBe(k[0]);
    expect(createOrder.mock.calls[1][1]).toEqual(createOrder.mock.calls[0][1]);
  });

  it("replayed 200 is handled like 201: token saved, pending key cleared", async () => {
    const { submitter, store, pendingKey } = setup(jest.fn().mockResolvedValue(created(true)));
    const r = await submitter.submit("s", req(), "S");
    expect(await store.getOrderToken(r.localId)).toBe("SECRET-ORDER-TOKEN");
    expect(await pendingKey()).toBeNull();
  });

  it.each([
    ["expectedTotal", { expectedTotal: 900 }],
    ["phone", { customer: { name: "Olena", phone: "0501112233" } }],
    ["name", { customer: { name: "Anna", phone: "0671234567" } }],
    ["lines", { lines: [{ productId: "p2", quantity: 2 }] }],
    ["location", { locationId: "loc2" }],
  ])("changing %s after a network failure gets a NEW key", async (_n, over) => {
    const createOrder = jest.fn().mockRejectedValueOnce(new BookingApiError(0, "api_unreachable")).mockResolvedValue(created());
    const { submitter } = setup(createOrder);
    await submitter.submit("s", req(), "S").catch(() => undefined);
    await submitter.submit("s", req(over), "S");
    const k = keys(createOrder);
    expect(k[1]).not.toBe(k[0]);
  });

  it("409 price_changed is final: pending key cleared, next submit (confirmed new total) uses a new key", async () => {
    const createOrder = jest.fn().mockRejectedValueOnce(new BookingApiError(409, "price_changed", "x", undefined, { total: 900 })).mockResolvedValue(created());
    const { submitter, store, pendingKey } = setup(createOrder);
    await expect(submitter.submit("s", req(), "S")).rejects.toMatchObject({ code: "price_changed" });
    expect(await pendingKey()).toBeNull();
    await submitter.submit("s", req({ expectedTotal: 900 }), "S");
    const k = keys(createOrder);
    expect(k[1]).not.toBe(k[0]);
  });

  it.each([[422, "duplicate_line"], [422, "insufficient_stock"], [422, "product_unavailable"], [422, "idempotency_key_reused"]])(
    "%i %s clears the pending key and propagates",
    async (status, code) => {
      const { submitter, store, pendingKey } = setup(jest.fn().mockRejectedValue(new BookingApiError(status, code)));
      await expect(submitter.submit("s", req(), "S")).rejects.toMatchObject({ status, code });
      expect(await pendingKey()).toBeNull();
    },
  );

  it.each([[429, "rate_limited"], [404, "product_orders_disabled"], [426, "upgrade_required"], [403, "tenant_suspended"]])(
    "%i %s keeps the pending key and propagates",
    async (status, code) => {
      const { submitter, store, pendingKey } = setup(jest.fn().mockRejectedValue(new BookingApiError(status, code)));
      await expect(submitter.submit("s", req(), "S")).rejects.toMatchObject({ status, code });
      expect(await pendingKey()).not.toBeNull();
    },
  );

  it("network error -> 429 -> retry reuses the SAME key", async () => {
    const createOrder = jest
      .fn()
      .mockRejectedValueOnce(new BookingApiError(0, "api_unreachable"))
      .mockRejectedValueOnce(new BookingApiError(429, "rate_limited"))
      .mockResolvedValue(created(true));
    const { submitter } = setup(createOrder);
    await submitter.submit("s", req(), "S").catch(() => undefined);
    await submitter.submit("s", req(), "S").catch(() => undefined);
    await submitter.submit("s", req(), "S");
    const k = keys(createOrder);
    expect(k[1]).toBe(k[0]);
    expect(k[2]).toBe(k[0]);
  });

  it("pending key is persisted BEFORE the request is sent (survives app restart)", async () => {
    let seen: unknown = "unset";
    const holder: { pendingKey?: ReturnType<typeof setup>["pendingKey"] } = {};
    const createOrder = jest.fn().mockImplementation(async () => {
      seen = await holder.pendingKey!();
      return created();
    });
    const s = setup(createOrder);
    holder.pendingKey = s.pendingKey;
    await s.submitter.submit("s", req(), "S");
    expect(seen).toMatchObject({ key: expect.any(String) });
  });

  it("restart after crash: a new submitter reuses the persisted key for the same body", async () => {
    const createOrder = jest.fn().mockRejectedValue(new BookingApiError(0, "api_unreachable"));
    const a = setup(createOrder);
    await a.submitter.submit("s", req(), "S").catch(() => undefined);
    const b = createOrderSubmitter({ api: { createOrder }, store: a.store, generateKey: () => "should-not-be-used", now: () => new Date(a.nowRef.t) });
    await b.submit("s", req(), "S").catch(() => undefined);
    const k = keys(createOrder);
    expect(k[1]).toBe(k[0]);
  });

  it("pending key older than 24 h is discarded: new key", async () => {
    const createOrder = jest.fn().mockRejectedValue(new BookingApiError(0, "api_unreachable"));
    const { submitter, nowRef } = setup(createOrder);
    await submitter.submit("s", req(), "S").catch(() => undefined);
    nowRef.t += PENDING_TTL_MS + 1;
    await submitter.submit("s", req(), "S").catch(() => undefined);
    const k = keys(createOrder);
    expect(k[1]).not.toBe(k[0]);
  });

  it("double tap while in flight: one request, same promise", async () => {
    let release!: (v: unknown) => void;
    const createOrder = jest.fn().mockImplementation(() => new Promise((r) => (release = r)));
    const { submitter } = setup(createOrder);
    const p1 = submitter.submit("s", req(), "S");
    const p2 = submitter.submit("s", req(), "S");
    await new Promise((r) => setTimeout(r, 0));
    release(created());
    const [a, b] = await Promise.all([p1, p2]);
    expect(createOrder).toHaveBeenCalledTimes(1);
    expect(a.localId).toBe(b.localId);
  });

  it("after a failure the in-flight lock is released (retry possible)", async () => {
    const createOrder = jest.fn().mockRejectedValueOnce(new BookingApiError(503, "")).mockResolvedValue(created());
    const { submitter } = setup(createOrder);
    await submitter.submit("s", req(), "S").catch(() => undefined);
    await expect(submitter.submit("s", req(), "S")).resolves.toBeDefined();
  });

  it("token is never part of the plain index", async () => {
    const { submitter, store, pendingKey } = setup(jest.fn().mockResolvedValue(created()));
    await submitter.submit("s", req(), "S");
    expect(JSON.stringify(await store.listOrders())).not.toContain("SECRET");
  });
});
