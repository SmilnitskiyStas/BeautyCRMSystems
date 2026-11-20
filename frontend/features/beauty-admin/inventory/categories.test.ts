import { describe, expect, it } from "vitest";
import {
  buildTree,
  canAddChild,
  depthOf,
  filterProductsByCategory,
  filterTree,
  moveProblem,
  moveTargets,
  pathOf,
  productCategoryLabel,
  subtreeHeight,
  subtreeIds,
  visibleNodes,
  withoutArchived,
} from "./categories";
import type { Category } from "./types";

const c = (id: string, parentId: string | null, name: string, o: Partial<Category> = {}): Category => ({ id, parentId, name, sortOrder: 0, isArchived: false, productCount: 0, childCount: 0, ...o });
const FLAT: Category[] = [
  c("h", null, "Волосся"),
  c("dye", "h", "Фарби"),
  c("amm", "dye", "Безаміачні"),
  c("sh", "h", "Шампуні", { sortOrder: -1 }),
  c("n", null, "Нігті"),
  c("gl", "n", "Гель-лаки"),
];

describe("дерево категорій", () => {
  it("buildTree: вкладеність, глибина, шлях, порядок за sortOrder", () => {
    const t = buildTree(FLAT);
    expect(t.map((x) => x.name)).toEqual(["Волосся", "Нігті"]);
    expect(t[0]!.children.map((x) => x.name)).toEqual(["Шампуні", "Фарби"]);
    const leaf = t[0]!.children[1]!.children[0]!;
    expect(leaf).toMatchObject({ depth: 3, path: "Волосся › Фарби › Безаміачні" });
  });

  it("pathOf / depthOf / subtreeHeight / subtreeIds", () => {
    expect(pathOf(FLAT, "amm")).toBe("Волосся › Фарби › Безаміачні");
    expect(pathOf(FLAT, null)).toBe("");
    expect(pathOf(FLAT, "x")).toBe("");
    expect(depthOf(FLAT, "amm")).toBe(3);
    expect(depthOf(FLAT, "x")).toBe(0);
    expect(subtreeHeight(FLAT, "h")).toBe(3);
    expect(subtreeHeight(FLAT, "gl")).toBe(1);
    expect([...subtreeIds(FLAT, "h")].sort()).toEqual(["amm", "dye", "h", "sh"]);
  });

  it("canAddChild: не глибше 3 рівнів", () => {
    expect(canAddChild(FLAT, null)).toBe(true);
    expect(canAddChild(FLAT, "dye")).toBe(true);
    expect(canAddChild(FLAT, "amm")).toBe(false);
  });

  it("moveProblem: цикл і глибина піддерева", () => {
    expect(moveProblem(FLAT, "h", "h")).toBe("category_cycle");
    expect(moveProblem(FLAT, "h", "amm")).toBe("category_cycle");
    expect(moveProblem(FLAT, "h", "gl")).toBe("category_depth");
    expect(moveProblem(FLAT, "dye", "n")).toBe(null);
    expect(moveProblem(FLAT, "amm", null)).toBe(null);
    expect(moveProblem(FLAT, "dye", "gl")).toBe("category_depth");
  });

  it("moveTargets: без самого вузла, нащадків, архівних і тих, що ламають глибину", () => {
    const flat = [...FLAT, c("arch", null, "Архів", { isArchived: true })];
    expect(moveTargets(flat, "dye").map((x) => x.id).sort()).toEqual(["h", "n"]);
    expect(moveTargets(flat, "gl").map((x) => x.id)).not.toContain("arch");
  });

  it("filterTree: збіги з предками, регістронезалежно; порожній запит - без змін", () => {
    const t = buildTree(FLAT);
    const r = filterTree(t, "БЕЗАМ");
    expect(r.nodes.map((n) => n.name)).toEqual(["Волосся"]);
    expect(r.nodes[0]!.children.map((n) => n.name)).toEqual(["Фарби"]);
    expect([...r.expand].sort()).toEqual(["dye", "h"]);
    expect(filterTree(t, "  ").nodes).toBe(t);
    expect(filterTree(t, "zzz").nodes).toEqual([]);
  });

  it("withoutArchived і visibleNodes", () => {
    const t = buildTree([...FLAT.slice(0, 2), c("a", "h", "Старе", { isArchived: true })]);
    expect(withoutArchived(t)[0]!.children.map((x) => x.id)).toEqual(["dye"]);
    expect(visibleNodes(buildTree(FLAT), new Set()).map((x) => x.id)).toEqual(["h", "n"]);
    expect(visibleNodes(buildTree(FLAT), new Set(["h", "dye"])).map((x) => x.id)).toEqual(["h", "sh", "dye", "amm", "n"]);
  });

  it("стійкість до циклів у даних", () => {
    const cyc = [c("a", "b", "A"), c("b", "a", "B")];
    expect(depthOf(cyc, "a")).toBe(2);
    expect(pathOf(cyc, "a")).toBe("B › A");
    expect(() => buildTree(cyc)).not.toThrow();
    expect(() => subtreeHeight(cyc, "a")).not.toThrow();
  });

  it("productCategoryLabel і фільтр товарів за піддеревом", () => {
    expect(productCategoryLabel({ categoryId: "dye", categoryPath: "Волосся › Фарби", category: "old" })).toEqual({ text: "Волосся › Фарби", legacy: false });
    expect(productCategoryLabel({ categoryId: null, categoryPath: null, category: "Нігті" })).toEqual({ text: "Нігті", legacy: true });
    expect(productCategoryLabel({ categoryId: null, categoryPath: null, category: null })).toBeNull();
    const items = [{ categoryId: "amm" }, { categoryId: "sh" }, { categoryId: "gl" }, { categoryId: null }];
    expect(filterProductsByCategory(items, FLAT, "h")).toHaveLength(2);
    expect(filterProductsByCategory(items, FLAT, "")).toHaveLength(4);
  });
});
