import { ROLE_PRESETS } from "@/features/beauty-auth/access";
import { BeautyApiError } from "@/features/beauty-auth/errors";
import type { CancelProductOrderResult, ProductOrder, ProductOrderQuery, ProductOrderStatus, ProductOrderStatusFilter, ProductOrderSummary } from "../inventory/orders";
import { CANCEL_REASON_MAX } from "../inventory/orders";
import type { NamedRef, Page, ProductRef } from "../inventory/types";
import type { BeautyLocation } from "../types";

/**
 * Mock замовлень товарів (§33.13, ADR-026). Правила контракту: фільтр за ЕФЕКТИВНИМ статусом (`pending_payment` без оплати
 * після `reservationExpiresAt` = `expired`), `open` = `pending_payment` + `paid`, період за замовч. 30 діб (≤ 366),
 * сортування `createdAt DESC`; поза scope -> `404 product_order_not_found`; дії - лише з `inventory.manage` (specialist -> 403).
 * Телефон повний лише за `clients.contacts = full`, інакше маска `+••••••••••33`. Видача повторна - noop.
 * Демо: №1034 - перше скасування оплаченого завершується `402 refund_failed` (замовлення лишається `paid`), друге проходить;
 * №1041 - зависла оплата («Перевірити оплату» завершує її).
 */

export interface ProductOrdersMockDeps {
  role: () => string;
  locations: () => BeautyLocation[];
  /** Заклади спеціаліста (`inventory.view = own`). */
  specialistLocationIds?: () => string[];
  now?: () => number;
  /** `false` - без затримки (тести). */
  wait?: boolean;
  userName?: string;
}

const err = (status: number, code: string, body: Record<string, unknown> = {}) =>
  new BeautyApiError(status, code, code, undefined, undefined, undefined, undefined, typeof body.permission === "string" ? body.permission : undefined, body);

const MIN = 60_000;
const DAY = 24 * 60 * MIN;
const DEFAULT_RANGE_DAYS = 30;
const MAX_RANGE_DAYS = 366;
const STATUSES: readonly ProductOrderStatus[] = ["pending_payment", "paid", "fulfilled", "cancelled", "expired", "refunded"];

interface Rec {
  id: string;
  number: number;
  /** Збережений статус; ефективний рахується з `reservationExpiresAt`. */
  status: ProductOrderStatus;
  locationIndex: number;
  customer: { name: string; phone: string };
  lines: { productId: string; name: string; unit: ProductRef["unit"]; quantity: number; unitPrice: number }[];
  createdAt: number;
  reservationExpiresAt: number | null;
  paidAt: number | null;
  fulfilledAt: number | null;
  closedAt: number | null;
  closeReason: string | null;
  paymentInProgress: boolean;
  refundInProgress: boolean;
  refundAmount: number | null;
  cancelNote: string | null;
  fulfilledBy: NamedRef | null;
  cancelledBy: NamedRef | null;
  payment: { status: "pending" | "paid" | "failed" | "refunded"; attempts: number } | null;
  source: "web" | "ios" | "android";
  refundFailuresLeft: number;
  saleOperationId: string | null;
  returnOperationId: string | null;
}

const PRODUCTS = {
  shampoo: { productId: "mock-sale-shampoo", name: "Шампунь професійний 500 мл", unit: "pcs" as const, unitPrice: 450 },
  gel: { productId: "mock-sale-gel", name: "Гель-лак (база)", unit: "pcs" as const, unitPrice: 299.9 },
  mask: { productId: "mock-sale-mask", name: "Маска для обличчя", unit: "pcs" as const, unitPrice: 380 },
  oil: { productId: "mock-sale-oil", name: "Олія для кутикули", unit: "pcs" as const, unitPrice: 120 },
};

const round2 = (n: number) => Math.round(n * 100) / 100;
const total = (r: Rec) => round2(r.lines.reduce((s, l) => s + l.unitPrice * l.quantity, 0));

export const maskPhone = (phone: string) => `+${"•".repeat(10)}${phone.replace(/\D/g, "").slice(-2)}`;

