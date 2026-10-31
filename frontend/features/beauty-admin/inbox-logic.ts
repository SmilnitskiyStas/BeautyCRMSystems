import { instantToLocal } from "../beauty-shared/zone-time";
import { dateTimeLabel, shortDate } from "./format";
import { maskCouponCodes } from "./marketing/logic";
import type { ConversationMessage, ConversationSummary, MessageSenderType } from "./types";
import type { Tone } from "./components/ui";

/** Ліміт довжини відповіді менеджера (§22.6.5): 1..3500 кодових точок. */
export const MAX_REPLY = 3500;
export const replyLength = (text: string): number => [...text.trim()].length;
/** Керівні символи, крім `\n` (бракує серверу): блокуємо до відправки. */
const CONTROL = /[\x00-\x09\x0b-\x1f\x7f]/;
export const hasControlChars = (text: string): boolean => CONTROL.test(text);

export const HANDOFF_REASON_LABEL: Record<string, string> = {
  complaint: "скарга",
  unclear: "незрозумілий запит",
  human_requested: "клієнт попросив людину",
  medical: "медичне питання",
  other: "інша причина",
  payment: "питання оплати",
  ai_unavailable: "ШІ недоступний",
  limits_required: "не задано ліміти ШІ",
  policy_unknown: "питання про політику закладу",
  reschedule_request: "прохання перенести запис",
  cancel_limit: "перевищено ліміт скасувань за добу",
  cancel_failed: "не вдалося скасувати запис",
  cancel_request: "прохання скасувати запис",
  booking_limit: "ліміт записів",
  booking_limit_reached: "ліміт записів",
  booking_failed: "не вдалося створити запис",
  staff_reply: "відповів адміністратор",
};
export const handoffReasonLabel = (code?: string) => (code ? (HANDOFF_REASON_LABEL[code] ?? "інша причина") : "");

/** Помилки доставки (§22.5): лише код, без тексту. */
export const FAILURE_LABEL: Record<string, string> = {
  bot_blocked: "не доставлено: клієнт заблокував бота",
  chat_not_found: "не доставлено: чат клієнта не знайдено",
  channel_inactive: "не доставлено: канал вимкнено",
  token_missing: "не доставлено: канал не налаштовано",
};
export const failureLabel = (code?: string) => (code ? (FAILURE_LABEL[code] ?? "не доставлено") : "не доставлено");

export const SENDER_LABEL: Record<MessageSenderType, string> = {
  client: "Клієнт",
  ai: "ШІ",
  staff: "Адміністратор",
  system: "Система",
};

/** Локальна дата-час `YYYY-MM-DDTHH:mm` у зоні закладу (без зони - зона браузера). */
export const localOf = (iso: string, timezone?: string): string => instantToLocal(iso, timezone);
export const clockOf = (iso: string, timezone?: string): string => localOf(iso, timezone).slice(11, 16);
/** «14:05» для сьогодні, «5 жовт., 14:05» для інших днів. */
export function stamp(iso: string, timezone?: string, now: Date = new Date()): string {
  const local = localOf(iso, timezone);
  const today = localOf(now.toISOString(), timezone).slice(0, 10);
  return local.slice(0, 10) === today ? local.slice(11, 16) : dateTimeLabel(local);
}
/** Заголовок дня в розмові. */
export function dayHeading(iso: string, timezone?: string, now: Date = new Date()): string {
  const day = localOf(iso, timezone).slice(0, 10);
  const today = localOf(now.toISOString(), timezone).slice(0, 10);
  if (day === today) return "Сьогодні";
  const yesterday = localOf(new Date(now.getTime() - 86_400_000).toISOString(), timezone).slice(0, 10);
  return day === yesterday ? "Вчора" : shortDate(day);
}

export interface AiStateView {
  tone: Tone;
  label: string;
  /** Додатковий рядок («Пауза до 02:00»). */
  extra?: string;
}

/** Стан ШІ (§22.11): «ШІ відповідає» / «Передано адміністратору {час}, причина» / «Пауза до {час}» / «ШІ не втручається до «Повернути ШІ»». */
export function aiStateView(c: Pick<ConversationSummary, "aiState" | "handoffReason" | "aiPausedUntil" | "awaitingStaffSince">, timezone?: string, now?: Date): AiStateView {
  if (c.aiState !== "handoff") {
    return c.awaitingStaffSince
      ? { tone: "wait", label: `Очікує відповіді з ${stamp(c.awaitingStaffSince, timezone, now)}`, extra: "ШІ відповідає на нові повідомлення" }
      : { tone: "ok", label: "ШІ відповідає" };
  }
  const reason = handoffReasonLabel(c.handoffReason);
  if (c.awaitingStaffSince) {
    return {
      tone: "wait",
      label: `Передано адміністратору ${stamp(c.awaitingStaffSince, timezone, now)}${reason ? `, ${reason}` : ""}`,
      ...(c.aiPausedUntil ? { extra: `Пауза до ${stamp(c.aiPausedUntil, timezone, now)}` } : { extra: "ШІ не втручається до «Повернути ШІ»" }),
    };
  }
  if (c.aiPausedUntil) return { tone: "now", label: `Пауза до ${stamp(c.aiPausedUntil, timezone, now)}`, ...(reason ? { extra: reason } : {}) };
  return { tone: "neutral", label: "ШІ не втручається до «Повернути ШІ»", ...(reason ? { extra: reason } : {}) };
}

export function senderOf(m: ConversationMessage): string {
  return m.senderType === "staff" && m.senderUser?.name ? m.senderUser.name : SENDER_LABEL[m.senderType] ?? "Система";
}

