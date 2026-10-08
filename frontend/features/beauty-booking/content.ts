import type { CancellationTerms, PaymentMethod, ReminderOption } from "./types";

export const reminderOptions: { id: ReminderOption; label: string }[] = [
  { id: "none", label: "Не нагадувати" },
  { id: "1h", label: "За 1 годину" },
  { id: "2h", label: "За 2 години" },
];

export const paymentOptions: { id: PaymentMethod; label: string; note: string }[] = [
  { id: "card", label: "Картою онлайн", note: "Оплата зараз, безпечно" },
  { id: "cash", label: "Готівкою", note: "Оплата в закладі після послуги" },
];

/** «година / години / годин» для цілого числа (укр.). */
export function hoursLabel(n: number): string {
  const m10 = n % 10;
  const m100 = n % 100;
  const word = m10 === 1 && m100 !== 11 ? "годину" : m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14) ? "години" : "годин";
  return `${n} ${word}`;
}

/**
 * Умови скасування для показу клієнту. Беруться ЛИШЕ з полів `cancellation` API (§11) - жодних зашитих відсотків.
 * Без умов (старий backend / помилка) - нейтральний текст.
 */
export function describeCancellation(t: CancellationTerms | undefined | null): string[] {
  if (!t) return ["Умови скасування уточнюйте в закладі."];
  const lines: string[] = [];
  lines.push(
    t.windowHours > 0
      ? `За ${hoursLabel(t.windowHours)} і менше до візиту повертається ${t.refundPercentInWindow}%.`
      : `Після початку візиту повертається ${t.refundPercentInWindow}%.`,
  );
  if (t.windowHours > 0) lines.push(`Раніше за цей строк повертається ${t.refundPercentOutside}%.`);
  if (t.deductFee && t.feePercent > 0) lines.push(`З суми повернення утримується комісія ${t.feePercent}%.`);
  return lines;
}

/** Чи потрапляє скасування «зараз» у вікно (межа включна, як на backend). */
export const isInCancellationWindow = (t: CancellationTerms, startsAtIso: string, now = Date.now()) =>
  Date.parse(startsAtIso) - now <= t.windowHours * 3_600_000;

/** Орієнтовний відсоток повернення, якщо скасувати зараз (з урахуванням комісії). Остаточну суму рахує сервер. */
export function estimateRefundPercent(t: CancellationTerms, startsAtIso: string, now = Date.now()): number {
  const base = isInCancellationWindow(t, startsAtIso, now) ? t.refundPercentInWindow : t.refundPercentOutside;
  const fee = t.deductFee ? t.feePercent : 0;
  return Math.floor((base * (100 - fee)) / 100);
}

export const paymentPolicy: Record<PaymentMethod, string> = {
  card: "Оплата карткою: кошти повертаємо на картку за умовами вище.",
  cash: "Оплата готівкою в закладі після послуги, передоплати немає.",
};

export const stepTitles = {
  1: "Оберіть заклад",
  2: "Оберіть майстра",
  3: "Послуга та час",
  4: "Оформлення",
  5: "Готово",
} as const;

export const formatPrice = (n: number) => `${n} ₴`;

export function formatDuration(min: number): string {
  const h = Math.floor(min / 60);
  const m = min % 60;
  return [h ? `${h} г` : "", m ? `${m} хв` : ""].filter(Boolean).join(" ");
}

export function formatDateTime(iso: string): string {
  return new Date(iso).toLocaleString("uk-UA", {
    day: "numeric",
    month: "long",
    hour: "2-digit",
    minute: "2-digit",
  });
}

export function reminderSummary(r: ReminderOption): string {
  if (r === "none") return "Нагадування вимкнено.";
  const label = reminderOptions.find((o) => o.id === r)?.label.toLowerCase();
  return `Нагадаємо про запис ${label} до візиту.`;
}
