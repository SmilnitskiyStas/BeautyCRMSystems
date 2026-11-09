import type { CancellationTerms } from "@beauty/booking-core";
import { uk } from "@/i18n/uk";

/**
 * Форматування БЕЗ Intl (ризик Hermes Intl/uk-UA): `startsAt` - ISO зі зсувом закладу
 * (`2026-11-12T14:30:00+02:00`), тож дату/час беремо з рядка напряму - це вже локальний час закладу.
 */
export function formatWhen(iso: string): string {
  const m = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})/.exec(iso);
  if (!m) return iso;
  const month = uk.months[Number(m[2]) - 1] ?? "";
  return `${Number(m[3])} ${month}, ${m[4]}:${m[5]}`;
}

export const formatPrice = (n: number): string => `${Number.isInteger(n) ? n : n.toFixed(2)} ${uk.common.currency}`;

type T = (key: string, opts?: Record<string, unknown>) => string;

/** Умови скасування з полів API (§11) - без зашитих відсотків: усе береться з `CancellationTerms`. */
export function cancellationLines(c: CancellationTerms | undefined, t: T): string[] {
  if (!c) return [t("cancellation.none")];
  const lines: string[] = [];
  if (c.refundPercentOutside >= 100 && !c.deductFee) {
    lines.push(t("cancellation.freeOutside", { h: c.windowHours }));
  } else {
    lines.push(t("cancellation.refundOutside", { p: c.refundPercentOutside }));
  }
  lines.push(t("cancellation.refundInWindow", { h: c.windowHours, p: c.refundPercentInWindow }));
  if (c.deductFee && c.feePercent > 0) lines.push(t("cancellation.fee", { p: c.feePercent }));
  return lines;
}
