import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { ChannelSendError, TELEGRAM_API, sendTelegramText } from "../adapters/channel-sender";
import { protect, unprotect } from "../adapters/channel-crypto";
import { alertText, alertsUrl, cleanName, digestText, fmtNum } from "../jobs/inventory-telegram-texts";
import { isQuietHour, loadInventoryTelegramConfig } from "./telegram";

describe("loadInventoryTelegramConfig", () => {
  it("defaults 21..08, no link", () => {
    expect(loadInventoryTelegramConfig({})).toEqual({ quietStart: 21, quietEnd: 8 });
  });
  it("parses values and trims the base url", () => {
    expect(loadInventoryTelegramConfig({ WORKER_INVENTORY_TELEGRAM_QUIET_START: "22", WORKER_INVENTORY_TELEGRAM_QUIET_END: "7", WORKER_ADMIN_BASE_URL: "https://crm.example.com/" }))
      .toEqual({ quietStart: 22, quietEnd: 7, adminBaseUrl: "https://crm.example.com" });
  });
  it.each([
    [{ WORKER_INVENTORY_TELEGRAM_QUIET_START: "24" }],
    [{ WORKER_INVENTORY_TELEGRAM_QUIET_END: "x" }],
    [{ WORKER_INVENTORY_TELEGRAM_QUIET_START: "8" }], // == default END
    [{ WORKER_ADMIN_BASE_URL: "http://crm.example.com" }],
  ])("rejects %j", (env) => { expect(() => loadInventoryTelegramConfig(env as never)).toThrow(); });
  it("allows http only for localhost", () => {
    expect(loadInventoryTelegramConfig({ WORKER_ADMIN_BASE_URL: "http://localhost:3000" }).adminBaseUrl).toBe("http://localhost:3000");
  });
});

describe("isQuietHour", () => {
  it("wraps midnight for 21..08", () => {
    for (const h of [21, 22, 23, 0, 7]) expect(isQuietHour(h, 21, 8)).toBe(true);
    for (const h of [8, 12, 20]) expect(isQuietHour(h, 21, 8)).toBe(false);
  });
  it("same-day window", () => {
    expect(isQuietHour(2, 1, 5)).toBe(true);
    expect(isQuietHour(5, 1, 5)).toBe(false);
  });
});

