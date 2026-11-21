import type { PublicProductOrder } from "@/api/productOrders";
import { uk } from "@/i18n/uk";
import { formatPrice } from "@/lib/format";

type T = (key: string, opts?: Record<string, unknown>) => string;

const pad = (n: number) => String(n).padStart(2, "0");

/** HH:MM за годинником пристрою (без Intl). Лише підказка «до HH:MM» - статус визначає сервер (§33.9). */
export function formatClock(iso: string): string {
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? "" : `${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

/** Момент UTC (`...Z`) у часі пристрою: «20 листопада, 10:15». */
function formatLocalWhen(iso: string): string {
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? "" : `${d.getDate()} ${uk.months[d.getMonth()]}, ${formatClock(iso)}`;
}

/** «м:сс» до моменту `iso`; 0 після спливу. */
export function formatLeft(iso: string, nowMs: number): string {
  const ms = Math.max(0, new Date(iso).getTime() - nowMs);
  if (Number.isNaN(ms)) return "";
  const s = Math.floor(ms / 1000);
  return `${Math.floor(s / 60)}:${pad(s % 60)}`;
}

/** Текст стану замовлення за таблицею §33.9. */
export function statusText(o: PublicProductOrder, t: T, nowMs: number = Date.now()): string {
  switch (o.status) {
    case "pending_payment":
      return o.paymentInProgress
        ? t("shop.status.processing")
        : t("shop.status.pending", { time: o.reservationExpiresAt ? formatClock(o.reservationExpiresAt) : "" });
    case "paid":
      return t("shop.status.paid", { place: [o.location.name, o.location.address].filter(Boolean).join(", "), n: o.number });
    case "fulfilled":
      return t("shop.status.fulfilled", { date: o.fulfilledAt ? formatLocalWhen(o.fulfilledAt) : "" });
    case "cancelled":
      return t(o.closeReason === "staff" ? "shop.status.cancelledStaff" : "shop.status.cancelledClient");
    case "expired":
      return t("shop.status.expired");
    case "refunded":
      return t(o.closeReason === "stock_unavailable" ? "shop.status.refundedStock" : "shop.status.refundedStaff", {
        sum: formatPrice(o.refundAmount ?? o.total),
      });
  }
}

/** «Замовити знову» доступне для скасованих/прострочених і повернених через відсутність товару (§33.9). */
export const canReorder = (o: PublicProductOrder): boolean =>
  o.status === "expired" || o.status === "cancelled" || (o.status === "refunded" && o.closeReason === "stock_unavailable");
