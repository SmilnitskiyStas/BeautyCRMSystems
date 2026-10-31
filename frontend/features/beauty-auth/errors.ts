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
    /** 409 key_affects_other_roles (§20.5): сценарії, яких торкнеться зміна ключа. */
    public readonly affectedRoles?: string[],
    /** 409 task_already_closed (§22.6.3): поточний стан задачі дзвінка. Лише для UI-оновлення рядка. */
    public readonly task?: unknown,
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
  has_appointments_outside_hours:
    "Є активні записи поза новими годинами роботи. Перегляньте їх, перенесіть або підтвердіть збереження годин.",
  outside_location_hours: "Цей час поза годинами роботи закладу. Оберіть інший час.",
  invalid_dates: "Перевірте дати: кінець не раніше початку, період до 366 днів, не давніше року й не далі ніж на 2 роки вперед.",
  invalid_range: "Некоректний діапазон дат (до 366 днів).",
  invalid_reason: "Причина занадто довга: закриття - до 200 символів, скасування запису - до 300.",
  invalid_closed_weekdays: "Некоректний перелік вихідних днів тижня.",
  api_unreachable: "Сервер недоступний. Перевірте з'єднання й спробуйте ще раз.",
  // §20.5: налаштування ШІ
  unknown_provider: "Невідомий провайдер ШІ.",
  provider_not_available: "Цей провайдер ШІ ще недоступний. Оберіть іншого.",
  unknown_model: "Ця модель недоступна. Оновіть сторінку й оберіть модель зі списку.",
  model_provider_mismatch: "Модель не належить обраному провайдеру. Оберіть модель зі списку.",
  invalid_mode: "Невідомий режим автономності.",
  auto_mode_ack_required: "Для режиму «Діє сам» потрібне підтвердження. Оберіть режим ще раз і підтвердьте.",
  invalid_limits: "Некоректні ліміти. Перевірте діапазони значень.",
  prompt_addendum_too_long: "Промпт задовгий: максимум 4000 символів.",
  prompt_addendum_forbidden: "У промпті є заборонені службові теги або рядки, що починаються з «===». Приберіть їх.",
  prompt_addendum_contains_secret: "У промпті схоже на ключ доступу. Приберіть його: ключі вводяться лише в полі ключа API.",
  invalid_key_format: "Ключ має неправильний формат. Скопіюйте його з кабінету провайдера повністю.",
  key_rejected: "Провайдер відхилив цей ключ. Перевірте його й спробуйте ще раз.",
  key_affects_other_roles: "Ключ використовує інший сценарій. Підтвердіть зміну в діалозі.",
  invalid_months: "Некоректна кількість місяців.",
  provider_unavailable: "Провайдер ШІ тимчасово недоступний, ключ не збережено. Спробуйте пізніше.",
  ai_secrets_unavailable: "Сховище ключів на сервері не налаштовано. Зверніться до підтримки.",
  ai_unavailable: "ШІ зараз недоступний. Перевірте стан у налаштуваннях.",
  ai_quota_exceeded: "Місячний ліміт ШІ вичерпано.",
  ai_rate_limited: "Забагато запитів до ШІ. Зачекайте хвилину.",
  not_supported: "Ця можливість ще не підключена до сервера.",
  // §22: нагадування, вподобання, задачі дзвінків, inbox
  conversation_not_found: "Розмову не знайдено. Можливо, її вже немає в списку.",
  channel_inactive: "Канал вимкнено, тому відповісти клієнту зараз не можна. Увімкніть канал у розділі «Канали».",
  channel_unsupported: "У цьому каналі відповідь із вебу поки недоступна. Відповідайте клієнту в самому каналі.",
  idempotency_conflict: "Це повідомлення вже надсилалося з іншим текстом. Оновіть розмову й спробуйте ще раз.",
  idempotency_key_required: "Не вдалося надіслати повідомлення. Оновіть сторінку й спробуйте ще раз.",
  invalid_text: "Текст відповіді має містити від 1 до 3500 символів, без службових символів.",
  client_not_found: "Клієнта не знайдено.",
  invalid_method: "Невідомий спосіб нагадування.",
  invalid_offsets: "Некоректні точки нагадування. Оберіть 1, 2 або 24 години; для дзвінка — лише одну точку.",
  invalid_reminder_method: "Невідомий спосіб нагадування.",
  invalid_reminder_offsets: "Некоректні точки нагадування. Оберіть 1, 2 або 24 години; для дзвінка — лише одну точку.",
  reminder_conflict: "Вкажіть або нові налаштування нагадувань, або старий варіант, не обидва.",
  telegram_unavailable: "У клієнта немає розмови в підключеному Telegram-каналі, тому нагадати там не вдасться.",
  phone_required: "У клієнта немає телефону, тож дзвінок-нагадування неможливий.",
  call_reminders_disabled: "Дзвінки-нагадування вимкнено в налаштуваннях закладу.",
  telegram_reminders_disabled: "Нагадування в Telegram вимкнено в налаштуваннях закладу.",
  call_task_not_found: "Задачу дзвінка не знайдено.",
  invalid_day: "Некоректний день. Оберіть сьогодні, завтра або дату.",
  invalid_status: "Некоректний статус задачі.",
  invalid_note: "Нотатка задовга: максимум 300 символів.",
  task_already_closed: "Цю задачу вже закрив інший адміністратор.",
  task_cancelled: "Задачу скасовано системою (запис змінено або скасовано).",
  channel_not_found: "Канал не знайдено. Оновіть сторінку.",
  invalid_settings: "Некоректні налаштування каналу. Перевірте значення й спробуйте ще раз.",
  // §24: собівартість і деталі періоду
  service_not_found: "Послугу не знайдено. Оновіть сторінку.",
  cost_not_found: "Собівартість для цього закладу вже видалено. Оновіть сторінку.",
  invalid_cost: "Собівартість має бути числом від 0 до 1 000 000, не більше 2 знаків після коми.",
  invalid_master_percent: "Відсоток майстру має бути від 0 до 100, не більше 2 знаків після коми.",
  invalid_date: "Некоректна дата.",
  invalid_cursor: "Список змінився. Закрийте деталі й відкрийте їх знову.",
  // §25: телефон клієнта
  no_pending_phone: "Нового номера вже немає: рішення прийнято раніше. Оновіть картку клієнта.",
  invalid_action: "Невідома дія з номером.",
  invalid_quiet_hours: "Некоректні години тиші: формат ГГ:ХХ, початок і кінець різні, тиша не довша за 12 годин.",
  // §26: згода, розсилки, купони
  campaign_changed: "Кампанію змінили, поки ви переглядали. Перегляд оновлено, перевірте його ще раз.",
  revision_changed: "Кампанію змінили, поки ви переглядали. Перегляд оновлено, перевірте його ще раз.",
  campaign_not_draft: "Кампанію вже підтверджено або скасовано, змінити її не можна.",
  campaign_finished: "Кампанія вже завершена, скасувати її не можна.",
  campaign_not_found: "Розсилку не знайдено. Можливо, її видалено.",
  audience_empty: "У вибраній аудиторії немає клієнтів, яким можна надіслати повідомлення. Змініть сегмент.",
  audience_too_large: "Аудиторія завелика: максимум 5000 одержувачів. Звузьте сегмент.",
  promotion_inactive: "Акція вимкнена або закінчиться раніше запланованого часу відправки.",
  promotion_in_campaign: "Акція бере участь у розсилці. Змінити знижку, період, послуги чи доступ не можна; можна лише вимкнути акцію.",
  invalid_schedule: "Некоректний час відправки: не раніше ніж зараз і не далі ніж за 30 днів.",
  invalid_name: "Назва розсилки: від 1 до 200 символів.",
  invalid_type: "Невідомий тип розсилки.",
  promotion_required: "Для розсилки про акцію оберіть акцію.",
  promotion_not_found: "Акцію не знайдено. Оновіть сторінку.",
  promotion_not_code_only: "Купон можна створити лише для акції «Лише за кодом».",
  promotion_must_be_public: "Без купона розсилку можна робити лише про публічну акцію.",
  invalid_segment: "Некоректні параметри аудиторії. Перевірте числа й виберіть вид сегмента.",
  invalid_template: "Текст повідомлення не пройшов перевірку: перевірте підстановки, довжину й службові символи.",
  invalid_coupon_mode: "Некоректний тип купона.",
  coupon_not_found: "Код не знайдено. Оновіть список кодів.",
  coupon_not_shared: "Для розсилки можна вибрати лише загальний код.",
  coupon_promotion_mismatch: "Цей код належить іншій акції.",
  coupon_period_outside_promotion: "Строк дії купона має лежати в періоді акції.",
  invalid_code: "Код: від 4 до 20 символів, лише латинські літери й цифри.",
  invalid_period: "Некоректний строк дії: кінець має бути пізніше за початок.",
  invalid_max_uses: "Ліміт використань: ціле число від 1 до 100 000.",
  coupon_code_taken: "Такий код уже існує. Придумайте інший.",
  coupon_expired: "Строк дії коду минув.",
  coupon_not_started: "Код ще не діє.",
  coupon_exhausted: "Код уже використано максимальну кількість разів.",
  coupon_revoked: "Код відкликано.",
  coupon_foreign_client: "Цей код належить іншому клієнту.",
  coupon_already_used: "Клієнт уже користувався цим кодом.",
  coupon_invalid: "Код не підходить: він недійсний, використаний або вже не діє.",
  coupon_unavailable: "Купон щойно став недоступним. Спробуйте без нього.",
  invalid_coupon_format: "Перевірте формат коду: від 4 до 24 символів, літери, цифри, пробіл чи дефіс.",
  marketing_disabled: "Розсилки вимкнено для вашого закладу. Зверніться до підтримки, щоб їх підключити.",
  invalid_level: "Невідомий рівень згоди.",
  basis_required: "Щоб підвищити згоду, вкажіть підставу.",
  invalid_basis: "Оберіть підставу зі списку.",
  invalid_basis_note: "Нотатка до підстави: від 1 до 200 символів.",
  admin_grant_disabled: "Підвищувати згоду за клієнта вимкнено. Клієнт може обрати рівень сам у боті (команда /marketing).",
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

