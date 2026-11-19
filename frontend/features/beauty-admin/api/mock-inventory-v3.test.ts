import { describe, expect, it } from "vitest";
import { BeautyApiError } from "@/features/beauty-auth/errors";
import { createMockInventoryV3 } from "./mock-inventory-v3";
import { toTelegramLinkStatus } from "./http-inventory";
import { CONSENT_VERSION } from "../inventory/telegram-link";

const ONE = [{ id: "c1", name: "Бот салону" }];
const TWO = [...ONE, { id: "c2", name: "Бот барбершопу" }];

function setup(o: { role?: string; channels?: { id: string; name: string }[]; enabled?: boolean } = {}) {
  const t = { now: Date.parse("2026-11-19T10:00:00Z"), role: o.role ?? "owner", enabled: o.enabled ?? true, channels: o.channels ?? ONE };
  const m = createMockInventoryV3({ role: () => t.role, channels: () => t.channels, featureEnabled: () => t.enabled, now: () => t.now });
  return { m, t };
}
async function code(p: Promise<unknown>) {
  const e = await p.then(
    () => null,
    (x: unknown) => x,
  );
  expect(e).toBeInstanceOf(BeautyApiError);
  return `${(e as BeautyApiError).status} ${(e as BeautyApiError).code}`;
}
const REQ = { consent: true as const, consentVersion: CONSENT_VERSION };

describe("mock §32.24: прив'язка Telegram", () => {
  it("none -> pending -> awaitingConfirmation -> active; відповідь без токена в статусі", async () => {
    const { m } = setup();
    expect((await m.getTelegramLink()).state).toBe("none");
    const r = await m.createTelegramLink(REQ);
    expect(r.deepLink).toMatch(/^https:\/\/t\.me\//);
    const s = await m.getTelegramLink();
    expect(s.state).toBe("pending");
    expect(JSON.stringify(s)).not.toContain("inv_");
    expect(s.expiresAt).toBe("2026-11-19T10:15:00.000Z");
    m.telegramScenario.simulateClaim();
    expect((await m.getTelegramLink()).state).toBe("awaitingConfirmation");
    m.telegramScenario.simulateConfirm();
    const a = await m.getTelegramLink();
    expect(a.state).toBe("active");
    expect(a.activatedAt).not.toBeNull();
    expect(a.expiresAt).toBeNull();
  });

  it("помилки POST за контрактом", async () => {
    const { m, t } = setup();
    expect(await code(m.createTelegramLink({ ...REQ, consent: false as unknown as true }))).toBe("422 consent_required");
    expect(await code(m.createTelegramLink({ ...REQ, consentVersion: "inv-tg-v0" }))).toBe("422 consent_version_outdated");
    t.channels = [];
    expect(await code(m.createTelegramLink(REQ))).toBe("422 telegram_channel_unavailable");
    t.channels = TWO;
    const e = await m.createTelegramLink(REQ).catch((x: BeautyApiError) => x);
    expect((e as BeautyApiError).code).toBe("telegram_channel_required");
    expect((e as BeautyApiError).body?.channels).toEqual(TWO);
    expect(await code(m.createTelegramLink({ ...REQ, channelId: "zzz" }))).toBe("404 channel_not_found");
    await m.createTelegramLink({ ...REQ, channelId: "c2" });
    m.telegramScenario.simulateClaim();
    m.telegramScenario.simulateConfirm();
    expect(await code(m.createTelegramLink({ ...REQ, channelId: "c2" }))).toBe("409 telegram_already_linked");
  });

  it("прапор вимкнено: GET featureEnabled=false, POST 403, DELETE працює", async () => {
    const { m, t } = setup();
    await m.createTelegramLink(REQ);
    t.enabled = false;
    expect((await m.getTelegramLink()).featureEnabled).toBe(false);
    expect(await code(m.createTelegramLink(REQ))).toBe("403 telegram_alerts_disabled");
    await m.deleteTelegramLink();
    expect((await m.getTelegramLink()).state).toBe("none");
  });

  it("лише owner: forbidden_role до розбору тіла", async () => {
    for (const role of ["admin", "deputy", "specialist"]) {
      const { m } = setup({ role });
      expect(await code(m.getTelegramLink())).toBe("403 forbidden_role");
      expect(await code(m.createTelegramLink(REQ))).toBe("403 forbidden_role");
      expect(await code(m.deleteTelegramLink())).toBe("403 forbidden_role");
    }
  });

  it("ліміт 5/год -> 429; TTL минув -> revoked(expired); bot_blocked показується як lastRevoke", async () => {
    const { m, t } = setup();
    for (let i = 0; i < 5; i++) await m.createTelegramLink(REQ);
    expect(await code(m.createTelegramLink(REQ))).toBe("429 rate_limited");
    t.now += 16 * 60_000;
    const s = await m.getTelegramLink();
    expect(s.state).toBe("none");
    expect(s.lastRevoke?.reason).toBe("expired");
    t.now += 3_600_000; // вікно ліміту минуло
    await m.createTelegramLink(REQ);
    m.telegramScenario.simulateClaim();
    m.telegramScenario.simulateConfirm();
    m.telegramScenario.simulateRevoke("bot_blocked");
    const b = await m.getTelegramLink();
    expect(b.state).toBe("none");
    expect(b.lastRevoke?.reason).toBe("bot_blocked");
  });

  it("DELETE ідемпотентний і не лишає lastRevoke", async () => {
    const { m } = setup();
    await m.deleteTelegramLink();
    await m.createTelegramLink(REQ);
    await m.deleteTelegramLink();
    await m.deleteTelegramLink();
    expect((await m.getTelegramLink()).lastRevoke).toBeNull();
  });
});

describe("http: toTelegramLinkStatus", () => {
  it("явна вибірка полів, невідомі значення -> безпечні дефолти, зайве відкидається", () => {
    const s = toTelegramLinkStatus({
      featureEnabled: true,
      state: "weird",
      channel: { id: "c", name: "Бот", chat_id: 42 },
      chatId: 42,
      token: "secret",
      lastRevoke: { reason: "nope", at: "2026-11-19T10:00:00Z" },
    });
    expect(s.state).toBe("none");
    expect(s.lastRevoke).toBeNull();
    expect(s.channel).toEqual({ id: "c", name: "Бот" });
    expect(JSON.stringify(s)).not.toMatch(/secret|42/);
    expect(s.consentVersion).toBe("inv-tg-v1");
    expect(toTelegramLinkStatus({ state: "active", lastRevoke: { reason: "bot_stop", at: "x" } }).lastRevoke).toEqual({ reason: "bot_stop", at: "x" });
  });
});
