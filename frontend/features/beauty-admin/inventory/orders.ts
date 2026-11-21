import { BeautyApiError } from "@/features/beauty-auth/errors";
import { alertBadgeText, formatMoney, formatQuantity, humanizeInventoryError } from "./logic";
import type { NamedRef, ProductRef, StockUnit } from "./types";

/**
 * Замовлення товарів клієнтами (ADR-026, контракт §33.13): типи адмін-API й чиста логіка екрана «Замовлення».
 * Цін закупівлі в DTO немає; суми - роздрібні, їх бачать усі з `inventory.view`.
 */

export type ProductOrderStatus = "pending_payment" | "paid" | "fulfilled" | "cancelled" | "expired" | "refunded";
/** `open` (за замовчуванням) = ефективні `pending_payment` + `paid`. */
export type ProductOrderStatusFilter = "open" | ProductOrderStatus | "all";
export type ProductOrderPaymentStatus = "pending" | "paid" | "failed" | "refunded";
export type ProductOrderSource = "web" | "ios" | "android";
export type ProductOrderCloseReason = "client" | "staff" | "stock_unavailable" | "expired" | "payment_failed";

export interface ProductOrderCustomer {
  name: string;
  /** Лише за `clients.contacts = full` (§30.5). */
  phone: string | null;
  phoneMasked: string | null;
}

export interface ProductOrderSummary {
  id: string;
  number: number;
  /** Ефективний статус. */
  status: ProductOrderStatus;
  location: NamedRef;
  customer: ProductOrderCustomer;
  lineCount: number;
  itemCount: number;
  total: number;
  currency: string;
  createdAt: string;
  reservationExpiresAt: string | null;
  paidAt: string | null;
  fulfilledAt: string | null;
  closedAt: string | null;
  /** Сервер може додати нові причини: невідомі показуємо нейтрально. */
  closeReason: string | null;
  paymentInProgress: boolean;
  refundInProgress: boolean;
}

export interface ProductOrderLine {
  product: ProductRef;
  /** Знімок назви й одиниці на момент замовлення. */
  name: string;
  unit: StockUnit;
  quantity: number;
  unitPrice: number;
  lineTotal: number;
}

export interface ProductOrder extends ProductOrderSummary {
  lines: ProductOrderLine[];
  refundAmount: number | null;
  cancelNote: string | null;
  fulfilledBy: NamedRef | null;
  cancelledBy: NamedRef | null;
  /** Лише за `clients.view = all` і якщо замовлення прив'язане до клієнта. */
  clientId: string | null;
  payment: { status: ProductOrderPaymentStatus; attempts: number } | null;
  saleOperationId: string | null;
  returnOperationId: string | null;
  source: ProductOrderSource;
}

export interface ProductOrderQuery {
  status?: ProductOrderStatusFilter;
  locationId?: string;
  from?: string;
  to?: string;
  number?: number;
  cursor?: string;
  take?: number;
}

export interface ProductOrderCounts {
  /** Оплачені до видачі в закладах scope (сервер обмежує 1000). */
  paid: number;
}

export interface CancelProductOrderResult {
  order: ProductOrder;
  refundAmount: number;
}

export const CANCEL_REASON_MAX = 300;

// ---------- підписи ----------

type Tone = "ok" | "wait" | "now" | "neutral" | "promo";

/** Статус завжди словами; колір - додатковий сигнал. */
export const ORDER_STATUS_VIEW: Record<ProductOrderStatus, { label: string; tone: Tone }> = {
  pending_payment: { label: "Очікує оплати", tone: "wait" },
  paid: { label: "Оплачено, до видачі", tone: "now" },
  fulfilled: { label: "Видано", tone: "ok" },
  cancelled: { label: "Скасовано", tone: "neutral" },
  expired: { label: "Прострочено", tone: "neutral" },
  refunded: { label: "Повернено кошти", tone: "promo" },
};
export const orderStatusLabel = (s: ProductOrderStatus) => ORDER_STATUS_VIEW[s].label;

/** Чіпи статусу; API приймає один статус, тому «скасовані» й «прострочені» окремо. */
export const ORDER_STATUS_FILTERS: { id: ProductOrderStatusFilter; label: string }[] = [
  { id: "open", label: "Оплачені до видачі" },
  { id: "all", label: "Усі" },
  { id: "fulfilled", label: "Видані" },
  { id: "cancelled", label: "Скасовані" },
  { id: "refunded", label: "Повернені" },
  { id: "expired", label: "Прострочені" },
];

export const ORDER_PAYMENT_LABEL: Record<ProductOrderPaymentStatus, string> = {
  pending: "Очікує оплати",
  paid: "Оплачено",
  failed: "Не пройшла",
  refunded: "Повернено",
};
export const ORDER_SOURCE_LABEL: Record<ProductOrderSource, string> = { web: "Сайт", ios: "Застосунок iOS", android: "Застосунок Android" };

const CLOSE_REASON_LABEL: Record<string, string> = {
  client: "Скасував клієнт",
  staff: "Скасував заклад",
  stock_unavailable: "Товар закінчився під час оплати",
  expired: "Минув час на оплату",
  payment_failed: "Оплата не пройшла",
};
/** `null` для невідомих причин (не показуємо технічні коди). */
export const closeReasonLabel = (r: string | null): string | null => (r ? (CLOSE_REASON_LABEL[r] ?? null) : null);

export const orderNumberLabel = (n: number) => `№${n}`;

