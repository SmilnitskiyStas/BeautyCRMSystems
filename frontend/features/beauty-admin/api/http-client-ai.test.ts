import { beforeEach, describe, expect, it, vi } from "vitest";

type Init = { method?: string; body?: string; cache?: string };
const apiJson = vi.fn<(path: string, init?: Init) => Promise<unknown>>();
vi.mock("@/features/beauty-auth/session", () => ({
  apiJson: (path: string, init?: Init) => apiJson(path, init),
  getUser: () => ({ id: "u", role: "owner", specialistId: null }),
}));

import { httpBeautyApi } from "./http-client";

const KEY = `sk-ant-${"q".repeat(24)}1234`;
const settingsDto = {
  keySource: "own",
  chat: { provider: "anthropic", model: "claude-haiku-5-5", tier: "economy", modelFallback: false },
  analytics: { provider: "anthropic", model: "claude-sonnet-5-5", tier: "balanced", modelFallback: false },
  mode: "confirm",
  promptAddendum: "Текст",
  isDefaultPrompt: false,
  defaultPromptAddendum: "Типовий",
  promptAddendumMaxLength: 4000,
  limits: { monthlyTokenLimit: null, monthlyRequestLimit: null, maxOutputTokensPerRequest: 1024 },
  platformQuota: { enabled: false, monthlyTokens: null, monthlyRequests: null },
  status: { chat: { state: "ready" }, analytics: { state: "ready" } },
  keys: [{ provider: "anthropic", hasKey: true, last4: "1234", updatedAt: "2026-10-20T10:00:00Z", updatedByName: "Власник", usedByRoles: ["chat", "analytics"], apiKey: KEY, keyEncrypted: "v2:x" }],
  updatedAt: null,
  updatedByName: null,
};
let calls: { path: string; method?: string; body?: string; cache?: string }[] = [];

beforeEach(() => {
  calls = [];
  apiJson.mockReset().mockImplementation(async (path, init) => {
    calls.push({ path, method: init?.method, body: init?.body, cache: init?.cache });
    if (path.includes("/test")) return { ok: true, provider: "anthropic", model: "claude-haiku-5-5", checkedAt: "2026-10-24T10:00:00Z", secret: KEY };
    if (path.includes("/keys/") && init?.method === "PUT")
      return { provider: "anthropic", hasKey: true, last4: "1234", updatedAt: "2026-10-24T10:00:00Z", keySource: "own", usedByRoles: ["chat"], apiKey: KEY };
    if (path.includes("/keys/") && init?.method === "DELETE") return undefined;
    if (path.includes("/ai/models")) return { roles: [], providers: [], models: [] };
    if (path.includes("/ai/usage")) return { keySource: "own" };
    return { ...settingsDto, warnings: [{ code: "personal_data", message: "m" }] };
  });
});

describe("§20.5: реальний клієнт ШІ", () => {
  it("GET settings/models/usage: no-store, місяці обмежені 1..12", async () => {
    await httpBeautyApi.getAiSettings();
    await httpBeautyApi.getAiModels();
    await httpBeautyApi.getAiUsage(99);
    await httpBeautyApi.getAiUsage(0);
    expect(calls.map((c) => c.path)).toEqual(["/api/beauty/ai/settings", "/api/beauty/ai/models", "/api/beauty/ai/usage?months=12", "/api/beauty/ai/usage?months=1"]);
    expect(calls.every((c) => c.cache === "no-store")).toBe(true);
  });

  it("відповідь settings: секретів немає (лише explicit-поля), для admin keys відсутній", async () => {
    const s = await httpBeautyApi.getAiSettings();
    expect(JSON.stringify(s)).not.toContain(KEY);
    expect(JSON.stringify(s)).not.toContain("keyEncrypted");
    expect(s.keys?.[0]).toEqual({ provider: "anthropic", hasKey: true, last4: "1234", updatedAt: "2026-10-20T10:00:00Z", updatedByName: "Власник", usedByRoles: ["chat", "analytics"] });
    apiJson.mockResolvedValueOnce({ ...settingsDto, keys: undefined });
    expect("keys" in (await httpBeautyApi.getAiSettings())).toBe(false);
  });

  it("PUT settings: тіло як у контракті, warnings повертаються", async () => {
    const input = {
      chat: { provider: "anthropic", model: "m1" },
      analytics: { provider: "anthropic", model: "m2" },
      mode: "auto" as const,
      promptAddendum: null,
      limits: { monthlyTokenLimit: 10, monthlyRequestLimit: null, maxOutputTokensPerRequest: 512 },
      acknowledgeAutoMode: true,
    };
    const r = await httpBeautyApi.saveAiSettings(input);
    expect(calls[0]).toMatchObject({ path: "/api/beauty/ai/settings", method: "PUT" });
    expect(JSON.parse(calls[0]!.body!)).toEqual(input);
    expect(r.warnings).toEqual([{ code: "personal_data", message: "m" }]);
    expect(r.settings.mode).toBe("confirm");
  });

  it("PUT key-source", async () => {
    await httpBeautyApi.setAiKeySource("platform");
    expect(calls[0]).toMatchObject({ path: "/api/beauty/ai/settings/key-source", method: "PUT" });
    expect(JSON.parse(calls[0]!.body!)).toEqual({ keySource: "platform" });
  });

  it("ключ лише в тілі PUT: у шляху/query його немає; ack лише коли true; відповідь без секретів", async () => {
    const r = await httpBeautyApi.saveAiKey({ provider: "anthropic", apiKey: KEY, forRole: "chat" });
    expect(calls[0]!.path).toBe("/api/beauty/ai/settings/keys/anthropic");
    expect(calls[0]!.path).not.toContain(KEY);
    expect(JSON.parse(calls[0]!.body!)).toEqual({ apiKey: KEY, forRole: "chat", verify: true });
    expect(JSON.stringify(r)).not.toContain(KEY);
    expect(r).toEqual({ provider: "anthropic", hasKey: true, last4: "1234", updatedAt: "2026-10-24T10:00:00Z", keySource: "own", usedByRoles: ["chat"] });
    await httpBeautyApi.saveAiKey({ provider: "anthropic", apiKey: KEY, forRole: "analytics", acknowledgeAffectedRoles: true });
    expect(JSON.parse(calls[1]!.body!)).toMatchObject({ forRole: "analytics", acknowledgeAffectedRoles: true });
  });

  it("DELETE ключа: forRole і acknowledgeAffectedRoles у query, без тіла", async () => {
    await httpBeautyApi.deleteAiKey({ provider: "anthropic", forRole: "chat" });
    await httpBeautyApi.deleteAiKey({ provider: "anthropic", forRole: "chat", acknowledgeAffectedRoles: true });
    expect(calls[0]).toMatchObject({ path: "/api/beauty/ai/settings/keys/anthropic?forRole=chat", method: "DELETE", body: undefined });
    expect(calls[1]!.path).toBe("/api/beauty/ai/settings/keys/anthropic?forRole=chat&acknowledgeAffectedRoles=true");
  });

  it("POST test: без ключа порожнє тіло; з кандидатом ключ у тілі; відповідь без зайвих полів", async () => {
    await httpBeautyApi.testAiKey("anthropic");
    expect(JSON.parse(calls[0]!.body!)).toEqual({});
    const r = await httpBeautyApi.testAiKey("anthropic", KEY);
    expect(calls[1]!.path).toBe("/api/beauty/ai/settings/keys/anthropic/test");
    expect(JSON.parse(calls[1]!.body!)).toEqual({ apiKey: KEY });
    expect(JSON.stringify(r)).not.toContain(KEY);
  });
});
