import { apiJson } from "@/features/beauty-auth/session";
import type { BeautyAdminApi } from "./client";
import { toProductRef } from "./http-inventory";
import type {
  ProductOrder,
  ProductOrderLine,
  ProductOrderPaymentStatus,
  ProductOrderSource,
  ProductOrderStatus,
  ProductOrderSummary,
} from "../inventory/orders";
import type { NamedRef, Page, StockUnit } from "../inventory/types";

/**
 * HTTP-клієнт адмін-API замовлень товарів (§33.13, ADR-026): `/api/beauty/inventory/product-orders`. Явна вибірка полів;
 * `phone`/`clientId` копіюються лише коли сервер їх віддав (права `clients.contacts`/`clients.view`), інакше `null`.
 * Усі відповіді `no-store`; тіла дій - `{}` (fulfill, reconcile) і `{reason?}` (cancel).
 */

const B = "/api/beauty/inventory/product-orders";
type Raw = Record<string, unknown>;
const obj = (v: unknown): Raw => (v && typeof v === "object" ? (v as Raw) : {});
const str = (v: unknown): string | null => (typeof v === "string" && v !== "" ? v : null);
const num = (v: unknown): number | null => (typeof v === "number" && Number.isFinite(v) ? v : null);
const num0 = (v: unknown): number => num(v) ?? 0;
const list = (v: unknown): unknown[] => (Array.isArray(v) ? v : []);
const oneOf = <T extends string>(v: unknown, all: readonly T[], fallback: T): T => (typeof v === "string" && (all as readonly string[]).includes(v) ? (v as T) : fallback);
const ref = (v: unknown): NamedRef | null => {
  const o = obj(v);
  const id = str(o.id);
  return id ? { id, name: str(o.name) ?? "" } : null;
};

const STATUSES = ["pending_payment", "paid", "fulfilled", "cancelled", "expired", "refunded"] as const satisfies readonly ProductOrderStatus[];
const PAYMENT_STATUSES = ["pending", "paid", "failed", "refunded"] as const satisfies readonly ProductOrderPaymentStatus[];
const SOURCES = ["web", "ios", "android"] as const satisfies readonly ProductOrderSource[];
const UNITS = ["pcs", "ml", "g", "l", "kg", "pack"] as const satisfies readonly StockUnit[];

export function toProductOrderSummary(v: unknown): ProductOrderSummary {
  const o = obj(v);
  const c = obj(o.customer);
  return {
    id: str(o.id) ?? "",
    number: num0(o.number),
    status: oneOf<ProductOrderStatus>(o.status, STATUSES, "pending_payment"),
    location: ref(o.location) ?? { id: "", name: "" },
    customer: { name: str(c.name) ?? "", phone: str(c.phone), phoneMasked: str(c.phoneMasked) },
    lineCount: num0(o.lineCount),
    itemCount: num0(o.itemCount),
    total: num0(o.total),
    currency: str(o.currency) ?? "UAH",
    createdAt: str(o.createdAt) ?? "",
    reservationExpiresAt: str(o.reservationExpiresAt),
    paidAt: str(o.paidAt),
    fulfilledAt: str(o.fulfilledAt),
    closedAt: str(o.closedAt),
    closeReason: str(o.closeReason),
    paymentInProgress: o.paymentInProgress === true,
    refundInProgress: o.refundInProgress === true,
  };
}

function toLine(v: unknown): ProductOrderLine {
  const o = obj(v);
  return {
    product: toProductRef(o.product),
    name: str(o.name) ?? "",
    unit: oneOf<StockUnit>(o.unit, UNITS, "pcs"),
    quantity: num0(o.quantity),
    unitPrice: num0(o.unitPrice),
    lineTotal: num0(o.lineTotal),
  };
}

export function toProductOrder(v: unknown): ProductOrder {
  const o = obj(v);
  const p = obj(o.payment);
  return {
    ...toProductOrderSummary(v),
    lines: list(o.lines).map(toLine),
    refundAmount: num(o.refundAmount),
    cancelNote: str(o.cancelNote),
    fulfilledBy: ref(o.fulfilledBy),
    cancelledBy: ref(o.cancelledBy),
    clientId: str(o.clientId),
    payment: o.payment ? { status: oneOf<ProductOrderPaymentStatus>(p.status, PAYMENT_STATUSES, "pending"), attempts: num0(p.attempts) } : null,
    saleOperationId: str(o.saleOperationId),
    returnOperationId: str(o.returnOperationId),
    source: oneOf<ProductOrderSource>(o.source, SOURCES, "web"),
  };
}

function toPage(raw: unknown): Page<ProductOrderSummary> {
  const o = obj(raw);
  const next = str(o.nextCursor);
  return { items: list(o.items).map(toProductOrderSummary), hasMore: o.hasMore === true, ...(next ? { nextCursor: next } : {}) };
}

const enc = encodeURIComponent;
function qs(p: Record<string, string | number | undefined>): string {
  const u = new URLSearchParams();
  for (const [k, v] of Object.entries(p)) if (v !== undefined && v !== "") u.set(k, String(v));
  const s = u.toString();
  return s ? `?${s}` : "";
}
const take = (n?: number) => (n === undefined ? undefined : Math.min(200, Math.max(1, n)));
const get = <T>(path: string) => apiJson<T>(`${B}${path}`, { cache: "no-store" });
const post = <T>(path: string, body: unknown) => apiJson<T>(`${B}${path}`, { method: "POST", body: JSON.stringify(body), cache: "no-store" });

type ProductOrdersApi = Pick<
  BeautyAdminApi,
  "getProductOrders" | "getProductOrderCounts" | "getProductOrder" | "fulfillProductOrder" | "cancelProductOrder" | "reconcileProductOrderPayment"
>;

export const httpProductOrdersApi: ProductOrdersApi = {
  getProductOrders: async (q = {}) => toPage(await get(`${qs({ status: q.status, locationId: q.locationId, from: q.from, to: q.to, number: q.number, cursor: q.cursor, take: take(q.take) })}`)),
  getProductOrderCounts: async () => ({ paid: Math.max(0, num0(obj(await get("/counts")).paid)) }),
  getProductOrder: async (id) => toProductOrder(await get(`/${enc(id)}`)),
  fulfillProductOrder: async (id) => toProductOrder(await post(`/${enc(id)}/fulfill`, {})),
  cancelProductOrder: async (id, reason) => {
    const trimmed = reason?.trim();
    const o = obj(await post(`/${enc(id)}/cancel`, trimmed ? { reason: trimmed } : {}));
    return { order: toProductOrder(o.order), refundAmount: num0(o.refundAmount) };
  },
  reconcileProductOrderPayment: async (id) => toProductOrder(await post(`/${enc(id)}/reconcile-payment`, {})),
};
