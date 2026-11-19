import { BeautyApiError, humanizeError } from "@/features/beauty-auth/errors";

/**
 * Прив'язка Telegram власника для сповіщень складу (§32.24, ADR-025 §12, TASK-891). Чиста логіка без React.
 * Токен і `deepLink` тримаються лише в стані компонента: не логуються, не пишуться в URL / localStorage / аналітику.
 */

export type TelegramLinkState = "none" | "pending" | "awaitingConfirmation" | "active";

export type RevokeReason =
  | "user_unlink"
  | "bot_stop"
  | "declined"
  | "replaced"
  | "expired"
  | "bot_blocked"
  | "chat_not_found"
  | "user_deactivated"
  | "role_changed"
  | "tenant_inactive";

export interface TelegramChannelRef {
  id: string;
  name: string;
}

export interface TelegramLinkStatus {
  featureEnabled: boolean;
  state: TelegramLinkState;
  channel: TelegramChannelRef | null;
  expiresAt: string | null;
  activatedAt: string | null;
  /** Час підтвердження claim у боті (ISO); null, якщо ще не було. Необов'язкове: додає backend у GET статусу. */
  claimedAt?: string | null;
  lastDeliveryAt: string | null;
  lastRevoke: { reason: RevokeReason; at: string } | null;
  consentVersion: string;
}

export interface TelegramLinkCreateRequest {
  consent: true;
  consentVersion: string;
  channelId?: string;
}

/** Відповідь `POST` (201, `no-store`): `deepLink` показується один раз. */
export interface TelegramLinkIssued {
  linkId: string;
  deepLink: string;
  expiresAt: string;
}

/** Поточна версія згоди; фактичну віддає сервер у `consentVersion`. */
export const CONSENT_VERSION = "inv-tg-v1";

/**
 * Чернетка тексту згоди `inv-tg-v1` (§32.24.7). ЧЕРНЕТКА для dev: для прод остаточний текст затверджує юрист (L3, ADR-025 §7.2),
 * після чого змінюється версія (`inv-tg-v2`) і цей рядок.
 */
export function consentText(botName: string | null): string {
  const bot = botName ? `«${botName}»` : "вашого салону";
  return `Ви вмикаєте службові сповіщення складу в Telegram від бота ${bot}. Ми збережемо ідентифікатор вашого чату Telegram у зашифрованому вигляді лише для надсилання цих сповіщень: заклад, товар, кількість, термін придатності. Дані клієнтів у сповіщеннях не надсилаються. Повідомлення передаються через сервіс Telegram. Вимкнути можна будь-коли в адмінці або командою /stop у боті — ідентифікатор чату буде видалено.`;
}

export const POLL_MS = 5_000;
export const TTL_MINUTES = 15;

/** Опитування лише в `pending|awaitingConfirmation` і не довше `expiresAt` (§32.24.9). */
export function shouldPoll(status: Pick<TelegramLinkStatus, "state" | "expiresAt"> | undefined, nowMs: number): boolean {
  if (!status) return false;
  if (status.state !== "pending" && status.state !== "awaitingConfirmation") return false;
  if (!status.expiresAt) return true;
  const t = Date.parse(status.expiresAt);
  return Number.isFinite(t) ? nowMs < t : true;
}

/** Залишок до `expiresAt` у секундах (>= 0). */
export function secondsLeft(expiresAt: string | null | undefined, nowMs: number): number {
  if (!expiresAt) return 0;
  const t = Date.parse(expiresAt);
  return Number.isFinite(t) ? Math.max(0, Math.ceil((t - nowMs) / 1000)) : 0;
}

