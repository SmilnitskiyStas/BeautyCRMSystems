import { productSchema } from "../productOrders";

const base = { id: "p", name: "n", unit: "pcs", price: 1, categoryPath: null, inStock: true };

describe("productSchema image URLs (m8)", () => {
  it("приймає https", () => {
    const p = productSchema.parse({ ...base, imageUrl: "https://a.x/i.jpg", images: [{ url: "https://a.x/1", thumbUrl: "https://a.x/t" }] });
    expect(p.imageUrl).toBe("https://a.x/i.jpg");
    expect(p.images).toHaveLength(1);
  });
  it("http/сміття трактує як відсутнє, не ламаючи розбір", () => {
    const p = productSchema.parse({
      ...base,
      imageUrl: "http://a.x/i.jpg",
      images: [{ url: "javascript:alert(1)", thumbUrl: "https://a.x/t" }, { url: "https://a.x/1", thumbUrl: "https://a.x/t" }],
    });
    expect(p.imageUrl).toBeNull();
    expect(p.images).toEqual([{ url: "https://a.x/1", thumbUrl: "https://a.x/t" }]);
  });
});
