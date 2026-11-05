import type { ContactSource, ReminderMethod } from "./types";

/** Дозволені точки нагадування, хвилини до візиту (§22.3 CHECK `<@ '{60,120,1440}'`). */
export const REMINDER_OFFSETS = [60, 120, 1440] as const;
export type ReminderOffset = (typeof REMINDER_OFFSETS)[number];

export const METHOD_LABEL: Record<ReminderMethod | "auto", string> = {
  none: "Не нагадувати",
  telegram: "Telegram",
  call: "Дзвінок адміністратора",
  auto: "Автоматично",
};

export const SOURCE_LABEL: Record<ContactSource, string> = {
  chat: "вибір клієнта в чаті",
  admin: "адміністратор",
  stop: "команда «Стоп» у чаті",
  web: "онлайн-форма запису",
};

export function offsetLabel(minutes: number): string {
  if (minutes === 1440) return "за 24 год";
  if (minutes % 60 === 0) return `за ${minutes / 60} год`;
  return `за ${minutes} хв`;
}

/** Унікальні значення за зростанням (застосунок нормалізує масив, §22.3). */
export const normalizeOffsets = (list: number[]): number[] => [...new Set(list)].sort((a, b) => a - b);

/** «за 2 год і за 24 год». */
export function offsetsText(offsets: number[]): string {
  const parts = normalizeOffsets(offsets).map(offsetLabel);
  if (parts.length <= 1) return parts.join("");
  return `${parts.slice(0, -1).join(", ")} і ${parts[parts.length - 1]}`;
}

/** Одним рядком: «Telegram: за 2 год і за 24 год», «Дзвінок: за 24 год», «Не нагадувати». */
export function reminderLine(method: ReminderMethod | "auto", offsets: number[]): string {
  if (method === "none" || offsets.length === 0) return "Не нагадувати";
  const name = method === "telegram" ? "Telegram" : method === "call" ? "Дзвінок" : "Автоматично";
  return `${name}: ${offsetsText(offsets)}`;
}

/** Правила §22.6.1: `none` - без точок; `telegram` - 1..3 з дозволених; `call` - рівно одна. Повертає код або `null`. */
export function validateOffsets(method: ReminderMethod, offsets: number[]): "invalid_offsets" | null {
  const list = normalizeOffsets(offsets);
  if (list.some((o) => !(REMINDER_OFFSETS as readonly number[]).includes(o))) return "invalid_offsets";
  if (method === "none") return list.length === 0 ? null : "invalid_offsets";
  if (method === "call") return list.length === 1 ? null : "invalid_offsets";
  return list.length >= 1 && list.length <= 3 ? null : "invalid_offsets";
}

/** Людські причини пропуску точки (`skipReason` - лише код, §22.4). */
export const SKIP_REASON_LABEL: Record<string, string> = {
  quiet_hours: "години тиші",
  merged: "об'єднано з сусідньою точкою",
  no_channel: "немає каналу для надсилання",
  no_phone: "немає телефону",
  call_disabled: "дзвінки вимкнено",
  telegram_disabled: "Telegram вимкнено",
  appointment_inactive: "запис неактивний",
  replanned: "перепланувано",
  too_late: "надто пізно",
  stopped: "клієнт відмовився",
  bot_blocked: "клієнт заблокував бота",
  chat_not_found: "чат не знайдено",
  channel_inactive: "канал вимкнено",
};

export const POINT_STATUS_LABEL: Record<string, string> = {
  scheduled: "заплановано",
  sent: "надіслано",
  failed: "не доставлено",
  skipped: "пропущено",
  cancelled: "скасовано",
  open: "очікує дзвінка",
  done: "зроблено",
};

const HHMM = /^([01]\d|2[0-3]):[0-5]\d$/;
const toMin = (v: string) => Number(v.slice(0, 2)) * 60 + Number(v.slice(3, 5));

/** Години тиші (§22.6.4): `HH:mm`, початок ≠ кінець, тривалість ≤ 12 год (через північ теж). */
export function validateQuietHours(start: string, end: string): boolean {
  if (!HHMM.test(start) || !HHMM.test(end) || start === end) return false;
  const len = (toMin(end) - toMin(start) + 1440) % 1440;
  return len <= 720;
}
