import { BookingApiError } from "@beauty/booking-core";
import { errorExtras, type ProductOrdersApi, type PublicProductOrder } from "@/api/productOrders";
import { isRetryable } from "@/features/booking/submit";

/** Опитування P3 після `payment_in_progress` / мережевої помилки: 5, 10, 20, 40 с, далі ще 45 с (≈ 2 хв) - тоді один повтор P4 (§33.10 D). */
export const POLL_DELAYS_MS = [5_000, 10_000, 20_000, 40_000, 45_000];

export interface PayOutcome {
  /** Актуальний стан замовлення (null, якщо дізнатись не вдалося). */
  order: PublicProductOrder | null;
  /** `code` помилки оплати (`payment_failed`, `order_expired`, ...) або null, якщо запит оплати/перевірки відпрацював без помилки. */
  errorCode: string | null;
}

/**
 * Оплата (P4) без наосліпних повторів. Якщо сервер повернув `paymentUrl` (рахунок monobank, ADR-028) - відкриваємо сторінку банку
 * й опитуємо P3, поки триває оплата.: після мережевої помилки/тайм-ауту/5xx спершу P3 (контракт §33.7: списання не більше одного,
 * але клієнт не знає, чи дійшов запит). Якщо оплата «в процесі» - опитування, через ~2 хв рівно один повтор P4.
 */
export async function payWithReconcile(
  deps: {
    api: Pick<ProductOrdersApi, "pay" | "getOrder">;
    sleep: (ms: number) => Promise<void>;
    delays?: number[];
    /** ADR-028: відкрити сторінку оплати monobank (системний браузер). Без нього - лише опитування. */
    openPaymentPage?: (url: string) => Promise<void>;
  },
  tenantSlug: string,
  token: string,
): Promise<PayOutcome> {
  const delays = deps.delays ?? POLL_DELAYS_MS;

  const attemptPay = async (openPage: boolean): Promise<PayOutcome | "poll" | "check"> => {
    try {
      const r = await deps.api.pay(tenantSlug, token);
      if (openPage && r.paymentUrl && r.order.status === "pending_payment" && r.order.paymentInProgress) {
        // клієнт платить на сторінці банку; результат прийде вебхуком на сервер -> опитуємо P3
        try {
          await deps.openPaymentPage?.(r.paymentUrl);
        } catch {
          return { order: r.order, errorCode: "payment_page_failed" };
        }
        return "poll";
      }
      return { order: r.order, errorCode: null };
    } catch (e) {
      if (!(e instanceof BookingApiError) || isRetryable(e)) return "check";
      if (e.code === "payment_in_progress") return "poll";
      return { order: errorExtras(e).order ?? null, errorCode: e.code || `http_${e.status}` };
    }
  };

  const check = async (): Promise<PayOutcome> => {
    try {
      return { order: await deps.api.getOrder(tenantSlug, token), errorCode: null };
    } catch (e) {
      return { order: null, errorCode: e instanceof BookingApiError ? e.code || `http_${e.status}` : "api_unreachable" };
    }
  };

  const first = await attemptPay(true);
  if (typeof first === "object") return first;

  for (let i = 0; ; i++) {
    const r = await check();
    const o = r.order;
    if (!o || !(o.status === "pending_payment" && o.paymentInProgress)) return r;
    if (i >= delays.length) break;
    await deps.sleep(delays[i]);
  }

  const last = await attemptPay(false); // повторний P4 не відкриває сторінку банку вдруге без дії клієнта
  return typeof last === "object" ? last : check();
}
