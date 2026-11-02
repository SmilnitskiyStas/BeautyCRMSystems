import { BeautyApiError, humanizeError } from "@/features/beauty-auth/errors";
import { int, money, serializeCsv, text, type CsvCell, type CsvFile } from "../analytics/csv";
import type {
  CampaignDto,
  CampaignStats,
  CampaignStatus,
  CampaignType,
  ConsentBasis,
  ConsentEvent,
  ConsentLevel,
  ConsentSource,
  CouponMode,
  CouponSummary,
  DiscountType,
  ExcludedCounts,
  PreviewWarning,
  Segment,
  SegmentKind,
} from "./types";

/** Вікно відправки V1 (§26.2): константа, налаштувань tenant немає. */
export const SEND_WINDOW = { from: "09:00", to: "20:00" } as const;
export const TEMPLATE_MAX = 1000;
export const NAME_MAX = 200;
export const MAX_SCHEDULE_DAYS = 30;
export const MAX_RECIPIENTS = 5000;
export const BASIS_NOTE_MAX = 200;

// ---------- підписи ----------

export const STATUS_LABEL: Record<CampaignStatus, string> = {
  draft: "Чернетка",
  scheduled: "Заплановано",
  sending: "Надсилається",
  done: "Завершено",
  cancelled: "Скасовано",
};
export const STATUS_TONE: Record<CampaignStatus, "neutral" | "wait" | "now" | "ok"> = {
  draft: "neutral",
  scheduled: "wait",
  sending: "now",
  done: "ok",
  cancelled: "neutral",
};
export const CAMPAIGN_STATUSES: CampaignStatus[] = ["draft", "scheduled", "sending", "done", "cancelled"];
export const TYPE_LABEL: Record<CampaignType, string> = { promo: "Акція", news: "Новина" };

export const SEGMENTS: { kind: SegmentKind; label: string; hint: string }[] = [
  { kind: "lapsed", label: "Давно не були", hint: "Клієнти, чий останній завершений візит був N днів тому або раніше, без майбутнього запису." },
  { kind: "frequent", label: "Часті клієнти", hint: "Клієнти, які були не менше N разів за останні M днів." },
  { kind: "new", label: "Нові клієнти", hint: "Клієнти, у яких рівно один завершений візит." },
  { kind: "all_consented", label: "Усі зі згодою", hint: "Усі клієнти закладу; повідомлення отримають лише ті, хто дав згоду." },
];

export const LEVEL_LABEL: Record<ConsentLevel, string> = {
  full: "Повна інформація",
  selective: "Лише акції на мої послуги",
  none: "Розсилки вимкнено",
};
export const LEVEL_SHORT: Record<ConsentLevel, string> = { full: "повна інформація", selective: "лише акції на послуги клієнта", none: "розсилки вимкнено" };
export const SOURCE_LABEL: Record<ConsentSource, string> = {
  chat: "Клієнт обрав у боті",
  admin: "Змінив адміністратор",
  stop: "Клієнт написав «Стоп»",
};
export const BASIS_LABEL: Record<ConsentBasis, string> = {
  written: "Письмова згода клієнта",
  in_person: "Згода особисто в закладі",
  other: "Інша підстава",
};

export const EXCLUDED_REASONS: { key: keyof ExcludedCounts; label: string; hint: string }[] = [
  { key: "noTelegram", label: "Немає Telegram", hint: "Клієнт не писав у підключений Telegram-бот, тому повідомлення йому не дійде." },
  { key: "unsubscribed", label: "Відписались", hint: "Клієнт вимкнув розсилки або написав «Стоп»." },
  { key: "noConsent", label: "Без згоди", hint: "Клієнт ще не обирав, чи отримувати акції." },
  { key: "levelMismatch", label: "Інший рівень згоди", hint: "Клієнт отримує лише акції на свої послуги, а ця розсилка йому не підходить." },
  { key: "frequency", label: "Недавно отримували повідомлення", hint: "Не більше одного маркетингового повідомлення на клієнта за обмежений період." },
];

export const WARNING_TEXT: Record<PreviewWarning, string> = {
  promotion_ends_soon: "Акція скоро закінчується: частина клієнтів може не встигнути скористатися.",
  coupon_expires_before_send: "Строк дії купона закінчиться раніше, ніж повідомлення дійдуть до всіх клієнтів.",
  exceeds_daily_limit: "Одержувачів більше за добовий ліміт: розсилка триватиме кілька днів.",
  no_buttons_channel: "У каналі вимкнено кнопки: клієнт побачить текстову підказку замість кнопок.",
};