describe("texts (§32.24.5)", () => {
  const base = { location: "Салон", product: "Гель", unit: "мл", quantity: "2.500", threshold: "10.000", lot: null, expiresOn: null, daysBucket: null } as const;
  it("low_stock", () => {
    expect(alertText({ ...base, kind: "low_stock" }, alertsUrl("https://a.b", "L1")))
      .toBe("Склад · Салон\nМало: Гель — залишок 2,5 мл (мінімум 10).\nhttps://a.b/beauty/inventory/alerts?locationId=L1");
  });
  it("expiring and expired, lot fallback, no link without base", () => {
    expect(alertText({ ...base, kind: "expiring", expiresOn: "2026-12-05", daysBucket: 30 }, "")).toBe("Склад · Салон\nЗакінчується термін: Гель, партія без номера — 2,5 мл, до 05.12.2026.");
    expect(alertText({ ...base, kind: "expiring", lot: "A-1", expiresOn: "2026-11-01", daysBucket: 0 }, "")).toContain("Прострочено: Гель, партія A-1");
  });
  it("shortage", () => {
    expect(alertText({ ...base, kind: "shortage" }, "")).toContain("Нестача при списанні на послугу: Гель — бракує 2,5 мл.");
  });
  it("cleanName strips control chars and truncates to 80", () => {
    expect(cleanName("a\u0000b\nc‮d")).toBe("a b c d");
    expect([...cleanName("x".repeat(200))]).toHaveLength(80);
    expect(fmtNum("3.000")).toBe("3");
  });
  it("I2: cleanName neutralizes URLs, mentions, commands and t.me in names, keeping them readable", () => {
    const strip = (s: string) => s.replace(/​/g, "");
    const inputs = ["https://evil.example/x", "@owner_bot тест", "/start inv_abc", "див. t.me/evil", "cool.com"];
    for (const i of inputs) {
      const out = cleanName(i);
      expect(strip(out)).toBe(i);
      expect(out).not.toMatch(/:\/\//);
      expect(out).not.toMatch(/@[\p{L}\p{N}_]/u);
      expect(out).not.toMatch(/(^|\s)\/[^\s​]/);
      expect(out).not.toMatch(/t\.me/i);
      expect(out).not.toMatch(/\p{L}\.\p{L}/u);
    }
    expect(cleanName("Крем 50 мл. Новий")).toBe("Крем 50 мл. Новий");
  });
  it("digest lists venues, caps at 30 rows", () => {
    const rows = Array.from({ length: 32 }, (_, i) => ({ location: `Z${String(i).padStart(2, "0")}`, low: 1, expiring: 0, shortage: 0 }));
    const t = digestText(32, rows, "");
    expect(t.startsWith("Склад: 32 нових сповіщень\n• Z00: мало — 1, термін — 0, нестача — 0")).toBe(true);
    expect(t.endsWith("і ще 2")).toBe(true);
  });
});

describe("sendTelegramText (shared transport)", () => {
  const ok = () => new Response(JSON.stringify({ ok: true, result: { message_id: 42 } }), { status: 200 });
  it("client path unchanged: no preview flag, optional markup", async () => {
    let body: Record<string, unknown> = {};
    const http = (async (_u: string, init: RequestInit) => { body = JSON.parse(String(init.body)); return ok(); }) as unknown as typeof fetch;
    const r = await sendTelegramText(http, TELEGRAM_API, "T", "7", "hi");
    expect(r.externalMessageId).toBe("42");
    expect(body).toEqual({ chat_id: "7", text: "hi" });
    await sendTelegramText(http, TELEGRAM_API, "T", "7", "hi", { disablePreview: true });
    expect(body).toEqual({ chat_id: "7", text: "hi", disable_web_page_preview: true });
  });
  it("maps 403 / chat not found / 429 / 5xx / network without leaking the token", async () => {
    const mk = (status: number, text = "") => (async () => new Response(text, { status })) as unknown as typeof fetch;
    const err = async (http: typeof fetch): Promise<ChannelSendError> => (await sendTelegramText(http, TELEGRAM_API, "SECRETTOKEN", "7", "x").then(() => null, (e: unknown) => e)) as ChannelSendError;
    expect(await err(mk(403))).toMatchObject({ code: "bot_blocked", permanent: true });
    expect(await err(mk(400, '{"description":"Bad Request: chat not found"}'))).toMatchObject({ code: "chat_not_found" });
    expect(await err(mk(400, "other"))).toMatchObject({ permanent: true, code: undefined });
    const r429 = await err(mk(429, '{"parameters":{"retry_after":17}}'));
    expect(r429).toMatchObject({ permanent: false }); expect(r429.extra.retryAfterSec).toBe(17);
    expect(await err(mk(502))).toMatchObject({ permanent: false });
    const net = await err((async () => { throw new Error("boom SECRETTOKEN"); }) as unknown as typeof fetch);
    expect(net).toMatchObject({ permanent: false });
    expect(net.message).not.toContain("SECRETTOKEN");
  });
});

describe("chat_id secret format", () => {
  it("raw string round-trips through the credentials cipher (ISecretProtector layout, not JSON)", () => {
    const key = Buffer.alloc(32, 7);
    expect(unprotect(key, protect(key, "-1001234567890"))).toBe("-1001234567890");
  });
});

// TASK-897 (a): ciphertexts produced by the real .NET AesGcmSecretProtector.Protect(chatId) (backend StaffChatSecretVectorTests).
const chatVector = JSON.parse(readFileSync(new URL("../../../.ai/artifacts/contracts/ADR-025-chat-id-secret-vector.json", import.meta.url), "utf8")) as
  { keyBase64: string; cases: { chatId: string; protectedValue: string }[] };

describe("backend -> worker chat_id compatibility (.NET vector)", () => {
  it("worker unprotect decrypts what .NET ISecretProtector.Protect produced, incl. negative supergroup ids", () => {
    const key = Buffer.from(chatVector.keyBase64, "base64");
    expect(key).toHaveLength(32);
    expect(chatVector.cases.length).toBeGreaterThanOrEqual(3);
    for (const c of chatVector.cases) expect(unprotect(key, c.protectedValue)).toBe(c.chatId);
  });
  it("a wrong key or a tampered vector fails closed (GCM tag)", () => {
    const c = chatVector.cases[0]!;
    expect(() => unprotect(Buffer.alloc(32, 1), c.protectedValue)).toThrow();
    const raw = Buffer.from(c.protectedValue, "base64"); raw[raw.length - 1]! ^= 1;
    expect(() => unprotect(Buffer.from(chatVector.keyBase64, "base64"), raw.toString("base64"))).toThrow();
  });
});
