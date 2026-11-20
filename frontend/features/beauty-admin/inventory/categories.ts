import type { Category, Product } from "./types";

/** Найбільша глибина дерева категорій (кореневий вузол = рівень 1). */
export const MAX_CATEGORY_DEPTH = 3;
export const PATH_SEPARATOR = " › ";

export interface CategoryNode extends Category {
  /** Рівень: 1 - корінь. */
  depth: number;
  /** Повний шлях «Волосся › Фарби». */
  path: string;
  children: CategoryNode[];
}

type Linked = Pick<Category, "id" | "parentId">;
type Named = Linked & Pick<Category, "name">;

const bySort = (a: Category, b: Category) => a.sortOrder - b.sortOrder || a.name.localeCompare(b.name, "uk");

export function indexById<T extends Linked>(flat: readonly T[]): Map<string, T> {
  return new Map(flat.map((c) => [c.id, c]));
}

/** Глибина вузла (1 - корінь); `0` - вузла немає. Захищено від циклів у некоректних даних. */
export function depthOf(flat: readonly Linked[], id: string): number {
  const by = indexById(flat);
  let depth = 0;
  let cur = by.get(id);
  const seen = new Set<string>();
  while (cur && !seen.has(cur.id)) {
    seen.add(cur.id);
    depth += 1;
    cur = cur.parentId ? by.get(cur.parentId) : undefined;
  }
  return depth;
}

/** Шлях вузла «Батько › Дитина»; порожній рядок, якщо вузла немає. */
export function pathOf(flat: readonly Named[], id: string | null | undefined): string {
  if (!id) return "";
  const by = indexById(flat);
  const names: string[] = [];
  const seen = new Set<string>();
  let cur = by.get(id);
  while (cur && !seen.has(cur.id)) {
    seen.add(cur.id);
    names.unshift(cur.name);
    cur = cur.parentId ? by.get(cur.parentId) : undefined;
  }
  return names.join(PATH_SEPARATOR);
}

/** Дерево з плоского списку: діти впорядковані за `sortOrder`, потім за назвою. Вузли з невідомим батьком стають кореневими. */
export function buildTree(flat: readonly Category[]): CategoryNode[] {
  const by = indexById(flat);
  const kids = new Map<string | null, Category[]>();
  for (const c of flat) {
    const key = c.parentId && by.has(c.parentId) ? c.parentId : null;
    kids.set(key, [...(kids.get(key) ?? []), c]);
  }
  const make = (c: Category, depth: number, parentPath: string, trail: Set<string>): CategoryNode => {
    const path = parentPath ? `${parentPath}${PATH_SEPARATOR}${c.name}` : c.name;
    const next = new Set(trail).add(c.id);
    return {
      ...c,
      depth,
      path,
      children: [...(kids.get(c.id) ?? [])]
        .sort(bySort)
        .filter((k) => !next.has(k.id))
        .map((k) => make(k, depth + 1, path, next)),
    };
  };
  return [...(kids.get(null) ?? [])].sort(bySort).map((c) => make(c, 1, "", new Set()));
}

/** Ідентифікатори вузла й усіх його нащадків. */
export function subtreeIds(flat: readonly Linked[], id: string): Set<string> {
  const out = new Set<string>([id]);
  let grew = true;
  while (grew) {
    grew = false;
    for (const c of flat) {
      if (c.parentId && out.has(c.parentId) && !out.has(c.id)) {
        out.add(c.id);
        grew = true;
      }
    }
  }
  return out;
}

/** Висота піддерева, рахуючи сам вузол (лист = 1). */
export function subtreeHeight(flat: readonly Linked[], id: string, seen: ReadonlySet<string> = new Set()): number {
  const next = new Set(seen).add(id);
  return 1 + Math.max(0, ...flat.filter((c) => c.parentId === id && !next.has(c.id)).map((c) => subtreeHeight(flat, c.id, next)));
}

