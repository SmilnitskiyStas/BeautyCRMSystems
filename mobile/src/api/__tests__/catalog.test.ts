import { createCatalogApi, roundCoord, type CatalogFetch } from "../catalog";

const ok = (body: unknown) => ({ ok: true, status: 200, statusText: "OK", headers: { get: () => null }, json: async () => body });

describe("catalog api: coordinate privacy (M2)", () => {
  it("roundCoord keeps 3 decimals", () => {
    expect(roundCoord(50.450123)).toBe(50.45);
    expect(roundCoord(30.52349)).toBe(30.523);
    expect(roundCoord(-0.0004)).toBe(-0);
  });

  it("nearby sends rounded coordinates", async () => {
    const fetch = jest.fn<ReturnType<CatalogFetch>, Parameters<CatalogFetch>>().mockResolvedValue(ok({ items: [] }));
    await createCatalogApi({ baseUrl: "https://api.x", fetch }).nearby(50.4501234, 30.5234987, 5);
    const [url, init] = fetch.mock.calls[0];
    expect(url).toBe("https://api.x/api/public/locations/nearby");
    expect(JSON.parse(init.body!)).toEqual({ lat: 50.45, lng: 30.523, radiusKm: 5 });
  });

  it("searchByCity is a POST with the city in the body, not in the URL", async () => {
    const fetch = jest.fn<ReturnType<CatalogFetch>, Parameters<CatalogFetch>>().mockResolvedValue(ok({ items: [] }));
    await createCatalogApi({ baseUrl: "https://api.x", fetch }).searchByCity(" Київ ");
    const [url, init] = fetch.mock.calls[0];
    expect(url).toBe("https://api.x/api/public/locations/search");
    expect(init.method).toBe("POST");
    expect(JSON.parse(init.body!)).toEqual({ city: "Київ" });
  });
});
