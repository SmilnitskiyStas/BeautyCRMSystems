import { PENDING_TTL_MS, createLocalStore } from "../tokenStore";

function memory() {
  const secureMap = new Map<string, string>();
  const plainMap = new Map<string, string>();
  const store = createLocalStore(
    {
      getItemAsync: async (k) => secureMap.get(k) ?? null,
      setItemAsync: async (k, v) => {
        if (!/^[A-Za-z0-9._-]+$/.test(k)) throw new Error(`invalid secure key ${k}`);
        secureMap.set(k, v);
      },
      deleteItemAsync: async (k) => void secureMap.delete(k),
    },
    { getItem: async (k) => plainMap.get(k) ?? null, setItem: async (k, v) => void plainMap.set(k, v), removeItem: async (k) => void plainMap.delete(k) },
  );
  return { store, secureMap, plainMap };
}
const rec = (id: string, createdAt = "2026-11-20T10:00:00.000Z") => ({ localId: id, tenantSlug: "s", locationName: "Salon", number: 1, total: 840, status: "pending_payment", createdAt });
const ID1 = "00000000-0000-4000-8000-000000000001";
const ID2 = "00000000-0000-4000-8000-000000000002";
const PK = { fingerprint: "f", key: "order-key-0123456789", createdAt: 1000 };

describe("order store (§33.10 E)", () => {
  it("upserts by localId (replayed 200 does not duplicate); list is newest first", async () => {
    const { store } = memory();
    await store.saveOrder(rec(ID1, "2026-11-20T10:00:00.000Z"), "T1");
    await store.saveOrder(rec(ID1, "2026-11-20T10:00:00.000Z"), "T1");
    await store.saveOrder(rec(ID2, "2026-11-20T11:00:00.000Z"), "T2");
    expect((await store.listOrders()).map((r) => r.localId)).toEqual([ID2, ID1]);
  });

  it("updateOrderSnapshot changes status only; token unaffected", async () => {
    const { store } = memory();
    await store.saveOrder(rec(ID1), "T1");
    await store.updateOrderSnapshot(ID1, { status: "paid" });
    expect((await store.listOrders())[0].status).toBe("paid");
    expect(await store.getOrderToken(ID1)).toBe("T1");
  });

  it("order tokens do not leak into the plain index; path-like ids are rejected", async () => {
    const { store, plainMap } = memory();
    await store.saveOrder(rec(ID1), "SECRET-T");
    expect([...plainMap.values()].join("")).not.toContain("SECRET-T");
    expect(await store.getOrderToken("../../x")).toBeNull();
  });

  it("removeOrder (404 not_found handling) drops only that order and its token", async () => {
    const { store } = memory();
    await store.saveOrder(rec(ID1), "T1");
    await store.saveOrder(rec(ID2), "T2");
    await store.removeOrder(ID1);
    expect(await store.getOrderToken(ID1)).toBeNull();
    expect(await store.getOrderToken(ID2)).toBe("T2");
    expect((await store.listOrders()).map((r) => r.localId)).toEqual([ID2]);
  });

  it("order and appointment pending keys are independent", async () => {
    const { store } = memory();
    await store.setOrderPendingKey(PK);
    expect(await store.getPendingKey(1000)).toBeNull();
    expect((await store.getOrderPendingKey(1000))?.key).toBe(PK.key);
  });

  it("order pending key expires after 24 h (boundary)", async () => {
    const { store } = memory();
    await store.setOrderPendingKey(PK);
    expect(await store.getOrderPendingKey(1000 + PENDING_TTL_MS - 1)).not.toBeNull();
    expect(await store.getOrderPendingKey(1000 + PENDING_TTL_MS)).toBeNull();
  });

  it("corrupted order index -> empty list; wipeAll still removes order tokens and pending key", async () => {
    const { store, plainMap, secureMap } = memory();
    await store.saveOrder(rec(ID1), "T1");
    await store.setOrderPendingKey(PK);
    for (const k of plainMap.keys()) if (k.includes("orders")) plainMap.set(k, "{garbage");
    expect(await store.listOrders()).toEqual([]);
    await store.wipeAll();
    expect(await store.getOrderToken(ID1)).toBeNull();
    expect(await store.getOrderPendingKey(2)).toBeNull();
    expect(secureMap.size).toBe(0);
  });
});