export const REPLY_BLOCKED_TEXT: Record<string, string> = {
  channel_inactive: "Канал вимкнено, тому відповісти клієнту з вебу зараз не можна. Увімкніть канал у розділі «Канали».",
  channel_unsupported: "У цьому каналі відповідь із вебу поки недоступна (у першій версії лише Telegram). Відповідайте клієнту в самому каналі.",
};

/** Префікс тіла `callback`-повідомлення на сервері (§23.10): `"[кнопка] {підпис}"`. */
const CALLBACK_PREFIX = "[кнопка]";
const MAX_BUTTONS = 10;
const MAX_LABEL = 64;

/** Підпис натиснутої кнопки: з `pressedLabel`, інакше з префікса `body`; сирий callback не показуємо ніколи. */
export function pressedLabelOf(m: Pick<ConversationMessage, "kind" | "body" | "pressedLabel">): string | null {
  if (m.kind !== "callback") return null;
  const label = (m.pressedLabel ?? (m.body.startsWith(CALLBACK_PREFIX) ? m.body.slice(CALLBACK_PREFIX.length) : "")).trim();
  return [...label].slice(0, MAX_LABEL).join("");
}

/** «Клієнт натиснув: {підпис}» (без підпису - «Клієнт натиснув кнопку»). */
export function pressedText(m: Pick<ConversationMessage, "kind" | "body" | "pressedLabel">): string | null {
  const label = pressedLabelOf(m);
  if (label === null) return null;
  return label ? `Клієнт натиснув: ${label}` : "Клієнт натиснув кнопку";
}

/** Підписи кнопок вихідного повідомлення: лише непорожні рядки, до 10 штук. */
export function buttonLabels(m: Pick<ConversationMessage, "buttons">): string[] {
  if (!Array.isArray(m.buttons)) return [];
  return m.buttons
    .filter((b): b is string => typeof b === "string" && b.trim() !== "")
    .slice(0, MAX_BUTTONS)
    .map((b) => [...b.trim()].slice(0, MAX_LABEL).join(""));
}

/** Службові тіла §25.4/§25.5.2: номера в повідомленні немає ні в якому вигляді. */
export const CONTACT_BODY = "[контакт]";
export const NUMBER_BODY = "[номер телефону]";

/**
 * Подія «клієнт поділився номером» (§25.8): чип замість тіла. Для `kind=contact` тіло НЕ показується ніколи; сам номер
 * у повідомленні відсутній за контрактом (є лише в картці клієнта й «Дзвінках»).
 */
export function contactChip(m: Pick<ConversationMessage, "kind" | "body" | "contactVerdict">): string | null {
  if (m.kind === "contact") {
    if (m.contactVerdict === "own") return "Клієнт поділився своїм контактом";
    if (m.contactVerdict === "foreign") return "Надіслано чужий контакт — відхилено";
    if (m.contactVerdict === "invalid") return "Некоректний номер";
    return "Клієнт поділився контактом";
  }
  return m.body === NUMBER_BODY ? "Клієнт написав номер" : null;
}

/** §26.9.3: тіло вхідного повідомлення з кодом купона замінюється сервером - самого коду в історії немає. */
export const COUPON_BODY = "[код купона]";

/** Шаблонні підписи подій згоди/купона за підписом натиснутої кнопки (без кодів і ПД); `null` - звичайна кнопка. */
export function marketingChipForLabel(label: string): string | null {
  const l = label.toLowerCase();
  if (l.includes("записатися зі знижкою")) return "Клієнт натиснув «Записатися зі знижкою»";
  if (l.includes("не надсилати")) return "Клієнт відписався від акцій";
  if (l.includes("повна інформація")) return "Клієнт обрав розсилки: повна інформація";
  if (l.includes("лише акції")) return "Клієнт обрав розсилки: лише акції на його послуги";
  return null;
}

/** Подія згоди/купона (§26.12): чип замість тіла. */
export function marketingChip(m: Pick<ConversationMessage, "kind" | "body" | "pressedLabel">): string | null {
  if (m.body === COUPON_BODY) return "Клієнт ввів код";
  const label = pressedLabelOf(m);
  return label ? marketingChipForLabel(label) : null;
}

/** Превʼю - подія, а не репліка: без префікса відправника. */
export const isCallbackPreview = (preview: string): boolean => preview.startsWith(CALLBACK_PREFIX) || preview === CONTACT_BODY || preview === NUMBER_BODY || preview === COUPON_BODY;

/** Превʼю в списку розмов: callback `[кнопка] X` -> «Клієнт натиснув: X»; `[контакт]`/`[номер телефону]` -> подія. */
export function previewText(preview: string): string {
  if (preview === CONTACT_BODY) return "Клієнт поділився контактом";
  if (preview === NUMBER_BODY) return "Клієнт написав номер";
  if (preview === COUPON_BODY) return "Клієнт ввів код";
  if (preview.startsWith(CALLBACK_PREFIX)) {
    const label = pressedLabelOf({ kind: "callback", body: preview });
    return (label ? marketingChipForLabel(label) : null) ?? pressedText({ kind: "callback", body: preview }) ?? preview;
  }
  return maskCouponCodes(preview);
}

/** Маска для зчитувачів з екрана: «Телефон приховано, закінчується на 33». */
export function maskLabel(masked: string): string {
  const tail = masked.replace(/\D/g, "");
  return tail ? `Телефон приховано, закінчується на ${tail}` : "Телефон приховано";
}
