import { BeautyApiError } from "@/features/beauty-auth/errors";
import { CONSENT_VERSION, type RevokeReason, type TelegramChannelRef, type TelegramLinkCreateRequest, type TelegramLinkIssued, type TelegramLinkState, type TelegramLinkStatus } from "../inventory/telegram-link";

/**
 * Mock етапу 2b (§32.24): прив'язка Telegram власника. Правила контракту: лише owner (`forbidden_role` до розбору тіла),
 * `POST` вимагає згоди з поточною версією, канал обирається (`telegram_channel_required`, якщо їх кілька), `active` -> 409,
 * нове посилання замінює попереднє, `DELETE` ідемпотентний і працює при вимкненому прапорі. Токена немає: `deepLink` фіктивний.
 *
 * Сценарії: `featureEnabled` (прапор), `channels` (0/1/багато), `simulate*` для демо й тестів (бот «приймає» токен / підтверджує /
 * блокується). У браузері `autoAdvance` сам рухає стан: pending -> awaitingConfirmation -> active.
 */

export interface TelegramMockDeps {
  role: () => string;
  channels: () => TelegramChannelRef[];
  /** Прапор backend `Inventory:TelegramOwnerAlertsEnabled`. */
  featureEnabled?: () => boolean;
  /** Модуль `beauty_inventory` увімкнено. */
  moduleEnabled?: () => boolean;
  now?: () => number;
  /** Демо: автоперехід станів через вказані мс (за замовчуванням вимкнено). */
  autoAdvanceMs?: number;
}

const TTL_MS = 15 * 60_000;
const err = (status: number, code: string, body: Record<string, unknown> = {}) => new BeautyApiError(status, code, code, undefined, undefined, undefined, undefined, undefined, body);

export function createMockInventoryV3(deps: TelegramMockDeps) {
  const now = deps.now ?? (() => Date.now());
  const enabled = () => (deps.featureEnabled ? deps.featureEnabled() : true);
  const iso = (ms: number) => new Date(ms).toISOString();

  let state: TelegramLinkState = "none";
  let channel: TelegramChannelRef | null = null;
  let expiresAt: number | null = null;
  let activatedAt: number | null = null;
  let claimedAt: number | null = null;
  let lastDeliveryAt: number | null = null;
  let lastRevoke: { reason: RevokeReason; at: number } | null = null;
  let seq = 0;
  let issuedInHour: number[] = [];
  let timer: ReturnType<typeof setTimeout> | null = null;

  const guard = () => {
    if (deps.moduleEnabled && !deps.moduleEnabled()) throw err(403, "module_disabled");
    if (deps.role() !== "owner") throw err(403, "forbidden_role");
  };
  const clearTimer = () => {
    if (timer) clearTimeout(timer);
    timer = null;
  };
  /** Прострочені issued|claimed -> revoked (expired); як воркер. */
  const sweep = () => {
    if ((state === "pending" || state === "awaitingConfirmation") && expiresAt !== null && now() >= expiresAt) revoke("expired");
  };
  function revoke(reason: RevokeReason) {
    clearTimer();
    state = "none";
    channel = null;
    expiresAt = null;
    activatedAt = null;
    claimedAt = null;
    lastRevoke = { reason, at: now() };
  }
  const advance = () => {
    if (!deps.autoAdvanceMs) return;
    clearTimer();
    timer = setTimeout(() => {
      if (state === "pending") {
        simulateClaim();
        advance();
      } else if (state === "awaitingConfirmation") simulateConfirm();
    }, deps.autoAdvanceMs);
  };

  function simulateClaim() {
    if (state === "pending") {
      state = "awaitingConfirmation";
      claimedAt = now();
    }
  }
  function simulateConfirm() {
    if (state === "awaitingConfirmation") {
      state = "active";
      activatedAt = now();
      expiresAt = null;
      lastRevoke = null;
    }
  }

  const status = (): TelegramLinkStatus => {
    sweep();
    const recent = lastRevoke && now() - lastRevoke.at < 30 * 86_400_000 ? lastRevoke : null;
    return {
      featureEnabled: enabled(),
      state,
      channel,
      expiresAt: expiresAt !== null ? iso(expiresAt) : null,
      activatedAt: activatedAt !== null ? iso(activatedAt) : null,
      claimedAt: claimedAt !== null ? iso(claimedAt) : null,
      lastDeliveryAt: lastDeliveryAt !== null ? iso(lastDeliveryAt) : null,
      lastRevoke: state === "none" && recent ? { reason: recent.reason, at: iso(recent.at) } : null,
      consentVersion: CONSENT_VERSION,
    };
  };

  return {
    async getTelegramLink(): Promise<TelegramLinkStatus> {
      guard();
      return status();
    },

    async createTelegramLink(r: TelegramLinkCreateRequest): Promise<TelegramLinkIssued> {
      guard();
      if (!enabled()) throw err(403, "telegram_alerts_disabled");
      sweep();
      if (r.consent !== true) throw err(422, "consent_required");
      if (r.consentVersion !== CONSENT_VERSION) throw err(422, "consent_version_outdated", { currentVersion: CONSENT_VERSION });
      if (state === "active") throw err(409, "telegram_already_linked");
      const t = now();
      issuedInHour = issuedInHour.filter((x) => t - x < 3_600_000);
      if (issuedInHour.length >= 5) throw err(429, "rate_limited");
      const all = deps.channels();
      let picked: TelegramChannelRef | undefined;
      if (r.channelId) {
        picked = all.find((c) => c.id === r.channelId);
        if (!picked) throw err(404, "channel_not_found");
      } else if (all.length === 0) throw err(422, "telegram_channel_unavailable");
      else if (all.length > 1) throw err(422, "telegram_channel_required", { channels: all });
      else picked = all[0];
      issuedInHour.push(t);
      seq += 1;
      state = "pending";
      channel = picked ?? null;
      expiresAt = t + TTL_MS;
      advance();
      return { linkId: `mock-link-${seq}`, deepLink: `https://t.me/mock_beauty_bot?start=inv_MOCK${String(seq).padStart(39, "0")}`, expiresAt: iso(expiresAt) };
    },

    async deleteTelegramLink(): Promise<void> {
      guard();
      if (state !== "none") revoke("user_unlink");
      lastRevoke = null;
    },

    /** Сценарії для демо й тестів (не частина `BeautyAdminApi`). */
    telegramScenario: {
      simulateClaim,
      simulateConfirm,
      simulateRevoke: (reason: RevokeReason) => {
        if (state !== "none") revoke(reason);
      },
      simulateDelivery: () => {
        if (state === "active") lastDeliveryAt = now();
      },
    },
  };
}