// ---------- шаблон ----------

export interface PlaceholderDef {
  token: string;
  label: string;
  /** Лише для кампаній `promo`. */
  promoOnly: boolean;
  /** Потребує купона. */
  needsCoupon: boolean;
}

export const PLACEHOLDERS: PlaceholderDef[] = [
  { token: "name", label: "Ім'я", promoOnly: false, needsCoupon: false },
  { token: "discount", label: "Знижка", promoOnly: true, needsCoupon: false },
  { token: "service", label: "Послуга", promoOnly: true, needsCoupon: false },
  { token: "valid_from", label: "Діє з", promoOnly: true, needsCoupon: false },
  { token: "valid_to", label: "Діє до", promoOnly: true, needsCoupon: false },
  { token: "code", label: "Код", promoOnly: false, needsCoupon: true },
];

const KNOWN = new Set(PLACEHOLDERS.map((p) => p.token));
const CONTROL = /[\x00-\x09\x0b-\x1f\x7f]/;

/** Підстановка доступна: `{code}` - лише з купоном, `{discount|service|valid_*}` - лише в акції. */
export function placeholderAllowed(p: PlaceholderDef, type: CampaignType, couponRequired: boolean): boolean {
  if (p.promoOnly && type !== "promo") return false;
  if (p.needsCoupon && !couponRequired) return false;
  return true;
}

export function usedPlaceholders(template: string): string[] {
  return [...template.matchAll(/\{([^{}]*)\}/g)].map((m) => m[1]);
}

/** Дзеркало серверної валідації `invalid_template` (§26.7): повертає текст помилки або `null`. */
export function validateTemplate(template: string, o: { type: CampaignType; couponMode: CouponMode }): string | null {
  if (template.trim().length === 0) return "Введіть текст повідомлення.";
  if ([...template].length > TEMPLATE_MAX) return `Текст задовгий: максимум ${TEMPLATE_MAX} символів.`;
  if (CONTROL.test(template)) return "У тексті є службові символи. Приберіть їх (дозволено лише перенесення рядка).";
  for (const name of usedPlaceholders(template)) {
    const def = PLACEHOLDERS.find((p) => p.token === name);
    if (!KNOWN.has(name) || !def) return `Невідома підстановка {${name}}. Доступні: ${PLACEHOLDERS.map((p) => `{${p.token}}`).join(", ")}.`;
    if (def.promoOnly && o.type !== "promo") return `Підстановка {${name}} доступна лише в розсилці про акцію.`;
    if (def.needsCoupon && o.couponMode === "none") return "Підстановка {code} потребує купона. Оберіть тип купона або приберіть її з тексту.";
  }
  return null;
}

export function validateName(name: string): string | null {
  const n = name.trim();
  if (n.length === 0) return "Вкажіть назву розсилки.";
  if ([...n].length > NAME_MAX) return `Назва задовга: максимум ${NAME_MAX} символів.`;
  return null;
}

export function validateSegment(s: Segment): string | null {
  const range = (v: number | undefined, lo: number, hi: number) => typeof v === "number" && Number.isInteger(v) && v >= lo && v <= hi;
  if (s.kind === "lapsed" && !range(s.days, 1, 730)) return "Вкажіть, скільки днів клієнт не був: від 1 до 730.";
  if (s.kind === "frequent") {
    if (!range(s.minVisits, 1, 100)) return "Вкажіть кількість візитів: від 1 до 100.";
    if (!range(s.periodDays, 1, 730)) return "Вкажіть період у днях: від 1 до 730.";
  }
  return null;
}

/** Нормалізація сегмента: поза своїм видом параметри не передаються (CHECK-и §26.3). */
export function normalizeSegment(s: Segment): Segment {
  switch (s.kind) {
    case "lapsed":
      return { kind: "lapsed", days: s.days, ...(s.serviceId ? { serviceId: s.serviceId } : {}) };
    case "frequent":
      return { kind: "frequent", minVisits: s.minVisits, periodDays: s.periodDays };
    default:
      return { kind: s.kind };
  }
}

// ---------- приклад повідомлення (§26.10) ----------

export interface SampleContext {
  discount?: string;
  services?: string[];
  validFrom?: string;
  validTo?: string;
  /** Код для прикладу: `ABCDE-FGHJK` або загальний код. */
  code?: string;
  name?: string;
}

