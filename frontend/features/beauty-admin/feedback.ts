import type { FeedbackInput, FeedbackSeverity, FeedbackType } from "./types";

/** Межі тексту заявки збігаються з бекендом (§27.9): 10..2000 символів після обрізання пробілів. */
export const FEEDBACK_MIN = 10;
export const FEEDBACK_MAX = 2000;
const PAGE_URL_MAX = 500;

export const FEEDBACK_TYPES: { id: FeedbackType; label: string; hint: string }[] = [
  { id: "cooperation", label: "Співпраця", hint: "Пропозиція партнерства або інтеграції" },
  { id: "improvement", label: "Покращити", hint: "Ідея, що зробить роботу зручнішою" },
  { id: "bug", label: "Не працює", hint: "Помилка або збій у роботі" },
];

export const FEEDBACK_SEVERITIES: { id: FeedbackSeverity; label: string }[] = [
  { id: "low", label: "Низька: дрібниця, є обхідний шлях" },
  { id: "normal", label: "Звичайна" },
  { id: "high", label: "Висока: заважає працювати" },
  { id: "critical", label: "Критична: робота зупинилась" },
];

export const DEFAULT_SEVERITY: FeedbackSeverity = "normal";

/**
 * ПЛЕЙСХОЛДЕР (Q8, human gate): фінальний юридичний текст про передачу даних провайдеру надасть власник.
 * Маркер `[ПЛЕЙСХОЛДЕР Q8]` лишається в тексті, доки його не замінено; тест стежить, щоб маркер не загубився непомітно.
 */
export const FEEDBACK_DATA_NOTICE_MARKER = "[ПЛЕЙСХОЛДЕР Q8]";
export const FEEDBACK_DATA_NOTICE = `${FEEDBACK_DATA_NOTICE_MARKER} Заявка передається розробникам платформи разом із вашим ім'ям, поштою, назвою закладу та сторінкою, з якої її надіслано. Остаточний текст про обробку даних буде затверджено власником.`;

const FORBIDDEN_CONTROL = /[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]/;

/** Довжина в символах Unicode (як `char_length` на сервері), а не в UTF-16 одиницях. */
export const messageLength = (raw: string): number => Array.from(raw.trim()).length;

export type FeedbackFormErrors = { message?: string };

export function validateFeedbackMessage(raw: string): string | undefined {
  const n = messageLength(raw);
  if (n < FEEDBACK_MIN) return `Опишіть докладніше: щонайменше ${FEEDBACK_MIN} символів.`;
  if (n > FEEDBACK_MAX) return `Занадто довго: не більше ${FEEDBACK_MAX} символів, зараз ${n}.`;
  if (FORBIDDEN_CONTROL.test(raw)) return "У тексті є службові символи. Приберіть їх і спробуйте ще раз.";
  return undefined;
}

/**
 * `pageUrl` за §27.9: лише шлях без query і фрагмента. Якщо значення не пройде серверну перевірку,
 * краще не слати нічого (поле необов'язкове), ніж отримати 422 через службовий параметр.
 */
export function pageUrlFrom(pathname: string | null | undefined): string | undefined {
  if (!pathname) return undefined;
  const path = pathname.split(/[?#]/, 1)[0];
  if (!path.startsWith("/") || path.startsWith("//") || path.length > PAGE_URL_MAX) return undefined;
  if (!/^[A-Za-z0-9/\-_.~%!$&'()*+,;=:@]+$/.test(path)) return undefined;
  return path;
}

export function buildFeedbackInput(v: { type: FeedbackType; severity: FeedbackSeverity; message: string }, pathname: string | null | undefined): FeedbackInput {
  const pageUrl = pageUrlFrom(pathname);
  return {
    type: v.type,
    // Критичність має сенс лише для «Не працює»; для решти типів сервер очікує `normal`.
    severity: v.type === "bug" ? v.severity : DEFAULT_SEVERITY,
    message: v.message.trim(),
    ...(pageUrl ? { pageUrl } : {}),
  };
}
