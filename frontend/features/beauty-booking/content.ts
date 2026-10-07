import type { PaymentMethod, ReminderOption } from "./types";

export const reminderOptions: { id: ReminderOption; label: string }[] = [
  { id: "none", label: "Не нагадувати" },
  { id: "1h", label: "За 1 годину" },
  { id: "2h", label: "За 2 години" },
];

export const paymentOptions: { id: PaymentMethod; label: string; note: string }[] = [
  { id: "card", label: "Картою онлайн", note: "Оплата зараз, безпечно" },
  { id: "cash", label: "Готівкою", note: "Оплата в закладі після послуги" },
];

// Рішення A2: скасування за 12 годин і менше до візиту → повернення 50%.
export const cancellationPolicy = "За 12 годин і менше до візиту повертається 50%.";

export const paymentPolicy: Record<PaymentMethod, string> = {
  card: "Оплата карткою: 50% суми повернемо на картку.",
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
