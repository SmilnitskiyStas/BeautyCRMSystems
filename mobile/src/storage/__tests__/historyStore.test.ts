import { BOOKING_DRAFT_TTL_MS, RECENT_LOCATIONS_MAX, createLocalStore, type PlainKV, type SecureKV } from "../tokenStore";

function memory() {
  const secureMap = new Map<string, string>();
  const plainMap = new Map<string, string>();
  const secure: SecureKV = {
    getItemAsync: async (k) => secureMap.get(k) ?? null,
    setItemAsync: async (k, v) => void secureMap.set(k, v),
    deleteItemAsync: async (k) => void secureMap.delete(k),
  };
  const plain: PlainKV = {
    getItem: async (k) => plainMap.get(k) ?? null,
    setItem: async (k, v) => void plainMap.set(k, v),
    removeItem: async (k) => void plainMap.delete(k),
  };
  return { secure, plain, plainMap };
}

const loc = (code: string) => ({ publicCode: code, name: `Salon ${code}`, address: "вул. Хрещатик, 1", city: "Київ" });

const draft = (updatedAt: number) => ({
  publicCode: "7K2M9QXA",
  locationName: "Salon A",
  service: { id: "svc-1", name: "Манікюр", durationMinutes: 60, priceOriginal: 500, priceFinal: 450 },
  specialist: { id: "sp-1", name: "Анна", role: "Майстер" },
  date: "2026-12-01",
  updatedAt,
});

describe("recent locations", () => {
  it("keeps the latest first, without duplicates, capped", async () => {
    const m = memory();
    const s = createLocalStore(m.secure, m.plain);
    for (let i = 0; i < RECENT_LOCATIONS_MAX + 2; i++) await s.addRecentLocation(loc(`CODE${i}`), 1000 + i);
    await s.addRecentLocation(loc("CODE3"), 5000);
    const list = await s.listRecentLocations();
    expect(list).toHaveLength(RECENT_LOCATIONS_MAX);
    expect(list[0].publicCode).toBe("CODE3");
    expect(new Set(list.map((l) => l.publicCode)).size).toBe(list.length);
  });

  it("removes one venue and survives corrupted storage", async () => {
    const m = memory();
    const s = createLocalStore(m.secure, m.plain);
    await s.addRecentLocation(loc("AAAA1111"), 1);
    await s.addRecentLocation(loc("BBBB2222"), 2);
    await s.removeRecentLocation("AAAA1111");
    expect((await s.listRecentLocations()).map((l) => l.publicCode)).toEqual(["BBBB2222"]);
    m.plainMap.set("beauty.recentLocations.v1", "{not json");
    expect(await s.listRecentLocations()).toEqual([]);
  });
});

describe("booking draft", () => {
  it("round-trips and expires after TTL", async () => {
    const m = memory();
    const s = createLocalStore(m.secure, m.plain);
    await s.setBookingDraft(draft(1000));
    expect((await s.getBookingDraft(2000))?.service.id).toBe("svc-1");
    expect(await s.getBookingDraft(1000 + BOOKING_DRAFT_TTL_MS)).toBeNull();
    expect(m.plainMap.has("beauty.bookingDraft.v1")).toBe(false);
  });

  it("drops a corrupted draft", async () => {
    const m = memory();
    const s = createLocalStore(m.secure, m.plain);
    m.plainMap.set("beauty.bookingDraft.v1", JSON.stringify({ publicCode: "X" }));
    expect(await s.getBookingDraft(0)).toBeNull();
    expect(m.plainMap.has("beauty.bookingDraft.v1")).toBe(false);
  });

  it("wipeAll removes history and the draft", async () => {
    const m = memory();
    const s = createLocalStore(m.secure, m.plain);
    await s.addRecentLocation(loc("AAAA1111"), 1);
    await s.setBookingDraft(draft(Date.now()));
    await s.wipeAll();
    expect(await s.listRecentLocations()).toEqual([]);
    expect(await s.getBookingDraft()).toBeNull();
  });
});
