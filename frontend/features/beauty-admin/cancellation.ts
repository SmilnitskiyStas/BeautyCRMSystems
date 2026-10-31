import type { CancelledBy } from "./types";

/**
 * «Скасував: клієнт | адміністратор (Ім'я) | система». Ім'я показуємо лише для staff і лише якщо API його віддав
 * (клієнтам/системі імені немає; не-керівникам backend його не надсилає).
 */
export function cancelledByLabel(by: CancelledBy | undefined): string {
  if (!by) return "Скасовано";
  if (by.type === "client") return "Скасував: клієнт";
  if (by.type === "system") return "Скасував: система";
  const name = by.name?.trim();
  return name ? `Скасував: адміністратор (${name})` : "Скасував: адміністратор";
}

/** Коротка форма для блоку календаря: «Скасував: клієнт». */
export const cancelledByShort = (by: CancelledBy | undefined) =>
  by ? cancelledByLabel(by).replace(/\s*\(.*\)$/, "") : "Скасовано";

export const MAX_CANCEL_REASON = 300;

/** Причина, яку backend записує при скасуванні клієнтом у чаті (§23.7); подія історії «Скасовано клієнтом у Telegram». */
export const CLIENT_TG_REASON = "Скасовано клієнтом у Telegram";
export const CLIENT_TG_CANCEL_EVENT = "Скасовано клієнтом у Telegram";

/** Скасовано клієнтом у Telegram: `cancelled_by_type=client` + стандартна причина (або джерело Telegram). */
export const isClientTelegramCancel = (a: { cancelledBy?: CancelledBy; cancelReason?: string }): boolean =>
  a.cancelledBy?.type === "client" && a.cancelReason?.trim() === CLIENT_TG_REASON;