/** Чи можна додати підкатегорію під вузол (глибина вузла < 3). `parentId = null` - корінь, завжди можна. */
export function canAddChild(flat: readonly Linked[], parentId: string | null): boolean {
  return parentId === null || depthOf(flat, parentId) < MAX_CATEGORY_DEPTH;
}

/**
 * Перевірка переміщення вузла під нового батька (`null` - у корінь): `category_cycle`, якщо батько - сам вузол або його
 * нащадок; `category_depth`, якщо піддерево після переміщення вийде за 3 рівні; інакше `null`.
 */
export function moveProblem(flat: readonly Linked[], id: string, newParentId: string | null): "category_cycle" | "category_depth" | null {
  if (newParentId !== null && subtreeIds(flat, id).has(newParentId)) return "category_cycle";
  const base = newParentId === null ? 0 : depthOf(flat, newParentId);
  return base + subtreeHeight(flat, id) > MAX_CATEGORY_DEPTH ? "category_depth" : null;
}

/** Нові батьки, доступні для переміщення вузла: активні, без самого вузла й нащадків, з урахуванням глибини піддерева. */
export function moveTargets(flat: readonly Category[], id: string): CategoryNode[] {
  const out: CategoryNode[] = [];
  const walk = (nodes: CategoryNode[]) => {
    for (const n of nodes) {
      if (!n.isArchived && moveProblem(flat, id, n.id) === null) out.push(n);
      walk(n.children);
    }
  };
  walk(buildTree(flat));
  return out;
}

/** Фільтр дерева за назвою (без урахування регістру): лишаються збіги та їхні предки; `expand` - предки збігів. */
export function filterTree(nodes: CategoryNode[], query: string): { nodes: CategoryNode[]; expand: Set<string> } {
  const q = query.trim().toLowerCase();
  if (!q) return { nodes, expand: new Set() };
  const expand = new Set<string>();
  const walk = (list: CategoryNode[]): CategoryNode[] =>
    list.flatMap((n) => {
      const kids = walk(n.children);
      if (n.name.toLowerCase().includes(q) || kids.length > 0) {
        if (kids.length > 0) expand.add(n.id);
        return [{ ...n, children: kids }];
      }
      return [];
    });
  return { nodes: walk(nodes), expand };
}

/** Прибрати з дерева архівні вузли разом з нащадками (для вибору категорії). */
export function withoutArchived(nodes: CategoryNode[]): CategoryNode[] {
  return nodes.filter((n) => !n.isArchived).map((n) => ({ ...n, children: withoutArchived(n.children) }));
}

/** Видимі вузли у порядку обходу (для навігації клавіатурою). */
export function visibleNodes(nodes: CategoryNode[], expanded: ReadonlySet<string>): CategoryNode[] {
  return nodes.flatMap((n) => [n, ...(expanded.has(n.id) ? visibleNodes(n.children, expanded) : [])]);
}

/** Підпис категорії товару: шлях із дерева, інакше старий текст з маркером. */
export function productCategoryLabel(p: Pick<Product, "categoryId" | "categoryPath" | "category">): { text: string; legacy: boolean } | null {
  if (p.categoryId && p.categoryPath) return { text: p.categoryPath, legacy: false };
  if (p.category) return { text: p.category, legacy: true };
  return null;
}

/**
 * Клієнтський фільтр списку товарів за вузлом дерева (включно з підкатегоріями).
 * TODO: замінити серверним `categoryId` у запиті, коли бекенд його підтримає (зараз фільтр діє лише на завантажені сторінки).
 */
export function filterProductsByCategory<T extends Pick<Product, "categoryId">>(items: T[], flat: readonly Category[], categoryId: string): T[] {
  if (!categoryId) return items;
  const ids = subtreeIds(flat, categoryId);
  return items.filter((p) => p.categoryId !== null && ids.has(p.categoryId));
}