export const SAMPLE_PERSONAL_CODE = "ABCDE-FGHJK";
export const UNSUBSCRIBE_LINE = "Щоб не отримувати акції, напишіть «Стоп» або натисніть кнопку «Не надсилати акції».";

export function formatDiscount(type: DiscountType, value: number): string {
  return type === "percent" ? `${value}%` : `${value} грн`;
}

export function serviceList(names: string[]): string {
  if (names.length === 0) return "усі послуги";
  if (names.length <= 3) return names.join(", ");
  return `${names.slice(0, 3).join(", ")} та ще ${names.length - 3}`;
}

const sanitize = (v: string) => [...v.replace(/[\x00-\x1f\x7f]+/g, " ").trim()].slice(0, 80).join("");

/** Підставляє значення в шаблон; невідомі підстановки лишає як є (валідація їх відсікає окремо). */
export function renderTemplate(template: string, c: SampleContext): string {
  const map: Record<string, string> = {
    name: c.name ?? "Вітаємо",
    discount: c.discount ?? "",
    service: serviceList(c.services ?? []),
    valid_from: c.validFrom ?? "",
    valid_to: c.validTo ?? "без обмеження строку",
    code: c.code ?? "",
  };
  return template.replace(/\{([^{}]*)\}/g, (m, k: string) => (k in map ? sanitize(map[k]) : m));
}

/** Рядки, які додає код і які менеджер не може прибрати (показуються сірим у редакторі). */
export function fixedLines(o: { type: CampaignType; couponMode: CouponMode; template: string }, c: SampleContext): string[] {
  const lines: string[] = [];
  if (o.type === "promo") {
    lines.push(
      `Умови: знижка ${c.discount ?? "{discount}"} на ${serviceList(c.services ?? [])}, діє з ${c.validFrom ?? "{valid_from}"} по ${c.validTo ?? "без обмеження строку"}. Одна знижка на запис, не сумується з іншими акціями.`,
    );
  }
  if (o.couponMode !== "none" && !o.template.includes("{code}")) lines.push(`Ваш код: ${c.code ?? SAMPLE_PERSONAL_CODE}`);
  lines.push(UNSUBSCRIBE_LINE);
  return lines;
}

export function sampleMessage(o: { type: CampaignType; couponMode: CouponMode; template: string }, c: SampleContext): string {
  const defined = Object.fromEntries(Object.entries(c).filter(([, v]) => v !== undefined)) as SampleContext;
  const ctx = { name: "Олена", code: SAMPLE_PERSONAL_CODE, ...defined };
  return [renderTemplate(o.template, ctx), ...fixedLines(o, ctx)].join("\n\n");
}

export function sampleButtons(couponMode: CouponMode): string[] {
  return [couponMode === "none" ? "📅 Записатися" : "🎟 Записатися зі знижкою", "🔕 Не надсилати акції"];
}

/** `dd.MM.yyyy` у зоні; кінець діапазону виключний, тож для `valid_to` передайте `exclusiveEnd`. */
export function dmy(iso: string, timeZone?: string, exclusiveEnd = false): string {
  const t = Date.parse(iso);
  if (Number.isNaN(t)) return "";
  const d = new Date(exclusiveEnd ? t - 1000 : t);
  const parts = new Intl.DateTimeFormat("uk-UA", { day: "2-digit", month: "2-digit", year: "numeric", ...(timeZone ? { timeZone } : {}) }).formatToParts(d);
  const get = (type: string) => parts.find((p) => p.type === type)?.value ?? "";
  return `${get("day")}.${get("month")}.${get("year")}`;
}

// ---------- купони ----------

export const SHARED_CODE_RE = /^[A-Za-z0-9]{4,20}$/;

export function sharedCodeError(code: string): string | null {
  return SHARED_CODE_RE.test(code.trim()) ? null : "Код: від 4 до 20 символів, лише латинські літери й цифри.";
}

/** Нормалізація §26.6 для порівняння на клієнті (не для відправки): регістр, пробіли, дефіси, O→0, I/L→1. */
export function normalizeCode(code: string): string {
  return code.toUpperCase().replace(/[\s-]/g, "").replace(/O/g, "0").replace(/[IL]/g, "1");
}

/** `•••••-••K7M`: повного коду немає, лише останні 3 символи. */
export const hintLabel = (hint: string) => `•••••-••${hint}`;

