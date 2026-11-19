import { ANY_SPECIALIST_ID, createHttpBookingApi, type FetchInitLike } from "@beauty/booking-core";
import { useDraft } from "@/state/draft";

function api(body: unknown) {
  const calls: string[] = [];
  const a = createHttpBookingApi({
    baseUrl: "https://api.test",
    fetch: async (url: string, _i: FetchInitLike) => {
      calls.push(url);
      return { ok: true, status: 200, statusText: "OK", headers: { get: () => null }, json: async () => body };
    },
  });
  return { a, calls };
}

describe("core: specialists / reviews / availability", () => {
  it("getSpecialists мапить нові поля й відкидає не-https фото", async () => {
    const { a } = api([
      { id: "s1", name: "Анна", title: "Стиліст", photoUrl: "http://x/a.jpg", experienceYears: 3, bio: " ", rating: 4.8, reviewsCount: 23 },
      { id: "s2", name: "Ія", title: null, photoUrl: "https://x/i.jpg", experienceYears: null, bio: "Люблю", rating: null, reviewsCount: 0 },
    ]);
    const [any, s1, s2] = await a.getSpecialists("t", "l1");
    expect(any.id).toBe(ANY_SPECIALIST_ID);
    expect(s1).toMatchObject({ photoUrl: null, experienceYears: 3, bio: null, rating: 4.8, reviewsCount: 23 });
    expect(s2).toMatchObject({ photoUrl: "https://x/i.jpg", rating: null, bio: "Люблю", role: "" });
  });
  it("getSpecialistReviews: URL і мапінг", async () => {
    const { a, calls } = api({ total: 1, items: [{ id: "r1", authorName: "Анна К.", rating: 5, text: null, createdAt: "2026-10-01T10:00:00Z" }] });
    const r = await a.getSpecialistReviews("t", "s1", { limit: 20, offset: 40 });
    expect(calls[0]).toBe("https://api.test/api/public/t/specialists/s1/reviews?limit=20&offset=40");
    expect(r.total).toBe(1);
    expect(r.items[0].text).toBeNull();
  });
  it("getAvailability: ANY -> без specialistId; невідомий статус -> closed", async () => {
    const { a, calls } = api({ days: [{ date: "2026-11-09", status: "available", freeSlots: 3 }, { date: "2026-11-10", status: "weird", freeSlots: 0 }] });
    const r = await a.getAvailability("t", { locationId: "l1", serviceId: "sv", specialistId: ANY_SPECIALIST_ID, from: "2026-11-09", days: 62 });
    expect(calls[0]).toBe("https://api.test/api/public/t/availability?locationId=l1&serviceId=sv&from=2026-11-09&days=62");
    expect(r.map((d) => d.status)).toEqual(["available", "closed"]);
    await a.getAvailability("t", { locationId: "l1", serviceId: "sv", specialistId: "s1", from: "2026-11-09", days: 7 });
    expect(calls[1]).toContain("specialistId=s1");
  });
});

describe("draft: скидання залежних кроків", () => {
  const svc = (id: string) => ({ id, name: id, durationMinutes: 30, priceOriginal: 1, priceFinal: 1 });
  const sp = { id: "s1", name: "Анна", role: "" };
  beforeEach(() => useDraft.getState().reset());

  it("інша послуга скидає майстра/дату/слот; та сама - ні", () => {
    const d = useDraft.getState();
    d.setService(svc("a"));
    d.setSpecialist(sp);
    d.setDate("2026-11-12");
    useDraft.getState().setService(svc("a"));
    expect(useDraft.getState()).toMatchObject({ specialistId: "s1", date: "2026-11-12" });
    useDraft.getState().setService(svc("b"));
    expect(useDraft.getState()).toMatchObject({ specialistId: ANY_SPECIALIST_ID, specialist: null, date: null });
  });
  it("зміна майстра скидає дату й слот; null = будь-який", () => {
    const d = useDraft.getState();
    d.setSpecialist(sp);
    d.setDate("2026-11-12");
    useDraft.getState().setSpecialist(null);
    expect(useDraft.getState()).toMatchObject({ specialistId: ANY_SPECIALIST_ID, specialistName: "", date: null, slot: null });
  });
});