/** Тексти для «Оновити меню команд бота» (§23.4): коди `channel_unsupported` / `telegram_unavailable` тут означають інше, ніж у відповіді клієнту. */
const SYNC_COMMANDS_BY_CODE: Record<string, string> = {
  channel_not_found: "Канал не знайдено. Оновіть сторінку й спробуйте ще раз.",
  channel_unsupported: "Меню команд можна оновити лише для Telegram-бота.",
  channel_inactive: "Канал вимкнено. Увімкніть його й спробуйте ще раз.",
  token_missing: "Для каналу не вказано токен бота. Підключіть бота заново.",
  telegram_unavailable: "Telegram зараз недоступний. Спробуйте ще раз за кілька хвилин.",
};

export function humanizeSyncCommandsError(e: unknown): string {
  if (e instanceof BeautyApiError) {
    const text = SYNC_COMMANDS_BY_CODE[e.code];
    if (text) return text;
    if (e.status === 404) return SYNC_COMMANDS_BY_CODE.channel_not_found;
    if (e.status === 502) return SYNC_COMMANDS_BY_CODE.telegram_unavailable;
  }
  return humanizeError(e, "Не вдалося оновити меню команд. Спробуйте ще раз.");
}

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
  let affectedRoles: string[] | undefined;
  let task: unknown;
  try {
    const body = (await res.json()) as { code?: unknown; message?: unknown; conflicts?: unknown; affectedRoles?: unknown; task?: unknown };
    if (typeof body.code === "string") code = body.code;
    if (typeof body.message === "string") message = body.message;
    if (Array.isArray(body.conflicts)) conflicts = body.conflicts.flatMap(parseConflict);
    if (Array.isArray(body.affectedRoles)) affectedRoles = body.affectedRoles.filter((r): r is string => typeof r === "string");
    if (body.task && typeof body.task === "object") task = body.task;
  } catch {
    /* не JSON */
  }
  const retry = Number(res.headers.get("Retry-After"));
  return new BeautyApiError(res.status, code, message, Number.isFinite(retry) && retry > 0 ? retry : undefined, conflicts, affectedRoles, task);
}
