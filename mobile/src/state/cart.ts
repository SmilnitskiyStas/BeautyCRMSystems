import { create } from "zustand";
import type { PublicProduct } from "@/api/productOrders";

/** Ліміти за замовчуванням (§33.3); реальні значення беруться з відповіді P1 (`maxLines`, `maxQuantityPerLine`). */
export const DEFAULT_LIMITS = { maxLines: 20, maxQuantityPerLine: 10 };
export type CartLimits = typeof DEFAULT_LIMITS;

export interface CartLine {
  productId: string;
  name: string;
  unit: string;
  /** Ціна з останньої відповіді P1. */
  price: number;
  quantity: number;
  /** Немає в наявності / недоступний за останнім P1 або відповіддю сервера. */
  unavailable: boolean;
}

/** Сума в копійках -> без похибок float. */
export const cartTotal = (lines: CartLine[]): number =>
  lines.reduce((s, l) => s + Math.round(l.price * 100) * l.quantity, 0) / 100;

export const cartCount = (lines: CartLine[]): number => lines.reduce((s, l) => s + l.quantity, 0);

interface Cart {
  /** Кошик прив'язаний до одного закладу (§33.10 B). */
  locationId: string | null;
  lines: CartLine[];
  /** Зафіксувати заклад; інший заклад -> кошик очищається. */
  bind(locationId: string): void;
  /** false, якщо ліміт рядків вичерпано або товар недоступний; кількість обрізається до `maxQuantityPerLine`. */
  add(p: PublicProduct, limits?: CartLimits): boolean;
  setQuantity(productId: string, quantity: number, limits?: CartLimits): void;
  remove(productId: string): void;
  removeMany(ids: string[]): void;
  markUnavailable(ids: string[]): void;
  /** Оновити ціни/наявність за свіжим P1; зниклі з каталогу товари позначаються недоступними. */
  refresh(items: PublicProduct[]): void;
  /** Підставити рядки (напр. «Замовити знову»). */
  replace(locationId: string, lines: Omit<CartLine, "unavailable">[], limits?: CartLimits): void;
  clear(): void;
}

export const useCart = create<Cart>((set, get) => ({
  locationId: null,
  lines: [],
  bind: (locationId) => set((s) => (s.locationId === locationId ? s : { locationId, lines: [] })),
  add(p, limits = DEFAULT_LIMITS) {
    if (!p.inStock) return false;
    const { lines } = get();
    const cur = lines.find((l) => l.productId === p.id);
    if (!cur && lines.length >= limits.maxLines) return false;
    set({
      lines: cur
        ? lines.map((l) => (l.productId === p.id ? { ...l, price: p.price, quantity: Math.min(l.quantity + 1, limits.maxQuantityPerLine) } : l))
        : [...lines, { productId: p.id, name: p.name, unit: p.unit, price: p.price, quantity: 1, unavailable: false }],
    });
    return true;
  },
  setQuantity(productId, quantity, limits = DEFAULT_LIMITS) {
    const q = Math.min(Math.floor(quantity), limits.maxQuantityPerLine);
    set((s) => ({
      lines: q < 1 ? s.lines.filter((l) => l.productId !== productId) : s.lines.map((l) => (l.productId === productId ? { ...l, quantity: q, unavailable: false } : l)),
    }));
  },
  remove: (productId) => set((s) => ({ lines: s.lines.filter((l) => l.productId !== productId) })),
  removeMany: (ids) => set((s) => ({ lines: s.lines.filter((l) => !ids.includes(l.productId)) })),
  markUnavailable: (ids) => set((s) => ({ lines: s.lines.map((l) => (ids.includes(l.productId) ? { ...l, unavailable: true } : l)) })),
  refresh: (items) =>
    set((s) => {
      const byId = new Map(items.map((i) => [i.id, i]));
      return {
        lines: s.lines.map((l) => {
          const p = byId.get(l.productId);
          return p ? { ...l, name: p.name, price: p.price, unavailable: l.unavailable || !p.inStock } : { ...l, unavailable: true };
        }),
      };
    }),
  replace: (locationId, lines, limits = DEFAULT_LIMITS) =>
    set({
      locationId,
      lines: lines.slice(0, limits.maxLines).map((l) => ({ ...l, quantity: Math.min(l.quantity, limits.maxQuantityPerLine), unavailable: false })),
    }),
  clear: () => set({ lines: [] }),
}));
