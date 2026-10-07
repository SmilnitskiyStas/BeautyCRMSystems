/** Помилка API: `{ code, message }` (контракт §9/§10) + HTTP-статус. */
export class BeautyApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly code: string,
    message: string,
    public readonly retryAfterSeconds?: number,
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

/** Розбір тіла помилки `{code,message}`; безпечний до не-JSON відповіді. */
export async function readApiError(res: Response): Promise<BeautyApiError> {
  let code = "";
  let message = res.statusText;
  try {
    const body = (await res.json()) as { code?: unknown; message?: unknown };
    if (typeof body.code === "string") code = body.code;
    if (typeof body.message === "string") message = body.message;
  } catch {
    /* не JSON */
  }
  const retry = Number(res.headers.get("Retry-After"));
  return new BeautyApiError(res.status, code, message, Number.isFinite(retry) && retry > 0 ? retry : undefined);
}
