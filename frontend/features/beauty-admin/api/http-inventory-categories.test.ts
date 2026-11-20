import { beforeEach, describe, expect, it, vi } from "vitest";

type Init = { method?: string; body?: string; cache?: string };
const apiJson = vi.fn<(path: string, init?: Init) => Promise<unknown>>();
vi.mock("@/features/beauty-auth/session", () => ({
  apiJson: (path: string, init?: Init) => apiJson(path, init),
  getUser: () => ({ id: "u", role: "owner", specialistId: null }),
}));

import { httpBeautyApi } from "./http-client";

beforeEach(() => {
  apiJson.mockReset();
  apiJson.mockResolvedValue(undefined);
});
const last = () => apiJson.mock.calls[apiJson.mock.calls.length - 1]!;

describe("http: категорії складу", () => {
  it("GET з includeArchived, плоский масив мапиться у вузли", async () => {
    apiJson.mockResolvedValueOnce([{ id: "a", parentId: null, name: "Волосся", sortOrder: 1, isArchived: false, productCount: 2, childCount: 1, extra: 1 }, { id: "b", parentId: "a", name: "Фарби" }]);
    const r = await httpBeautyApi.getInventoryCategories(true);
    expect(last()[0]).toBe("/api/beauty/inventory/categories?includeArchived=true");
    expect(last()[1]).toMatchObject({ cache: "no-store" });
    expect(r).toEqual([
      { id: "a", parentId: null, name: "Волосся", sortOrder: 1, isArchived: false, productCount: 2, childCount: 1 },
      { id: "b", parentId: "a", name: "Фарби", sortOrder: 0, isArchived: false, productCount: 0, childCount: 0 },
    ]);
    apiJson.mockResolvedValueOnce([]);
    await httpBeautyApi.getInventoryCategories();
    expect(last()[0]).toBe("/api/beauty/inventory/categories?includeArchived=false");
  });

  it("мутації: шляхи й методи", async () => {
    apiJson.mockResolvedValue({ id: "a", name: "A" });
    await httpBeautyApi.createInventoryCategory({ name: "A", parentId: "p" });
    expect(last()[0]).toBe("/api/beauty/inventory/categories");
    expect(last()[1]).toMatchObject({ method: "POST", body: JSON.stringify({ name: "A", parentId: "p" }) });
    await httpBeautyApi.updateInventoryCategory("a b", { name: "B" });
    expect(last()[0]).toBe("/api/beauty/inventory/categories/a%20b");
    expect(last()[1]).toMatchObject({ method: "PUT" });
    await httpBeautyApi.moveInventoryCategory("a", { parentId: null });
    expect(last()[0]).toBe("/api/beauty/inventory/categories/a/move");
    expect(last()[1]).toMatchObject({ method: "PUT", body: JSON.stringify({ parentId: null }) });
    await httpBeautyApi.archiveInventoryCategory("a");
    expect(last()[0]).toBe("/api/beauty/inventory/categories/a/archive");
    expect(last()[1]).toMatchObject({ method: "POST" });
    await httpBeautyApi.unarchiveInventoryCategory("a");
    expect(last()[0]).toBe("/api/beauty/inventory/categories/a/unarchive");
    await httpBeautyApi.deleteInventoryCategory("a");
    expect(last()[0]).toBe("/api/beauty/inventory/categories/a");
    expect(last()[1]).toMatchObject({ method: "DELETE" });
  });

  it("товар: categoryId/categoryPath мапляться, відсутні -> null", async () => {
    apiJson.mockResolvedValueOnce({ items: [{ id: "p1", name: "A", unit: "ml", purpose: "sale", categoryId: "c1", categoryPath: "Волосся › Фарби", category: "Старе" }, { id: "p2", name: "B", unit: "ml", purpose: "sale" }], hasMore: false });
    const items = (await httpBeautyApi.getInventoryProducts()).items;
    expect(items[0]).toMatchObject({ categoryId: "c1", categoryPath: "Волосся › Фарби", category: "Старе" });
    expect(items[1]).toMatchObject({ categoryId: null, categoryPath: null, category: null });
  });
});