export function createMockProductOrders(deps: ProductOrdersMockDeps) {
  const now = deps.now ?? (() => Date.now());
  const me: NamedRef = { id: "mock-user", name: deps.userName ?? "Світлана Коваленко" };
  const iso = (ms: number) => new Date(ms).toISOString();
  const wait = async <T>(v: T): Promise<T> => {
    if (deps.wait !== false) await new Promise((r) => setTimeout(r, 120));
    return structuredClone(v);
  };

  let recs: Rec[] | null = null;

  const seed = (): Rec[] => {
    const t = now();
    const mk = (number: number, o: Partial<Rec> & Pick<Rec, "customer" | "lines" | "createdAt" | "status">): Rec => ({
      id: `po-${number}`,
      number,
      locationIndex: 0,
      reservationExpiresAt: null,
      paidAt: null,
      fulfilledAt: null,
      closedAt: null,
      closeReason: null,
      paymentInProgress: false,
      refundInProgress: false,
      refundAmount: null,
      cancelNote: null,
      fulfilledBy: null,
      cancelledBy: null,
      payment: null,
      source: "ios",
      refundFailuresLeft: 0,
      saleOperationId: null,
      returnOperationId: null,
      ...o,
    });
    const paid = (at: number) => ({ paidAt: at, payment: { status: "paid" as const, attempts: 1 } });
    return [
      mk(1042, { status: "paid", customer: { name: "Олена Мельник", phone: "+380501234567" }, lines: [{ ...PRODUCTS.shampoo, quantity: 1 }, { ...PRODUCTS.oil, quantity: 2 }], createdAt: t - 25 * MIN, ...paid(t - 24 * MIN) }),
      mk(1041, { status: "pending_payment", customer: { name: "Тетяна Бондар", phone: "+380671112233" }, lines: [{ ...PRODUCTS.mask, quantity: 1 }], createdAt: t - 9 * MIN, reservationExpiresAt: t + 6 * MIN, paymentInProgress: true, payment: { status: "pending", attempts: 1 }, source: "android" }),
      mk(1040, { status: "pending_payment", customer: { name: "Марія Коваль", phone: "+380931234500" }, lines: [{ ...PRODUCTS.gel, quantity: 3 }], createdAt: t - 4 * MIN, reservationExpiresAt: t + 11 * MIN, source: "web" }),
      mk(1039, { status: "fulfilled", locationIndex: 1, customer: { name: "Ірина Савчук", phone: "+380970001122" }, lines: [{ ...PRODUCTS.shampoo, quantity: 2 }], createdAt: t - 2 * DAY, ...paid(t - 2 * DAY + 3 * MIN), fulfilledAt: t - 2 * DAY + 5 * 60 * MIN, fulfilledBy: me, saleOperationId: "op-sale-1039" }),
      mk(1038, { status: "refunded", customer: { name: "Наталія Гриценко", phone: "+380631239876" }, lines: [{ ...PRODUCTS.mask, quantity: 2 }], createdAt: t - 3 * DAY, ...paid(t - 3 * DAY + 2 * MIN), closedAt: t - 3 * DAY + 60 * MIN, closeReason: "staff", refundAmount: 760, cancelNote: "Клієнтка попросила скасувати", cancelledBy: me, saleOperationId: "op-sale-1038", returnOperationId: "op-ret-1038", payment: { status: "refunded", attempts: 1 } }),
      mk(1037, { status: "cancelled", customer: { name: "Юлія Остапенко", phone: "+380501110099" }, lines: [{ ...PRODUCTS.oil, quantity: 1 }], createdAt: t - 5 * DAY, closedAt: t - 5 * DAY + 4 * MIN, closeReason: "client", source: "web" }),
      mk(1036, { status: "pending_payment", locationIndex: 1, customer: { name: "Софія Лисенко", phone: "+380681234000" }, lines: [{ ...PRODUCTS.gel, quantity: 1 }], createdAt: t - 40 * MIN, reservationExpiresAt: t - 25 * MIN, closedAt: t - 25 * MIN, closeReason: "expired" }),
      mk(1034, { status: "paid", locationIndex: 1, customer: { name: "Анна Шевчук", phone: "+380991234567" }, lines: [{ ...PRODUCTS.shampoo, quantity: 1 }, { ...PRODUCTS.mask, quantity: 1 }], createdAt: t - 3 * 60 * MIN, ...paid(t - 3 * 60 * MIN + MIN), refundFailuresLeft: 1 }),
    ];
  };
  const all = (): Rec[] => (recs ??= seed());

  // ---------- права й scope ----------
  const role = () => deps.role();
  const canManage = () => role() !== "specialist";
  const fullContacts = () => ROLE_PRESETS[role() as keyof typeof ROLE_PRESETS]?.["clients.contacts"] === "full";
  const scopeLocations = (): BeautyLocation[] => {
    const locs = deps.locations().filter((l) => l.isActive !== false);
    if (role() !== "specialist") return locs;
    const mine = new Set(deps.specialistLocationIds?.() ?? []);
    return locs.filter((l) => mine.has(l.id));
  };
  /** Заклад замовлення (за індексом серед активних закладів; якщо закладів менше - перший). */
  const locationOf = (r: Rec): NamedRef => {
    const locs = deps.locations().filter((l) => l.isActive !== false);
    const l = locs[r.locationIndex] ?? locs[0];
    return l ? { id: l.id, name: l.name } : { id: "", name: "" };
  };
  const inScope = (r: Rec) => scopeLocations().some((l) => l.id === locationOf(r).id);
  const requireManage = () => {
    if (!canManage()) throw err(403, "forbidden_role", { permission: "inventory.manage" });
  };

  const effective = (r: Rec): ProductOrderStatus => (r.status === "pending_payment" && !r.paymentInProgress && r.reservationExpiresAt !== null && r.reservationExpiresAt <= now() ? "expired" : r.status);

  const toSummary = (r: Rec): ProductOrderSummary => {
    const status = effective(r);
    const phone = r.customer.phone;
    return {
      id: r.id,
      number: r.number,
      status,
      location: locationOf(r),
      customer: { name: r.customer.name, phone: fullContacts() ? phone : null, phoneMasked: fullContacts() ? null : maskPhone(phone) },
      lineCount: r.lines.length,
      itemCount: r.lines.reduce((s, l) => s + l.quantity, 0),
      total: total(r),
      currency: "UAH",
      createdAt: iso(r.createdAt),
      reservationExpiresAt: status === "pending_payment" && r.reservationExpiresAt !== null ? iso(r.reservationExpiresAt) : null,
      paidAt: r.paidAt === null ? null : iso(r.paidAt),
      fulfilledAt: r.fulfilledAt === null ? null : iso(r.fulfilledAt),
      closedAt: r.closedAt === null ? null : iso(r.closedAt),
      closeReason: r.closeReason,
      paymentInProgress: r.paymentInProgress,
      refundInProgress: r.refundInProgress,
    };
  };
  const toOrder = (r: Rec): ProductOrder => ({
    ...toSummary(r),
    lines: r.lines.map((l) => ({
      product: { id: l.productId, name: l.name, unit: l.unit, purpose: "sale", category: null, barcode: null, isActive: true },
      name: l.name,
      unit: l.unit,
      quantity: l.quantity,
      unitPrice: l.unitPrice,
      lineTotal: round2(l.unitPrice * l.quantity),
    })),
    refundAmount: r.refundAmount,
    cancelNote: r.cancelNote,
    fulfilledBy: r.fulfilledBy,
    cancelledBy: r.cancelledBy,
    clientId: null,
    payment: r.payment,
    saleOperationId: r.saleOperationId,
    returnOperationId: r.returnOperationId,
    source: r.source,
  });

  const byId = (id: string): Rec => {
    const r = all().find((x) => x.id === id);
    if (!r || !inScope(r)) throw err(404, "product_order_not_found");
    return r;
  };

  function page<T>(items: T[], q: { cursor?: string; take?: number }): Page<T> {
    const size = Math.min(200, Math.max(1, q.take ?? 50));
    const start = q.cursor === undefined ? 0 : Number(q.cursor);
    if (!Number.isInteger(start) || start < 0) throw err(422, "invalid_cursor");
    const more = start + size < items.length;
    return { items: items.slice(start, start + size), hasMore: more, ...(more ? { nextCursor: String(start + size) } : {}) };
  }

  const parseDate = (v: string | undefined): number | null => {
    if (v === undefined || v === "") return null;
    const ms = Date.parse(v);
    if (Number.isNaN(ms)) throw err(422, "invalid_range");
    return ms;
  };

  return {
    async getProductOrders(q: ProductOrderQuery = {}): Promise<Page<ProductOrderSummary>> {
      const status: ProductOrderStatusFilter = q.status ?? "open";
      if (status !== "open" && status !== "all" && !STATUSES.includes(status)) throw err(422, "invalid_status");
      if (q.locationId !== undefined && q.locationId !== "" && !scopeLocations().some((l) => l.id === q.locationId)) throw err(404, "location_not_found");
      const to = parseDate(q.to) ?? now();
      const from = parseDate(q.from) ?? to - DEFAULT_RANGE_DAYS * DAY;
      if (from > to || to - from > MAX_RANGE_DAYS * DAY) throw err(422, "invalid_range");
      const rows = all()
        .filter(inScope)
        .filter((r) => !q.locationId || locationOf(r).id === q.locationId)
        .filter((r) => r.createdAt >= from && r.createdAt <= to)
        .filter((r) => q.number === undefined || r.number === q.number)
        .filter((r) => {
          const s = effective(r);
          return status === "all" ? true : status === "open" ? s === "pending_payment" || s === "paid" : s === status;
        })
        .sort((a, b) => b.createdAt - a.createdAt || b.id.localeCompare(a.id));
      return wait(page(rows.map(toSummary), q));
    },

    async getProductOrderCounts(): Promise<{ paid: number }> {
      return wait({ paid: Math.min(1000, all().filter((r) => inScope(r) && effective(r) === "paid").length) });
    },

    async getProductOrder(id: string): Promise<ProductOrder> {
      return wait(toOrder(byId(id)));
    },

    async fulfillProductOrder(id: string): Promise<ProductOrder> {
      requireManage();
      const r = byId(id);
      if (r.status === "fulfilled") return wait(toOrder(r));
      if (r.refundInProgress) throw err(409, "refund_in_progress");
      if (effective(r) !== "paid") throw err(409, "order_not_paid");
      r.status = "fulfilled";
      r.fulfilledAt = now();
      r.fulfilledBy = me;
      r.saleOperationId = `op-sale-${r.number}`;
      return wait(toOrder(r));
    },

    async cancelProductOrder(id: string, reason?: string): Promise<CancelProductOrderResult> {
      requireManage();
      const r = byId(id);
      if (reason !== undefined && (typeof reason !== "string" || reason.trim().length > CANCEL_REASON_MAX)) throw err(422, "invalid_reason");
      if (r.status === "fulfilled") throw err(409, "order_fulfilled");
      const eff = effective(r);
      if (eff === "cancelled" || eff === "expired" || eff === "refunded") throw err(409, "order_closed");
      if (r.paymentInProgress) throw err(409, "payment_in_progress");
      if (r.refundInProgress) throw err(409, "refund_in_progress");
      const note = reason?.trim() || null;
      if (r.status === "pending_payment") {
        r.status = "cancelled";
        r.closeReason = "staff";
        r.refundAmount = 0;
      } else {
        if (r.refundFailuresLeft > 0) {
          r.refundFailuresLeft -= 1;
          throw err(402, "refund_failed");
        }
        r.status = "refunded";
        r.closeReason = "staff";
        r.refundAmount = total(r);
        r.returnOperationId = `op-ret-${r.number}`;
        r.saleOperationId = r.saleOperationId ?? `op-sale-${r.number}`;
        if (r.payment) r.payment = { ...r.payment, status: "refunded" };
      }
      r.closedAt = now();
      r.cancelNote = note;
      r.cancelledBy = me;
      return wait({ order: toOrder(r), refundAmount: r.refundAmount ?? 0 });
    },

    async reconcileProductOrderPayment(id: string): Promise<ProductOrder> {
      requireManage();
      const r = byId(id);
      if (!r.paymentInProgress) throw err(409, "nothing_to_reconcile");
      r.paymentInProgress = false;
      r.status = "paid";
      r.paidAt = now();
      r.payment = { status: "paid", attempts: r.payment?.attempts ?? 1 };
      return wait(toOrder(r));
    },
  };
}
