/** Запис, який блокує зміну (`conflicts[]` у 409, §17): без клієнтських даних. */
export interface ApiConflict {
  appointmentId: string;
  /** ISO зі зсувом/Z (backend) або локальний `YYYY-MM-DDTHH:mm` (mock). */
  startsAt: string;
  serviceName: string;
  specialistName?: string;
}

/** Помилка API: `{ code, message }` (контракт §9/§10) + HTTP-статус; для 409 §17 - ще `conflicts[]`. */
export class BeautyApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly code: string,
    message: string,
    public readonly retryAfterSeconds?: number,
    public readonly conflicts?: ApiConflict[],
  ) {
    super(message);
    this.name = "BeautyApiError";
  }
}

/** Людські тексти за кодом помилки. Англомовний `message` сервера користувачу не показуємо. */
const BY_CODE: Record<string, string> = {
  invalid_credentials: "Невірна пошта, пароль або назва бізнесу.",
  account_locked: "Забагато невдалих спроб. Обліковий запис тимчасово заблоковано, спробуйте через 15 хвилин.",
  rate_limited: "Забагато запитів. Зачекайте хвилину і спробуйте ще раз.",
  invalid_token: "Сесія завершилась. Увійдіть знову.",
  tenant_required: "Сесія завершилась. Увійдіть знову.",
  no_session: "Сесія завершилась. Увійдіть знову.",
  forbidden_role: "У вас немає прав на цю дію.",
  module_disabled: "Цей розділ не підключено для вашого бізнесу.",
  invite_invalid: "Запрошення недійсне або термін його дії минув. Попросіть надіслати нове.",
  invite_closed: "Це запрошення вже використано або скасовано.",
  user_exists: "Користувач із такою поштою вже існує.",
  weak_password: "Пароль надто простий. Використайте щонайменше 12 символів, літери й цифри.",
  slot_unavailable: "Цей час уже зайнято. Оберіть інший.",
  slot_in_past: "Не можна перенести запис у минуле.",
  outside_working_hours: "Цей час поза робочим графіком майстра.",
  already_cancelled: "Запис уже скасовано.",
  appointment_closed: "Запис завершено, змінити його не можна.",
  appointment_not_found: "Запис не знайдено.",
  nothing_to_change: "Нічого змінювати: час не змінився.",
  payment_failed: "Не вдалося провести платіж. Спробуйте ще раз.",
  refund_failed: "Не вдалося повернути кошти. Спробуйте ще раз або зверніться до підтримки.",
  invalid_window_hours: "Вікно скасування має бути від 0 до 720 годин.",
  invalid_refund_percent: "Відсоток повернення має бути від 0 до 100.",
  invalid_fee_percent: "Відсоток комісії має бути від 0 до 100.",
  settings_incomplete: "Заповніть усі поля налаштувань.",
  absence_overlap: "У цей період уже є відсутність цього працівника. Змініть дати або скасуйте наявну.",
  specialist_unavailable: "Працівник недоступний у цей час (відсутність або послугу не призначено).",
  invalid_working_hours: "Некоректний графік: перевірте час початку й кінця, інтервали не повинні перетинатися.",
  has_future_appointments: "Є майбутні записи (очікують або підтверджені). Спершу перенесіть або скасуйте їх.",
  timezone_locked: "Змінити часову зону не можна, поки в закладі є майбутні записи. Спершу перенесіть або скасуйте їх.",
  location_name_taken: "Заклад із такою назвою вже існує. Оберіть іншу назву.",
  invalid_timezone: "Невідома часова зона. Оберіть зону зі списку, наприклад Europe/Kyiv.",
  location_not_found: "Заклад не знайдено.",
  closure_overlap: "На ці дати вже є закриття закладу. Змініть діапазон або видаліть наявне закриття.",
  location_closed: "Заклад у цей день не працює (вихідний або закриття). Оберіть інший день.",
  has_appointments_on_closed_days:
    "У вихідні дні є активні записи. Перегляньте їх, перенесіть або підтвердіть збереження вихідного.",
  invalid_dates: "Перевірте дати: кінець не раніше початку, період до 366 днів, не давніше року й не далі ніж на 2 роки вперед.",
  invalid_range: "Некоректний діапазон дат (до 366 днів).",
  invalid_reason: "Причина занадто довга: закриття - до 200 символів, скасування запису - до 300.",
  invalid_closed_weekdays: "Некоректний перелік вихідних днів тижня.",
  api_unreachable: "Сервер недоступний. Перевірте з'єднання й спробуйте ще раз.",
  not_supported: "Ця можливість ще не підключена до сервера.",
};

const BY_STATUS: Record<number, string> = {
  401: "Сесія завершилась. Увійдіть знову.",
  403: "У вас немає прав на цю дію.",
  404: "Не знайдено.",
  409: "Конфлікт даних. Оновіть сторінку й спробуйте ще раз.",
  422: "Перевірте введені дані.",
  423: "Обліковий запис тимчасово заблоковано. Спробуйте пізніше.",
  429: "Забагато запитів. Зачекайте хвилину і спробуйте ще раз.",
};

export function humanizeError(e: unknown, fallback = "Щось пішло не так. Спробуйте ще раз."): string {
  if (e instanceof BeautyApiError) {
    return BY_CODE[e.code] ?? BY_STATUS[e.status] ?? (e.status >= 500 ? "Помилка сервера. Спробуйте пізніше." : fallback);
  }
  if (e instanceof TypeError) return BY_CODE.api_unreachable;
  return fallback;
}

function parseConflict(raw: unknown): ApiConflict[] {
  if (!raw || typeof raw !== "object") return [];
  const c = raw as Record<string, unknown>;
  if (typeof c.appointmentId !== "string" || typeof c.startsAt !== "string") return [];
  return [
    {
      appointmentId: c.appointmentId,
      startsAt: c.startsAt,
      serviceName: typeof c.serviceName === "string" ? c.serviceName : "",
      ...(typeof c.specialistName === "string" && c.specialistName ? { specialistName: c.specialistName } : {}),
    },
  ];
}

/** Розбір тіла помилки `{code,message}`; безпечний до не-JSON відповіді. */
export async function readApiError(res: Response): Promise<BeautyApiError> {
  let code = "";
  let message = res.statusText;
  let conflicts: ApiConflict[] | undefined;
  try {
    const body = (await res.json()) as { code?: unknown; message?: unknown; conflicts?: unknown };
    if (typeof body.code === "string") code = body.code;
    if (typeof body.message === "string") message = body.message;
    if (Array.isArray(body.conflicts)) conflicts = body.conflicts.flatMap(parseConflict);
  } catch {
    /* не JSON */
  }
  const retry = Number(res.headers.get("Retry-After"));
  return new BeautyApiError(res.status, code, message, Number.isFinite(retry) && retry > 0 ? retry : undefined, conflicts);
}
