import { describe, expect, it } from "vitest";
import { BeautyApiError } from "@/features/beauty-auth/errors";
import { DEMO_LOCATIONS } from "../../beauty-shared/demo-seed";
import { createMockInventory } from "./mock-inventory";

const NOW = Date.parse("2026-10-14T09:00:00Z");

function setup(role = "owner") {
  let r = role;
  const inv = createMockInventory({ role: () => r, locations: () => DEMO_LOCATIONS, specialistLocationIds: () => ["c"], now: () => NOW, wait: false });
  return { inv, setRole: (x: string) => (r = x) };
}
async function expectError(p: Promise<unknown>, status: number, code: string) {
  const e = await p.then(
    () => null,
    (x: unknown) => x,
  );
  expect(e).toBeInstanceOf(BeautyApiError);
  expect((e as BeautyApiError).status).toBe(status);
  expect((e as BeautyApiError).code).toBe(code);
}
const byName = <T extends { name: string }>(list: T[], name: string): T => list.find((c) => c.name === name)!;

describe("mock: дерево категорій", () => {
  it("сид: невелике дерево з лічильниками, товари мають categoryPath", async () => {
    const { inv } = setup();
    const cats = await inv.getInventoryCategories();
    expect(cats.map((c) => c.name).sort()).toEqual(["Безаміачні", "Волосся", "Гель-лаки", "Нігті", "Фарби", "Шампуні"]);
    const hair = byName(cats, "Волосся");
    expect(hair).toMatchObject({ parentId: null, childCount: 2, productCount: 0, isArchived: false });
    expect(byName(cats, "Безаміачні").productCount).toBe(1);
    const products = (await inv.getInventoryProducts({ active: "all", take: 100 })).items;
    expect(products.find((p) => p.name.startsWith("Фарба"))).toMatchObject({ categoryPath: "Волосся › Фарби › Безаміачні" });
    // Товар без категорії в дереві зберігає старий текст.
    expect(products.find((p) => p.name.startsWith("Віск"))).toMatchObject({ categoryId: null, categoryPath: null, category: "Депіляція" });
  });

  it("створення: глибина ≤ 3, дубль імені під тим самим батьком (без регістру), інший батько - можна", async () => {
    const { inv } = setup();
    const cats = await inv.getInventoryCategories();
    const leaf = byName(cats, "Безаміачні");
    await expectError(inv.createInventoryCategory({ name: "Четвертий", parentId: leaf.id }), 422, "category_depth");
    await expectError(inv.createInventoryCategory({ name: "ВОЛОССЯ" }), 409, "category_name_taken");
    await expectError(inv.createInventoryCategory({ name: "  " }), 422, "invalid_name");
    await expectError(inv.createInventoryCategory({ name: "X", parentId: "nope" }), 422, "category_not_found");
    const created = await inv.createInventoryCategory({ name: "волосся", parentId: byName(cats, "Нігті").id });
    expect(created).toMatchObject({ name: "волосся", childCount: 0, productCount: 0 });
  });

  it("перейменування: дубль серед сусідів - 409; своє ім'я з іншим регістром - можна", async () => {
    const { inv } = setup();
    const cats = await inv.getInventoryCategories();
    await expectError(inv.updateInventoryCategory(byName(cats, "Шампуні").id, { name: "фарби" }), 409, "category_name_taken");
    const r = await inv.updateInventoryCategory(byName(cats, "Шампуні").id, { name: "ШАМПУНІ" });
    expect(r.name).toBe("ШАМПУНІ");
    await expectError(inv.updateInventoryCategory("nope", { name: "A" }), 404, "category_not_found");
  });

  it("переміщення: цикл, глибина піддерева, дубль імені, успіх оновлює шлях товару", async () => {
    const { inv } = setup();
    const cats = await inv.getInventoryCategories();
    const hair = byName(cats, "Волосся");
    const dyes = byName(cats, "Фарби");
    const nails = byName(cats, "Нігті");
    await expectError(inv.moveInventoryCategory(hair.id, { parentId: byName(cats, "Безаміачні").id }), 422, "category_cycle");
    await expectError(inv.moveInventoryCategory(hair.id, { parentId: nails.id }), 422, "category_depth");
    await expectError(inv.moveInventoryCategory(dyes.id, { parentId: byName(cats, "Гель-лаки").id }), 422, "category_depth");
    await inv.moveInventoryCategory(dyes.id, { parentId: nails.id });
    const products = (await inv.getInventoryProducts({ active: "all", take: 100 })).items;
    expect(products.find((p) => p.name.startsWith("Фарба"))!.categoryPath).toBe("Нігті › Фарби › Безаміачні");
    await inv.moveInventoryCategory(dyes.id, { parentId: null });
    expect((await inv.getInventoryCategories()).find((c) => c.id === dyes.id)!.parentId).toBeNull();
  });

  it("архів: приховує з вибору (includeArchived=false), товар зберігає шлях; архівний батько блокує додавання", async () => {
    const { inv } = setup();
    const cats = await inv.getInventoryCategories();
    const dyes = byName(cats, "Фарби");
    await inv.archiveInventoryCategory(dyes.id);
    expect((await inv.getInventoryCategories()).some((c) => c.id === dyes.id)).toBe(false);
    expect((await inv.getInventoryCategories(true)).find((c) => c.id === dyes.id)!.isArchived).toBe(true);
    await expectError(inv.createInventoryCategory({ name: "Нова", parentId: dyes.id }), 422, "category_archived");
    const dye = (await inv.getInventoryProducts({ active: "all", take: 100 })).items.find((p) => p.name.startsWith("Фарба"))!;
    expect(dye.categoryPath).toBe("Волосся › Фарби › Безаміачні");
    const other = (await inv.getInventoryProducts({ active: "all", take: 100 })).items.find((p) => p.name.startsWith("Шампунь"))!;
    await expectError(inv.updateInventoryProduct(other.id, { name: other.name, unit: other.unit, purpose: other.purpose, salePrice: 450, categoryId: dyes.id }), 422, "category_archived");
    // Товар, що вже лежить в архівній категорії, можна зберегти без зміни категорії.
    await inv.updateInventoryProduct(dye.id, { name: dye.name, unit: dye.unit, purpose: dye.purpose, categoryId: byName(cats, "Безаміачні").id });
    await inv.unarchiveInventoryCategory(dyes.id);
    expect((await inv.getInventoryCategories()).some((c) => c.id === dyes.id)).toBe(true);
  });

  it("видалення: непорожню (підкатегорії/товари) - 409 category_not_empty; порожню - можна", async () => {
    const { inv } = setup();
    const cats = await inv.getInventoryCategories();
    await expectError(inv.deleteInventoryCategory(byName(cats, "Волосся").id), 409, "category_not_empty");
    await expectError(inv.deleteInventoryCategory(byName(cats, "Гель-лаки").id), 409, "category_not_empty");
    const fresh = await inv.createInventoryCategory({ name: "Порожня" });
    await inv.deleteInventoryCategory(fresh.id);
    expect((await inv.getInventoryCategories()).some((c) => c.id === fresh.id)).toBe(false);
    await expectError(inv.deleteInventoryCategory(fresh.id), 404, "category_not_found");
  });

  it("права: specialist - читання без змін (403), товар з categoryId і зміна/очищення", async () => {
    const { inv, setRole } = setup();
    const cats = await inv.getInventoryCategories();
    const gel = byName(cats, "Гель-лаки");
    const p = (await inv.getInventoryProducts({ active: "all", take: 100 })).items.find((x) => x.name.startsWith("Шампунь"))!;
    const upd = await inv.updateInventoryProduct(p.id, { name: p.name, unit: p.unit, purpose: p.purpose, salePrice: 450, categoryId: gel.id });
    expect(upd).toMatchObject({ categoryId: gel.id, categoryPath: "Нігті › Гель-лаки" });
    // categoryId відсутнє = без змін
    expect((await inv.updateInventoryProduct(p.id, { name: p.name, unit: p.unit, purpose: p.purpose, salePrice: 450 })).categoryId).toBe(gel.id);
    expect((await inv.updateInventoryProduct(p.id, { name: p.name, unit: p.unit, purpose: p.purpose, salePrice: 450, categoryId: null })).categoryPath).toBeNull();
    await expectError(inv.updateInventoryProduct(p.id, { name: p.name, unit: p.unit, purpose: p.purpose, salePrice: 450, categoryId: "nope" }), 422, "category_not_found");
    setRole("specialist");
    await expectError(inv.createInventoryCategory({ name: "Нова" }), 403, "forbidden_role");
    await expectError(inv.deleteInventoryCategory(gel.id), 403, "forbidden_role");
  });
});
