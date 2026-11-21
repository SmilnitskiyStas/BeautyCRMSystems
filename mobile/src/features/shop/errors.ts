import { BookingApiError, humanizeBookingError } from "@beauty/booking-core";
import { uk } from "@/i18n/uk";

const TEXTS: Record<string, string> = uk.shop.errors;

/** Текст помилки замовлення за `code` (§33.11); невідомий код -> загальні тексти core. 429 завжди з `Retry-After` (core). */
export function shopErrorText(e: unknown): string {
  if (e instanceof BookingApiError && e.status !== 429 && TEXTS[e.code]) return TEXTS[e.code];
  return humanizeBookingError(e);
}

/** Той самий таблиці текст за готовим кодом (для `PayOutcome.errorCode`). */
export const shopCodeText = (code: string): string => TEXTS[code] ?? uk.shop.errors.generic;

/** 404 product_orders_disabled: розділ ховаємо (§33.2). */
export const isShopDisabled = (e: unknown): boolean => e instanceof BookingApiError && e.status === 404 && e.code === "product_orders_disabled";
