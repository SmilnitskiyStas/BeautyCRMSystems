import { BookingApiError } from "./types";

/** Людські тексти за кодом помилки публічного API. Англомовний `message` сервера користувачу не показуємо. */
const BY_CODE: Record<string, string> = {
  slot_unavailable: "Цей час щойно зайняли. Оберіть інший.",
  specialist_unavailable: "Майстер недоступний у цей час. Оберіть інший час або майстра.",
  slot_in_past: "Цей час уже минув. Оберіть інший.",
  outside_working_hours: "Цей час поза графіком майстра. Оберіть інший.",
  invalid_start: "Оберіть час із запропонованих.",
  invalid_date: "Оберіть дату в межах найближчих днів.",
  invalid_name: "Вкажіть імʼя (від 2 до 100 символів).",
  invalid_phone: "Перевірте номер телефону, наприклад +380501112233.",
  invalid_reminder: "Оберіть варіант нагадування зі списку.",
  invalid_payment_method: "Оберіть спосіб оплати зі списку.",
  booking_limit_reached: "На цей номер уже забагато записів. Спробуйте пізніше або зверніться до закладу.",
  captcha_failed: "Не вдалося підтвердити, що ви не робот. Спробуйте ще раз.",
  idempotency_key_reused: "Дані змінились після спроби запису. Підтвердіть запис ще раз.",
  idempotency_conflict: "Запис уже створюється. Зачекайте кілька секунд і перевірте ще раз.",
  invalid_request: "Не вдалося обробити запит. Перевірте дані й спробуйте ще раз.",
  payment_failed: "Не вдалося провести платіж. Спробуйте ще раз або оберіть оплату в закладі.",
  refund_failed: "Не вдалося повернути кошти. Зверніться до закладу.",
  already_cancelled: "Запис уже скасовано.",
  appointment_closed: "Цей запис уже завершено, скасувати його не можна.",
  not_found: "Запис або заклад не знайдено. Перевірте посилання.",
  api_unreachable: "Сервер недоступний. Перевірте з'єднання й спробуйте ще раз.",
};

const BY_STATUS: Record<number, string> = {
  402: "Не вдалося провести платіж. Спробуйте ще раз.",
  404: "Не знайдено. Перевірте посилання.",
  409: "Цей час уже зайнято або запис змінився. Оновіть сторінку й спробуйте ще раз.",
  422: "Перевірте введені дані.",
};

/** 429: просимо зачекати; якщо сервер дав `Retry-After`, показуємо орієнтовний час. */
function rateLimited(e: BookingApiError): string {
  const s = e.retryAfterSeconds;
  if (!s) return "Забагато спроб. Зачекайте хвилину й спробуйте ще раз.";
  return s <= 90 ? `Забагато спроб. Спробуйте ще раз приблизно через ${s} с.` : "Забагато спроб. Спробуйте пізніше.";
}

export function humanizeBookingError(e: unknown, fallback = "Щось пішло не так. Спробуйте ще раз."): string {
  if (e instanceof BookingApiError) {
    if (e.status === 429 || e.code === "rate_limited") return rateLimited(e);
    return BY_CODE[e.code] ?? BY_STATUS[e.status] ?? (e.status >= 500 ? "Помилка сервера. Спробуйте пізніше." : fallback);
  }
  if (e instanceof TypeError) return BY_CODE.api_unreachable;
  return fallback;
}

/** Слот зайнятий/недоступний: UI скидає вибір часу й оновлює слоти. */
export const isSlotLost = (e: unknown): boolean =>
  e instanceof BookingApiError &&
  e.status === 409 &&
  ["slot_unavailable", "specialist_unavailable"].includes(e.code);