/** «14:05» / «0:07» - для видимого відліку (не озвучується live-областю). */
export function countdownLabel(seconds: number): string {
  const s = Math.max(0, Math.floor(seconds));
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}`;
}

export const STATE_LABEL: Record<TelegramLinkState, string> = {
  none: "Не підключено",
  pending: "Очікує підтвердження в боті",
  awaitingConfirmation: "Підтвердьте в Telegram",
  active: "Підключено",
};

/** Повідомлення про зміну стану для live-області (один раз на зміну, без відліку). */
export function stateAnnouncement(state: TelegramLinkState, prev: TelegramLinkState | null): string | null {
  if (prev === null || prev === state) return null;
  switch (state) {
    case "awaitingConfirmation":
      return "Чат отримано. Підтвердьте підключення в Telegram.";
    case "active":
      return "Telegram-сповіщення підключено.";
    case "pending":
      return "Посилання створено. Відкрийте його в Telegram.";
    case "none":
      return prev === "active" ? "Telegram-сповіщення відключено." : "Підключення не завершено.";
  }
}

export const REVOKE_TEXT: Record<RevokeReason, { text: string; show: boolean }> = {
  user_unlink: { text: "Telegram відключено.", show: false },
  bot_stop: { text: "Ви вимкнули сповіщення командою /stop у боті.", show: true },
  declined: { text: "Підключення скасовано в боті.", show: true },
  replaced: { text: "Попереднє посилання замінено новим.", show: false },
  expired: { text: "Термін дії посилання минув. Створіть нове.", show: true },
  bot_blocked: { text: "Бот заблоковано у Telegram — підключіть знову.", show: true },
  chat_not_found: { text: "Чат у Telegram не знайдено — підключіть знову.", show: true },
  user_deactivated: { text: "Ваш обліковий запис було вимкнено, тож сповіщення відключено.", show: true },
  role_changed: { text: "Роль змінилась, тож сповіщення відключено.", show: true },
  tenant_inactive: { text: "Роботу закладу призупинено, тож сповіщення відключено.", show: true },
};

/** Банер причини відкликання (лише `state = none` і лише для причин, про які варто сказати). */
export function revokeBanner(s: Pick<TelegramLinkStatus, "state" | "lastRevoke">): string | null {
  if (s.state !== "none" || !s.lastRevoke) return null;
  const r = REVOKE_TEXT[s.lastRevoke.reason];
  return r?.show ? r.text : null;
}

/** Права (дзеркало backend, не єдина лінія захисту): лише owner і модуль `beauty_inventory`. */
export function canManageTelegramLink(a: { isOwner: boolean; hasModule: (m: string) => boolean }): boolean {
  return a.isOwner && a.hasModule("beauty_inventory");
}

/** Режим картки з урахуванням прапора: вимкнено - без підключення, але «Відключити» лишається за `state ≠ none`. */
export function cardView(s: Pick<TelegramLinkStatus, "featureEnabled" | "state">): { canConnect: boolean; canRenew: boolean; canDisconnect: boolean; disabledNote: boolean } {
  return { canConnect: s.featureEnabled && s.state === "none", canRenew: s.featureEnabled && (s.state === "pending" || s.state === "awaitingConfirmation"), canDisconnect: s.state !== "none", disabledNote: !s.featureEnabled };
}

/** Тексти §32.24.8. */
export const TELEGRAM_BY_CODE: Record<string, string> = {
  telegram_alerts_disabled: "Сповіщення в Telegram поки недоступні",
  forbidden_role: "Лише власник може підключити Telegram-сповіщення",
  telegram_already_linked: "Telegram уже підключено. Спершу відключіть його",
  consent_required: "Підтвердьте згоду на обробку ідентифікатора чату",
  consent_version_outdated: "Текст згоди оновився — перегляньте його ще раз",
  telegram_channel_unavailable: "У салоні немає активного Telegram-бота. Підключіть його в розділі «Канали»",
  telegram_channel_required: "Оберіть бота для сповіщень",
  channel_not_found: "Бота не знайдено",
  telegram_bot_unavailable: "Не вдалося зв’язатися з Telegram. Спробуйте пізніше",
  rate_limited: "Забагато спроб. Спробуйте пізніше",
};

export function humanizeTelegramLinkError(e: unknown): string {
  if (e instanceof BeautyApiError) {
    const t = TELEGRAM_BY_CODE[e.code];
    if (t) return t;
  }
  return humanizeError(e, "Не вдалося виконати дію. Спробуйте ще раз.");
}

/** `extras.channels` з `422 telegram_channel_required`. */
export function channelsFromError(e: unknown): TelegramChannelRef[] {
  if (!(e instanceof BeautyApiError) || e.code !== "telegram_channel_required") return [];
  const raw = e.body?.channels;
  if (!Array.isArray(raw)) return [];
  const out: TelegramChannelRef[] = [];
  for (const c of raw) {
    if (c && typeof c === "object" && typeof (c as { id?: unknown }).id === "string") {
      out.push({ id: (c as { id: string }).id, name: typeof (c as { name?: unknown }).name === "string" ? (c as { name: string }).name : "" });
    }
  }
  return out;
}

/** `422 consent_version_outdated` -> поточна версія (`extras.currentVersion`). */
export function currentVersionFromError(e: unknown): string | null {
  if (!(e instanceof BeautyApiError) || e.code !== "consent_version_outdated") return null;
  const v = e.body?.currentVersion;
  return typeof v === "string" ? v : null;
}