export function maxUsesError(v: string): string | null {
  if (v.trim() === "") return null;
  const n = Number(v);
  return Number.isInteger(n) && n >= 1 && n <= 100000 ? null : "Ліміт використань: ціле число від 1 до 100 000 або порожньо.";
}

// ---------- розклад ----------

/** Повертає текст помилки або `null`; `iso` порожній = «зараз». */
export function scheduleError(iso: string | undefined, nowMs: number): string | null {
  if (!iso) return null;
  const t = Date.parse(iso);
  if (Number.isNaN(t)) return "Вкажіть дату й час відправки.";
  if (t < nowMs - 5 * 60_000) return "Час відправки вже минув. Оберіть пізніший або запустіть зараз.";
  if (t > nowMs + MAX_SCHEDULE_DAYS * 86_400_000) return `Запланувати можна не далі ніж на ${MAX_SCHEDULE_DAYS} днів уперед.`;
  return null;
}

// ---------- помилки ----------

export const isMarketingDisabled = (e: unknown): boolean =>
  e instanceof BeautyApiError && (e.code === "marketing_disabled" || e.code === "module_disabled");

/** Коди, що збігаються із загальними (inbox тощо), але в маркетингу мають інший зміст. */
const BY_CODE: Record<string, string> = {
  idempotency_key_required: "Не вдалося підтвердити розсилку. Оновіть сторінку й спробуйте ще раз.",
  idempotency_conflict: "Це підтвердження вже надсилалося з іншими параметрами. Оновіть сторінку й перевірте розсилку.",
  channel_unsupported: "Цей канал згоди ще не підтримується. Зараз доступний лише Telegram.",
};

/** Тексти маркетингових помилок; серверне `message` не показуємо. Решта - загальний `humanizeError`. */
export function humanizeMarketingError(e: unknown, fallback = "Щось пішло не так. Спробуйте ще раз."): string {
  if (e instanceof BeautyApiError) {
    if (e.status === 429 || e.code === "rate_limited") {
      const s = e.retryAfterSeconds;
      return s && s <= 90 ? `Забагато запитів. Спробуйте ще раз приблизно через ${s} с.` : "Забагато запитів. Зачекайте хвилину й спробуйте ще раз.";
    }
    if (e.code === "forbidden_role") return "У вас немає прав на цю дію: розсилки доступні власнику й адміністратору.";
    if (BY_CODE[e.code]) return BY_CODE[e.code];
  }
  return humanizeError(e, fallback);
}

// ---------- журнал згоди ----------

export function consentEventLine(ev: ConsentEvent): string {
  const to = LEVEL_SHORT[ev.levelTo];
  const from = ev.levelFrom ? LEVEL_SHORT[ev.levelFrom] : "не обирав";
  const who = ev.source === "admin" ? (ev.actor?.name ? `адміністратор ${ev.actor.name}` : "адміністратор") : ev.source === "stop" ? "«Стоп» від клієнта" : "клієнт у боті";
  const basis = ev.basis ? `, підстава: ${BASIS_LABEL[ev.basis].toLowerCase()}` : "";
  return `${from} → ${to} (${who}${basis})`;
}

// ---------- CSV статистики ----------

const SKIP_LABEL: Record<string, string> = {
  campaign_cancelled: "Пропущено: кампанію скасовано",
  client_deleted: "Пропущено: клієнта видалено",
  no_channel: "Пропущено: немає каналу",
  no_consent: "Пропущено: немає згоди",
  unsubscribed: "Пропущено: відписався",
  level_mismatch: "Пропущено: інший рівень згоди",
  offer_expired: "Пропущено: пропозиція втратила чинність",
  frequency: "Пропущено: недавно отримував повідомлення",
  consent_withdrawn: "Пропущено: згоду відкликано",
  bot_blocked: "Пропущено: клієнт заблокував бота",
  chat_not_found: "Пропущено: чат не знайдено",
  channel_inactive: "Пропущено: канал вимкнено",
  token_missing: "Пропущено: немає токена",
  send_failed: "Пропущено: помилка відправки",
};
export const skipLabel = (code: string) => SKIP_LABEL[code] ?? "Пропущено: інша причина";

/**
 * CSV статистики кампанії: лише агрегати й назва (захист від CSV-injection через `text`). Імен, id, кодів купонів,
 * хешів і GUID у файлі немає.
 */
