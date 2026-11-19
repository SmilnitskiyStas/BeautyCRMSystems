import { describe, expect, it } from "vitest";
import { BeautyApiError } from "@/features/beauty-auth/errors";
import {
  REVOKE_TEXT,
  TELEGRAM_BY_CODE,
  canManageTelegramLink,
  cardView,
  channelsFromError,
  consentText,
  countdownLabel,
  currentVersionFromError,
  humanizeTelegramLinkError,
  revokeBanner,
  secondsLeft,
  shouldPoll,
  stateAnnouncement,
  type RevokeReason,
} from "./telegram-link";

const E = (status: number, code: string, body?: Record<string, unknown>) => new BeautyApiError(status, code, code, undefined, undefined, undefined, undefined, undefined, body);
const NOW = Date.parse("2026-11-19T10:00:00Z");

describe("telegram-link: чиста логіка (§32.24.9)", () => {
  it("shouldPoll: лише pending|awaitingConfirmation і не довше expiresAt", () => {
    const future = "2026-11-19T10:10:00Z";
    expect(shouldPoll({ state: "pending", expiresAt: future }, NOW)).toBe(true);
    expect(shouldPoll({ state: "awaitingConfirmation", expiresAt: future }, NOW)).toBe(true);
    expect(shouldPoll({ state: "pending", expiresAt: "2026-11-19T09:59:00Z" }, NOW)).toBe(false);
    expect(shouldPoll({ state: "active", expiresAt: null }, NOW)).toBe(false);
    expect(shouldPoll({ state: "none", expiresAt: null }, NOW)).toBe(false);
    expect(shouldPoll(undefined, NOW)).toBe(false);
  });

  it("відлік: секунди й підпис", () => {
    expect(secondsLeft("2026-11-19T10:15:00Z", NOW)).toBe(900);
    expect(secondsLeft("2026-11-19T09:00:00Z", NOW)).toBe(0);
    expect(secondsLeft(null, NOW)).toBe(0);
    expect(countdownLabel(905)).toBe("15:05");
    expect(countdownLabel(7)).toBe("0:07");
    expect(countdownLabel(-3)).toBe("0:00");
  });

  it("озвучення лише на зміну стану", () => {
    expect(stateAnnouncement("pending", null)).toBeNull();
    expect(stateAnnouncement("pending", "pending")).toBeNull();
    expect(stateAnnouncement("active", "awaitingConfirmation")).toMatch(/підключено/);
    expect(stateAnnouncement("none", "active")).toMatch(/відключено/);
  });

  it("банер причини: тільки при state=none і лише для значущих причин", () => {
    const reasons = Object.keys(REVOKE_TEXT) as RevokeReason[];
    expect(reasons).toHaveLength(10);
    expect(revokeBanner({ state: "none", lastRevoke: { reason: "bot_blocked", at: "x" } })).toBe("Бот заблоковано у Telegram — підключіть знову.");
    expect(revokeBanner({ state: "none", lastRevoke: { reason: "user_unlink", at: "x" } })).toBeNull();
    expect(revokeBanner({ state: "active", lastRevoke: { reason: "bot_blocked", at: "x" } })).toBeNull();
    expect(revokeBanner({ state: "none", lastRevoke: null })).toBeNull();
  });

  it("cardView: прапор вимкнено -> без підключення, але «Відключити» за state != none", () => {
    expect(cardView({ featureEnabled: false, state: "none" })).toEqual({ canConnect: false, canRenew: false, canDisconnect: false, disabledNote: true });
    expect(cardView({ featureEnabled: false, state: "active" })).toMatchObject({ canConnect: false, canDisconnect: true, disabledNote: true });
    expect(cardView({ featureEnabled: true, state: "none" })).toMatchObject({ canConnect: true, canDisconnect: false });
    expect(cardView({ featureEnabled: true, state: "pending" })).toMatchObject({ canRenew: true, canDisconnect: true });
  });

  it("права: лише owner і модуль", () => {
    expect(canManageTelegramLink({ isOwner: true, hasModule: () => true })).toBe(true);
    expect(canManageTelegramLink({ isOwner: false, hasModule: () => true })).toBe(false);
    expect(canManageTelegramLink({ isOwner: true, hasModule: () => false })).toBe(false);
  });

  it("усі коди §32.24.8 мають текст; невідомий -> загальний", () => {
    const codes = [
      "telegram_alerts_disabled",
      "forbidden_role",
      "telegram_already_linked",
      "consent_required",
      "consent_version_outdated",
      "telegram_channel_unavailable",
      "telegram_channel_required",
      "channel_not_found",
      "telegram_bot_unavailable",
      "rate_limited",
    ];
    for (const c of codes) expect(TELEGRAM_BY_CODE[c]).toBeTruthy();
    expect(humanizeTelegramLinkError(E(403, "forbidden_role"))).toBe("Лише власник може підключити Telegram-сповіщення");
    expect(humanizeTelegramLinkError(E(503, "telegram_bot_unavailable"))).toMatch(/Спробуйте пізніше/);
    expect(humanizeTelegramLinkError(E(500, "x"))).toBe("Помилка сервера. Спробуйте пізніше.");
  });

  it("extras: channels і currentVersion", () => {
    const e = E(422, "telegram_channel_required", { channels: [{ id: "a", name: "A" }, { nope: 1 }, { id: "b" }] });
    expect(channelsFromError(e)).toEqual([
      { id: "a", name: "A" },
      { id: "b", name: "" },
    ]);
    expect(channelsFromError(E(422, "other", { channels: [{ id: "a" }] }))).toEqual([]);
    expect(currentVersionFromError(E(422, "consent_version_outdated", { currentVersion: "inv-tg-v2" }))).toBe("inv-tg-v2");
    expect(currentVersionFromError(new Error("x"))).toBeNull();
  });

  it("текст згоди inv-tg-v1: без ПД клієнтів, з назвою бота", () => {
    expect(consentText("Мій бот")).toMatch(/від бота «Мій бот»/);
    expect(consentText(null)).toMatch(/Дані клієнтів у сповіщеннях не надсилаються/);
    expect(consentText(null)).toMatch(/\/stop/);
  });
});
