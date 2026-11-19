import { BeautyApiError, humanizeError } from "@/features/beauty-auth/errors";
import type { AiCatalogModel, AiMode, AiRoleId, AiRoleStatus } from "../types";

/** Резервні назви сценаріїв; основні приходять з `GET /ai/models`.roles. */
export const FALLBACK_ROLE_LABEL: Record<AiRoleId, string> = {
  chat: "Спілкування з клієнтами",
  analytics: "Аналітика й рекомендації",
};

export const MODE_VIEW: Record<AiMode, { label: string; hint: string }> = {
  suggest: { label: "Лише пропонує", hint: "ШІ готує варіанти відповідей і дій, але нічого не виконує. Усе робите ви." },
  confirm: { label: "Готує, я підтверджую", hint: "ШІ готує чернетки записів і відповідей, а ви підтверджуєте кожну дію. Рекомендовано." },
  auto: { label: "Діє сам", hint: "ШІ сам виконує дії без вашого підтвердження: створює записи й надсилає відповіді. Ризикований режим." },
};

export const MODES: AiMode[] = ["suggest", "confirm", "auto"];

/** Місяць, що настає після поточного (UTC): «до 1 листопада». */
export function nextMonthLabel(now = new Date()): string {
  const first = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth() + 1, 1));
  return first.toLocaleDateString("uk-UA", { day: "numeric", month: "long", timeZone: "UTC" });
}

/** Текст стану сценарію за `status.{role}.reason` (§20.3, §20.9). */
export function statusReasonText(reason: string | undefined, providerLabel = "провайдера", now = new Date()): string {
  switch (reason) {
    case "own_key_missing":
      return `Додайте ключ ${providerLabel} для цього сценарію.`;
    case "own_key_unreadable":
      return "Збережений ключ не вдалося прочитати. Додайте його знову.";
    case "secrets_unavailable":
      return "Сховище ключів на сервері недоступне. Зверніться до підтримки.";
    case "provider_not_available":
      return "Цей провайдер ще не підтримується. Оберіть іншого провайдера.";
    case "platform_disabled":
      return "Ключ платформи недоступний для вашої компанії. Додайте власний ключ або зверніться до підтримки.";
    case "platform_key_missing":
      return "Ключ платформи не налаштовано. Додайте власний ключ або зверніться до підтримки.";
    case "quota_exceeded":
      return `Місячний ліміт вичерпано до ${nextMonthLabel(now)}.`;
    default:
      return "ШІ зараз недоступний.";
  }
}

/** Загальний стан (за `status.chat`). */
export function overallStatusText(s: AiRoleStatus, now = new Date()): string {
  if (s.state === "ready") return "ШІ працює.";
  if (s.reason === "own_key_missing") return "ШІ вимкнено: немає ключа. Додайте ключ у налаштуваннях ШІ.";
  return `ШІ вимкнено. ${statusReasonText(s.reason, "провайдера", now)}`;
}

const TEST_REASON: Record<string, string> = {
  key_rejected: "Провайдер відхилив ключ.",
  insufficient_permissions: "У ключа недостатньо прав для цієї моделі.",
  provider_rate_limited: "Провайдер обмежив частоту запитів. Спробуйте за хвилину.",
  provider_unavailable: "Провайдер тимчасово недоступний. Спробуйте пізніше.",
  model_unavailable: "Модель недоступна для цього ключа.",
  no_key: "Ключа немає. Додайте його.",
};
export const testReasonText = (reason?: string) => (reason ? (TEST_REASON[reason] ?? "Ключ не пройшов перевірку.") : "Ключ не пройшов перевірку.");

const trim = (n: number) => Number(n.toFixed(2)).toString().replace(".", ",");

/** «≈ $0,8 / 1M вхідних · $4 / 1M вихідних токенів, ціни на 24.10.2026» або «Ціну не вказано». Ціни лише з каталогу API. */
export function priceText(m: Pick<AiCatalogModel, "inputPricePerMTok" | "outputPricePerMTok" | "currency" | "priceUpdatedAt">): string {
  if (m.inputPricePerMTok === null || m.outputPricePerMTok === null) return "Ціну не вказано";
  const sym = m.currency === "USD" ? "$" : "";
  const cur = m.currency === "USD" ? "" : ` ${m.currency}`;
  const date = m.priceUpdatedAt ? `, ціни на ${dmy(m.priceUpdatedAt)}` : "";
  return `≈ ${sym}${trim(m.inputPricePerMTok)}${cur} / 1M вхідних · ${sym}${trim(m.outputPricePerMTok)}${cur} / 1M вихідних токенів${date}`;
}

/** `2026-10-24T…` -> `24.10.2026` (UTC-дата з рядка, без зсувів зони). */
export function dmy(iso: string): string {
  const m = /^(\d{4})-(\d{2})-(\d{2})/.exec(iso);
  return m ? `${m[3]}.${m[2]}.${m[1]}` : iso;
}

const MONTH_IN = ["січні", "лютому", "березні", "квітні", "травні", "червні", "липні", "серпні", "вересні", "жовтні", "листопаді", "грудні"];
/** `2026-10-01` -> «жовтні 2026». */
export function monthIn(iso: string): string {
  const m = /^(\d{4})-(\d{2})/.exec(iso);
  return m ? `${MONTH_IN[Number(m[2]) - 1] ?? m[2]} ${m[1]}` : iso;
}

export const nf = (n: number) => n.toLocaleString("uk-UA").replace(/ /g, " ");

/** Текст помилки для UI. Тіло відповіді сервера не показуємо; 429 - з очікуванням, якщо його повідомлено. */
export function aiErrorText(e: unknown, fallback = "Не вдалося виконати дію. Спробуйте ще раз."): string {
  if (e instanceof BeautyApiError && e.status === 429) {
    const base = e.code === "ai_quota_exceeded" ? "Місячний ліміт ШІ вичерпано." : "Забагато спроб.";
    if (e.retryAfterSeconds) {
      const min = Math.ceil(e.retryAfterSeconds / 60);
      return `${base} Спробуйте ще раз приблизно через ${min} хв.`;
    }
    return `${base} Зачекайте трохи й спробуйте ще раз.`;
  }
  return humanizeError(e, fallback);
}

export const errorCode = (e: unknown) => (e instanceof BeautyApiError ? e.code : "");
