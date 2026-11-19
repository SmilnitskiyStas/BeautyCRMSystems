import { clientSchema, reminderEmailError, type ClientInput, type ReminderOption } from "@beauty/booking-core";

export const REMINDER_OPTIONS: ReminderOption[] = ["none", "1h", "2h", "24h"];

export interface CheckoutFormValues {
  name: string;
  phone: string;
  email: string;
  reminder: ReminderOption;
}

export type CheckoutFieldErrors = { name?: string; phone?: string; email?: string };

/**
 * Валідація полів кроку підтвердження. Пошта необов'язкова; якщо обрано нагадування, пошта обов'язкова
 * (помилка біля поля пошти). `client` - готове тіло для API (email у нижньому регістрі або відсутній).
 */
export function validateCheckoutForm(v: CheckoutFormValues): { errors: CheckoutFieldErrors; client: ClientInput | null } {
  const errors: CheckoutFieldErrors = {};
  const parsed = clientSchema.safeParse({ name: v.name, phone: v.phone, email: v.email });
  if (!parsed.success) {
    for (const i of parsed.error.issues) {
      const k = i.path[0] as keyof CheckoutFieldErrors;
      errors[k] ??= i.message;
    }
  }
  if (!errors.email) {
    const need = reminderEmailError(v.reminder, v.email);
    if (need) errors.email = need;
  }
  if (!parsed.success || Object.keys(errors).length > 0) return { errors, client: null };
  return { errors, client: parsed.data };
}