export function campaignStatsCsv(c: Pick<CampaignDto, "name" | "status">, s: CampaignStats, coupons?: CouponSummary | null): CsvFile {
  const rows: CsvCell[][] = [
    [text("Кампанія"), text(c.name)],
    [text("Статус"), text(STATUS_LABEL[c.status])],
    [text("Одержувачів"), int(s.recipients)],
    [text("Відправлено"), int(s.byStatus.sent)],
    [text("Очікують"), int(s.byStatus.pending + s.byStatus.queued)],
    [text("Помилки доставки"), int(s.byStatus.failed)],
    [text("Помилка: бот заблоковано"), int(s.failed.botBlocked)],
    [text("Помилка: чат не знайдено"), int(s.failed.chatNotFound)],
    [text("Помилка: інша"), int(s.failed.other)],
    [text("Пропущено"), int(s.byStatus.skipped)],
    ...Object.entries(s.skipped).map(([k, v]): CsvCell[] => [text(skipLabel(k)), int(v)]),
    [text("Натиснули «Записатися»"), int(s.clicks)],
    [text("Записів"), int(s.bookings)],
    [text("Записів за купоном"), int(s.bookingsWithCoupon)],
    [text("Активних використань купонів"), int(s.couponRedemptionsActive)],
    [text("Виручка від записів з розсилки, ₴"), money(s.revenue)],
    [text("Відписались після розсилки"), int(s.unsubscribedAfter)],
    [text("Переглянуто"), text("недоступно в Telegram")],
    ...(coupons
      ? [
          [text("Купонів видано"), int(coupons.issued)],
          [text("Купонів використано"), int(coupons.redeemedActive)],
          [text("Купонів повернено"), int(coupons.released)],
          [text("Купонів відкликано"), int(coupons.revoked)],
        ]
      : []),
  ];
  return { fileName: "beauty-campaign-stats.csv", content: serializeCsv(["Показник", "Значення"], rows) };
}

// ---------- ПД/коди в UI (inbox) ----------

const keepLast3 = (token: string): string => {
  const chars = [...token];
  let seen = 0;
  // Маскуємо все, крім трьох останніх буквено-цифрових символів; дефіси лишаємо.
  return chars
    .reverse()
    .map((ch) => {
      if (ch === "-") return ch;
      seen += 1;
      return seen <= 3 ? ch : "•";
    })
    .reverse()
    .join("");
};

/**
 * Маскує коди купонів у тексті повідомлення: після слова «код» (`Ваш код: ABCDE-FGHJK`, `напишіть сюди код AUTUMN15`) і персональний
 * формат `XXXXX-XXXXX` (Crockford). Лишаються останні 3 символи. Повний код у DOM після відправки не потрапляє.
 */
export function maskCouponCodes(textIn: string): string {
  return textIn
    .replace(/(код[:\s]+)([A-Za-z0-9][A-Za-z0-9-]{3,23})/gi, (_m, pre: string, code: string) => `${pre}${keepLast3(code)}`)
    .replace(/\b[0-9A-HJKMNP-TV-Z]{5}-[0-9A-HJKMNP-TV-Z]{5}\b/g, (m) => keepLast3(m));
}

// ---------- форма акції ----------

/** `dd.MM.yyyy` (поле «Початок/Завершення» акції) -> `YYYY-MM-DD`; некоректне -> `""`. */
export function parseDmy(s: string): string {
  const m = /^(\d{2})\.(\d{2})\.(\d{4})$/.exec(s.trim());
  if (!m) return "";
  const d = new Date(Date.UTC(+m[3], +m[2] - 1, +m[1]));
  return d.getUTCFullYear() === +m[3] && d.getUTCMonth() === +m[2] - 1 && d.getUTCDate() === +m[1] ? `${m[3]}-${m[2]}-${m[1]}` : "";
}

/** Помилки полів загального коду акції «Лише за кодом»; порожній об'єкт - можна зберігати. */
export function couponFormErrors(v: { code: string; maxUses: string; from: string; to: string }): { code?: string; maxUses?: string; period?: string } {
  const out: { code?: string; maxUses?: string; period?: string } = {};
  const code = sharedCodeError(v.code);
  if (code) out.code = code;
  const mu = maxUsesError(v.maxUses);
  if (mu) out.maxUses = mu;
  if (!v.from || !v.to) out.period = "Вкажіть строк дії коду.";
  else if (v.to < v.from) out.period = "Кінець дії коду має бути не раніше початку.";
  return out;
}
