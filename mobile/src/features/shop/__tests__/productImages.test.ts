import { productCatalogSchema } from "@/api/productOrders";
import { galleryOf } from "../ProductImages";

const base = { id: "p", name: "N", unit: "шт", price: 1, categoryPath: null, inStock: true };
const cat = (items: unknown[]) => ({ location: { id: "l", name: "L" }, currency: "UAH", reservationMinutes: 30, maxLines: 10, maxQuantityPerLine: 5, items, truncated: false });

describe("product images", () => {
  it("V1 response without image fields parses", () => {
    const r = productCatalogSchema.safeParse(cat([base]));
    expect(r.success).toBe(true);
    expect(galleryOf(r.success ? r.data.items[0] : {})).toEqual([]);
  });
  it("null fields and V1.1 images parse", () => {
    const img = { url: "https://x/a-1600.webp", thumbUrl: "https://x/a-400.webp", width: 1, height: 1 };
    const r = productCatalogSchema.parse(cat([{ ...base, imageUrl: null, images: [] }, { ...base, id: "q", imageUrl: img.thumbUrl, images: [img] }]));
    expect(galleryOf(r.items[0])).toEqual([]);
    expect(galleryOf(r.items[1])).toEqual([img]);
  });
  it("imageUrl without images falls back to one photo", () => {
    expect(galleryOf({ imageUrl: "https://x/t.webp" })).toHaveLength(1);
  });
});