/** Телефон для відображення: повний (`clients.contacts = full`) або маска від сервера; без жодного - «—». */
export function customerPhone(c: Pick<ProductOrderCustomer, "phone" | "phoneMasked">): { text: string; masked: boolean } {
  if (c.phone) return { text: c.phone, masked: false };
  if (c.phoneMasked) return { text: c.phoneMasked, masked: true };
  return { text: "—", masked: true };
}

/** «Приховано» для скрінрідера: маска з крапок не озвучується осмислено. */
export const phoneAriaLabel = (c: Pick<ProductOrderCustomer, "phone" | "phoneMasked">) => {
  const p = customerPhone(c);
  return p.masked && p.text !== "—" ? `Телефон приховано, закінчується на ${p.text.replace(/\D/g, "").slice(-2)}` : undefined;
};

const plural = (n: number, one: string, few: string, many: string) => {
  const m10 = n % 10;
  const m100 = n % 100;
  if (m10 === 1 && m100 !== 11) return one;
  if (m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14)) return few;
  return many;
};
/** «2 позиції, 5 шт». */
export const orderSizeLabel = (o: Pick<ProductOrderSummary, "lineCount" | "itemCount">) =>
  `${o.lineCount} ${plural(o.lineCount, "позиція", "позиції", "позицій")}, ${o.itemCount} шт`;

export const orderTotalLabel = (o: Pick<ProductOrderSummary, "total" | "currency">) => (o.currency === "UAH" ? formatMoney(o.total) : `${formatMoney(o.total).replace(" ₴", "")} ${o.currency}`);
export const orderLineQuantity = (l: Pick<ProductOrderLine, "quantity" | "unit">) => formatQuantity(l.quantity, l.unit);

/** Індикатори поряд зі статусом (словами). */
export function orderIndicators(o: Pick<ProductOrderSummary, "paymentInProgress" | "refundInProgress">): string[] {
  return [...(o.paymentInProgress ? ["Оплата в процесі"] : []), ...(o.refundInProgress ? ["Повернення в процесі"] : [])];
}

export const ordersBadgeText = alertBadgeText;

// ---------- пошук за номером ----------

/** «№1042», «#1042», «1042» -> 1042; порожньо -> `null` (без фільтра); некоректне -> `"invalid"`. */
export function parseOrderNumber(input: string): number | null | "invalid" {
  const t = input.trim().replace(/^[№#]\s*/, "");
  if (t === "") return null;
  if (!/^\d{1,9}$/.test(t)) return "invalid";
  const n = Number(t);
  return n > 0 ? n : "invalid";
}

// ---------- дії ----------

export interface OrderActions {
  fulfill: boolean;
  cancel: boolean;
  reconcile: boolean;
}

/**
 * Які дії доступні. `canManage` - право `inventory.manage` у закладі замовлення (`/scope`). Видача - лише з `paid`;
 * скасування - з `pending_payment|paid`, але не поки оплата чи повернення «в процесі» (там потрібна «Перевірити оплату»).
 */
export function orderActions(o: Pick<ProductOrderSummary, "status" | "paymentInProgress" | "refundInProgress">, canManage: boolean): OrderActions {
  if (!canManage) return { fulfill: false, cancel: false, reconcile: false };
  const busy = o.paymentInProgress || o.refundInProgress;
  return {
    fulfill: o.status === "paid" && !o.refundInProgress,
    cancel: (o.status === "paid" || o.status === "pending_payment") && !busy,
    reconcile: o.paymentInProgress,
  };
}

/** Пояснення в діалозі скасування. */
export function cancelExplanation(o: Pick<ProductOrderSummary, "status" | "total" | "currency">): string {
  return o.status === "paid"
    ? `Кошти ${orderTotalLabel(o)} буде повернено клієнту повністю, товар повернеться на склад.`
    : "Замовлення ще не оплачено: резерв товару буде знято, гроші не списуються.";
}
export const cancelButtonLabel = (o: Pick<ProductOrderSummary, "status" | "total" | "currency">) =>
  o.status === "paid" ? `Скасувати й повернути ${orderTotalLabel(o)}` : "Скасувати замовлення";

export function validateCancelReason(reason: string): string | null {
  return reason.trim().length > CANCEL_REASON_MAX ? `Причина: до ${CANCEL_REASON_MAX} символів.` : null;
}

// ---------- помилки ----------

const ORDER_ERRORS: Record<string, string> = {
  invalid_reason: `Причина: до ${CANCEL_REASON_MAX} символів.`,
  invalid_status: "Технічна помилка фільтра. Оновіть сторінку.",
  location_not_found: "Заклад не знайдено.",
};
/** Тексти помилок замовлень: коди `product_order_not_found|order_*|payment_in_progress|...` - у `errors.ts`; тут - перекриття спільних кодів. */
export function humanizeOrderError(e: unknown, fallback?: string): string {
  if (e instanceof BeautyApiError && ORDER_ERRORS[e.code]) return ORDER_ERRORS[e.code]!;
  return humanizeInventoryError(e, fallback);
}

// ---------- журнал руху (§33.14) ----------

export const SALE_MOVEMENT_LABEL = { sale: "Продаж", sale_return: "Повернення продажу" } as const;
/** «Продаж (замовлення №{n})» або «Продаж»; номера в журналі немає, тому з `productOrderId` - «за замовленням». */
export const saleMovementLabel = (type: "sale" | "sale_return", productOrderId: string | null | undefined) =>
  productOrderId ? `${SALE_MOVEMENT_LABEL[type]} (за замовленням клієнта)` : SALE_MOVEMENT_LABEL[type];
