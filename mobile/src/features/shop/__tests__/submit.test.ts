import { BookingApiError } from "@beauty/booking-core";
import { createLocalStore } from "@/storage/tokenStore";
import { productOrderSchema } from "@/api/productOrders";
import { order as rawOrder } from "@/api/__tests__/orderFixture";
import { createOrderSubmitter } from "../submit";

const req = (qty = 2) => ({ locationId: "loc1", customer: { name: "Olena", phone: "0671234567" }, lines: [{ productId: "p1", quantity: qty }], expectedTotal: 840 });
const created = { publicToken: "SECRET-ORDER-TOKEN", order: productOrderSchema.parse(rawOrder()), replayed: false };

function setup(createOrder: jest.Mock) {
  const secureMap = new Map<string, string>();
  const plainMap = new Map<string, string>();
  const store = createLocalStore(
    { getItemAsync: async (k) => secureMap.get(k) ?? null, setItemAsync: async (k, v) => void secureMap.set(k, v), deleteItemAsync: async (k) => void secureMap.delete(k) },
    { getItem: async (k) => plainMap.get(k) ?? null, setItem: async (k, v) => void plainMap.set(k, v), removeItem: async (k) => void plainMap.delete(k) },
  );
  let n = 0;
  const submitter = createOrderSubmitter({ api: { createOrder }, store, generateKey: () => `00000000-0000-4000-8000-${String(++n).padStart(12, "0")}` });
  return { submitter, store, secureMap, plainMap };
}

describe("order submitter", () => {
  it("stores the token in SecureStore under the idempotency-key id, keeps it out of the index", async () => {
    const createOrder = jest.fn().mockResolvedValue(created);
    const { submitter, store, secureMap, plainMap } = setup(createOrder);
    const r = await submitter.submit("salon", req(), "Salon");
    expect(createOrder.mock.calls[0][2]).toBe(r.localId);
    expect(secureMap.get(`order-token.${r.localId}`)).toBe("SECRET-ORDER-TOKEN");
    expect(plainMap.get("beauty.orders.index.v1")).not.toContain("SECRET-ORDER-TOKEN");
    expect(await store.getOrderToken(r.localId)).toBe("SECRET-ORDER-TOKEN");
    expect((await store.listOrders())[0]).toMatchObject({ number: 1042, total: 840 });
    expect(await store.getOrderPendingKey()).toBeNull();
  });

  it("network failure keeps the key: retry with the same body reuses it, changed body gets a new one", async () => {
    const createOrder = jest.fn().mockRejectedValueOnce(new BookingApiError(0, "api_unreachable")).mockRejectedValueOnce(new BookingApiError(503, "")).mockResolvedValue(created);
    const { submitter } = setup(createOrder);
    await expect(submitter.submit("s", req(), "S")).rejects.toBeInstanceOf(BookingApiError);
    await expect(submitter.submit("s", req(), "S")).rejects.toBeInstanceOf(BookingApiError);
    await submitter.submit("s", req(3), "S");
    const keys = createOrder.mock.calls.map((c) => c[2]);
    expect(keys[1]).toBe(keys[0]);
    expect(keys[2]).not.toBe(keys[0]);
  });

  it("final 4xx rejection clears the pending key", async () => {
    const createOrder = jest.fn().mockRejectedValue(new BookingApiError(422, "insufficient_stock", "x", undefined, { productIds: ["p1"] }));
    const { submitter, store } = setup(createOrder);
    await expect(submitter.submit("s", req(), "S")).rejects.toMatchObject({ code: "insufficient_stock" });
    expect(await store.getOrderPendingKey()).toBeNull();
  });

  it("removeOrder and wipeAll delete order tokens", async () => {
    const { submitter, store, secureMap } = setup(jest.fn().mockResolvedValue(created));
    const r = await submitter.submit("s", req(), "S");
    await store.removeOrder(r.localId);
    expect(secureMap.has(`order-token.${r.localId}`)).toBe(false);
    const r2 = await submitter.submit("s", req(5), "S");
    await store.wipeAll();
    expect(await store.getOrderToken(r2.localId)).toBeNull();
    expect(await store.listOrders()).toEqual([]);
  });
});
