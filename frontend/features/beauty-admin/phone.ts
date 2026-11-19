/**
 * Телефон клієнта (§25): маска для inbox і перевірка того, що в UI не потрапив повний номер.
 * Повний номер показується лише в картці клієнта й у «Дзвінках» (owner/admin).
 */

const DIGITS = /\d/g;

/** `+380501112233` -> `+••••••••••33` (знак `+` і останні 2 цифри, решта `•`). */
export function maskPhone(phone: string): string {
  const digits = (phone.match(DIGITS) ?? []).join("");
  if (digits.length === 0) return "";
  return `+${"•".repeat(Math.max(0, digits.length - 2))}${digits.slice(-2)}`;
}

/** Захист від витоку: якщо сервер віддав у масці більше 2 цифр, показуємо перемасковане значення. */
export function safeMask(masked: string | undefined | null): string | null {
  if (!masked) return null;
  const digits = (masked.match(DIGITS) ?? []).length;
  return digits > 2 ? maskPhone(masked) : masked;
}

/** Номер для `tel:` (лише `+` і цифри). */
export const telHref = (phone: string) => `tel:${phone.replace(/[^+\d]/g, "")}`;

export const PHONE_SOURCE_LABEL = {
  telegram_contact: "з Telegram-контакту",
  manual_chat: "введено в чаті",
  web_form: "з онлайн-запису",
  staff: "внесено адміністратором",
} as const;
