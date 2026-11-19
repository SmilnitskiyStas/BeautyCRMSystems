import { BeautyApiError } from "@/features/beauty-auth/errors";
import { normalizePrompt, promptLength } from "../ai-settings/prompt";
import type {
  AiCatalog,
  AiCatalogModel,
  AiKeyInfo,
  AiKeySource,
  AiLimits,
  AiMode,
  AiRoleId,
  AiRoleStatus,
  AiSettings,
  AiSettingsInput,
  AiUsage,
  AiWarning,
} from "../types";
import type { BeautyAdminApi } from "./client";

/**
 * In-memory mock налаштувань ШІ за контрактом §20.5. Відтворює коди помилок (403, 409 key_affects_other_roles,
 * 422 provider_not_available / prompt_addendum_*, 429, 502) і НЕ зберігає повний ключ: лише `last4`.
 * Ціни й id моделей нижче - демо-значення цього mock; у компонентах frontend цін і id моделей немає.
 */

const ROLES: AiRoleId[] = ["chat", "analytics"];
const LATENCY_MS = 120;
const KEY_RATE_LIMIT = { permits: 5, windowSeconds: 600 };
const MAX_PROMPT = 4000;

export const MOCK_DEFAULT_PROMPT = [
  "Тон: дружній і професійний, звертайся до клієнта на «ви».",
  "Уточнюй послугу, бажаний день і майстра; пропонуй найближчі вільні слоти.",
  "Не обіцяй знижок і цін, яких немає в активних акціях і прайсі.",
  "На медичні питання й протипоказання радь консультацію майстра.",
].join("\n");

export const MOCK_AI_CATALOG: AiCatalog = {
  roles: [
    { id: "chat", label: "Спілкування з клієнтами" },
    { id: "analytics", label: "Аналітика й рекомендації", inUse: false },
  ],
  providers: [
    { id: "anthropic", label: "Anthropic", available: true, availabilityLabel: null },
    { id: "openai", label: "OpenAI", available: false, availabilityLabel: "Скоро" },
  ],
  models: [
    { id: "claude-haiku-5-5", provider: "anthropic", available: true, tier: "economy", tierLabel: "Економна", costRank: 1, costLabel: "дешевша", inputPricePerMTok: 1, outputPricePerMTok: 5, currency: "USD", priceUpdatedAt: "2026-10-24", isDefaultFor: ["chat"] },
    { id: "claude-sonnet-5-5", provider: "anthropic", available: true, tier: "balanced", tierLabel: "Збалансована", costRank: 2, costLabel: "середня", inputPricePerMTok: 3, outputPricePerMTok: 15, currency: "USD", priceUpdatedAt: "2026-10-24", isDefaultFor: ["analytics"] },
    { id: "claude-opus-5-5", provider: "anthropic", available: true, tier: "smartest", tierLabel: "Найрозумніша", costRank: 3, costLabel: "дорожча", inputPricePerMTok: 15, outputPricePerMTok: 75, currency: "USD", priceUpdatedAt: "2026-10-24", isDefaultFor: [] },
  ],
};

const err = (status: number, code: string, retry?: number, affected?: string[]) =>
  new BeautyApiError(status, code, code, retry, undefined, affected);

const FORBIDDEN = /<\/?\s*(client_message|tenant_instructions|system)\b/i;
const SECRET = /sk-ant-[\w-]{10,}|\bsk-[\w-]{20,}/;
const KEY_FORMAT = /^[\x21-\x7e]{20,512}$/;

function warningsFor(text: string): AiWarning[] {
  const out: AiWarning[] = [];
  if (/(ігноруй|ignore)\s+(усі\s+|всі\s+|попередні|previous)|ти\s+тепер/i.test(text))
    out.push({ code: "override_attempt", message: "Текст схожий на спробу змінити правила ШІ. Правила безпеки залишаються незмінними." });
  if (/(режим|знижк|розсилк)/i.test(text))
    out.push({ code: "code_enforced_setting", message: "Режим, знижки й години розсилок задає система: додаток їх не змінить." });
  if (/\+?\d[\d\s()-]{8,}\d|[\w.+-]+@[\w-]+\.[\w.]+/.test(text))
    out.push({ code: "personal_data", message: "У тексті схоже на телефон або email. Не вставляйте персональні дані клієнтів." });
  return out;
}

interface State {
  keySource: AiKeySource;
  chat: { provider: string; model: string };
  analytics: { provider: string; model: string };
  mode: AiMode;
  prompt: string | null;
  limits: AiLimits;
  platformEnabled: boolean;
  platformTokens: number | null;
  platformRequests: number | null;
  /** Лише останні 4 символи; повного ключа mock не зберігає. */
  keys: Map<string, { last4: string; updatedAt: string }>;
  updatedAt: string | null;
}

export interface MockAiOptions {
  role: () => string;
  now?: () => Date;
  catalog?: AiCatalog;
  /** Токени, використані цього місяця (для quota_exceeded). */
  usedTokens?: number;
  wait?: boolean;
}

type AiApi = Pick<
  BeautyAdminApi,
  "getAiSettings" | "saveAiSettings" | "setAiKeySource" | "getAiModels" | "saveAiKey" | "deleteAiKey" | "testAiKey" | "getAiUsage"
>;

export function createMockAi(opts: MockAiOptions): AiApi & { __state: () => State } {
  const catalog = opts.catalog ?? MOCK_AI_CATALOG;
  const now = opts.now ?? (() => new Date());
  const usedBase = { chat: { requests: 100, input: 30_000, output: 9_000 }, analytics: { requests: 24, input: 8_400, output: 3_100 } };
  const usedTokens = () => opts.usedTokens ?? Object.values(usedBase).reduce((a, r) => a + r.input + r.output, 0);

  const st: State = {
    keySource: "platform",
    chat: { provider: "anthropic", model: "claude-haiku-5-5" },
    analytics: { provider: "anthropic", model: "claude-sonnet-5-5" },
    mode: "confirm",
    prompt: null,
    limits: { monthlyTokenLimit: null, monthlyRequestLimit: null, maxOutputTokensPerRequest: 1024 },
    platformEnabled: false,
    platformTokens: null,
    platformRequests: null,
    keys: new Map(),
    updatedAt: null,
  };
  const hits: number[] = [];

  const delay = async <T>(v: T): Promise<T> => {
    if (opts.wait !== false) await new Promise((r) => setTimeout(r, LATENCY_MS));
    return structuredClone(v);
  };

  const assertAccess = (ownerOnly: boolean) => {
    const r = opts.role();
    if (r === "owner" || (r === "admin" && !ownerOnly)) return;
    throw err(403, "forbidden_role");
  };
  const rateLimit = () => {
    const t = now().getTime();
    while (hits.length && t - hits[0] > KEY_RATE_LIMIT.windowSeconds * 1000) hits.shift();
    if (hits.length >= KEY_RATE_LIMIT.permits) throw err(429, "rate_limited", Math.ceil((KEY_RATE_LIMIT.windowSeconds * 1000 - (t - hits[0])) / 1000));
    hits.push(t);
  };

  const model = (id: string): AiCatalogModel | undefined => catalog.models.find((m) => m.id === id);
  const providerOf = (id: string) => catalog.providers.find((p) => p.id === id);
  const defaultModel = (provider: string, role: AiRoleId) =>
    catalog.models.find((m) => m.provider === provider && m.isDefaultFor.includes(role)) ?? catalog.models.find((m) => m.provider === provider);
  const usedBy = (provider: string): AiRoleId[] => ROLES.filter((r) => st[r].provider === provider);

  function checkRole(sel: { provider: string; model: string }) {
    const p = providerOf(sel.provider);
    if (!p) throw err(422, "unknown_provider");
    if (!p.available) throw err(422, "provider_not_available");
    const m = model(sel.model);
    if (!m) throw err(422, "unknown_model");
    if (m.provider !== sel.provider) throw err(422, "model_provider_mismatch");
  }

  function effectiveLimits() {
    const t = st.limits;
    const platform = st.keySource === "platform" && st.platformEnabled;
    const pick = (a: number | null, b: number | null) => (a !== null && b !== null ? Math.min(a, b) : (a ?? b));
    const pt = platform && st.platformTokens ? st.platformTokens : null;
    const pr = platform && st.platformRequests ? st.platformRequests : null;
    const both = (a: number | null, b: number | null) => a !== null && b !== null;
    const source: "tenant" | "platform" | "min" =
      both(t.monthlyTokenLimit, pt) || both(t.monthlyRequestLimit, pr) ? "min" : (pt ?? pr) !== null && t.monthlyTokenLimit === null && t.monthlyRequestLimit === null ? "platform" : "tenant";
    return { monthlyTokens: pick(t.monthlyTokenLimit, pt), monthlyRequests: pick(t.monthlyRequestLimit, pr), source };
  }

  function statusOf(role: AiRoleId): AiRoleStatus {
    const sel = st[role];
    if (!providerOf(sel.provider)?.available) return { state: "unavailable", reason: "provider_not_available" };
    if (st.keySource === "own") {
      if (!st.keys.has(sel.provider)) return { state: "unavailable", reason: "own_key_missing" };
    } else if (!st.platformEnabled) return { state: "unavailable", reason: "platform_disabled" };
    const lim = effectiveLimits();
    if (lim.monthlyTokens !== null && usedTokens() >= lim.monthlyTokens) return { state: "unavailable", reason: "quota_exceeded" };
    return { state: "ready" };
  }

  function roleDto(role: AiRoleId) {
    const sel = st[role];
    const m = model(sel.model);
    // Модель, якої більше немає в каталозі: діє дефолт ролі, `modelFallback: true`.
    if (!m || !m.available) {
      const d = defaultModel(sel.provider, role);
      return { provider: sel.provider, model: d?.id ?? sel.model, tier: d?.tier ?? "economy", modelFallback: true };
    }
    return { provider: sel.provider, model: m.id, tier: m.tier, modelFallback: false };
  }

  function settings(): AiSettings {
    const owner = opts.role() === "owner";
    const keys: AiKeyInfo[] = catalog.providers
      .filter((p) => p.available)
      .map((p) => {
        const k = st.keys.get(p.id);
        return {
          provider: p.id,
          hasKey: !!k,
          last4: k?.last4 ?? null,
          updatedAt: k?.updatedAt ?? null,
          updatedByName: k ? "Власник" : null,
          usedByRoles: usedBy(p.id),
        };
      });
    return {
      keySource: st.keySource,
      chat: roleDto("chat"),
      analytics: roleDto("analytics"),
      mode: st.mode,
      promptAddendum: st.prompt ?? MOCK_DEFAULT_PROMPT,
      isDefaultPrompt: st.prompt === null,
      defaultPromptAddendum: MOCK_DEFAULT_PROMPT,
      promptAddendumMaxLength: MAX_PROMPT,
      limits: { ...st.limits },
      platformQuota: { enabled: st.platformEnabled, monthlyTokens: st.platformTokens, monthlyRequests: st.platformRequests },
      status: { chat: statusOf("chat"), analytics: statusOf("analytics") },
      // Для admin властивості `keys` немає взагалі.
      ...(owner ? { keys } : {}),
      updatedAt: st.updatedAt,
      updatedByName: st.updatedAt ? "Власник" : null,
    };
  }

  const cost = (m: AiCatalogModel | undefined, input: number, output: number) =>
    m && m.inputPricePerMTok !== null && m.outputPricePerMTok !== null
      ? Math.round(((input / 1e6) * m.inputPricePerMTok + (output / 1e6) * m.outputPricePerMTok) * 10_000) / 10_000
      : null;

  return {
    __state: () => st,

    getAiSettings: async () => {
      assertAccess(false);
      return delay(settings());
    },

    saveAiSettings: async (input: AiSettingsInput) => {
      assertAccess(false);
      for (const r of ROLES) checkRole(input[r]);
      if (!["suggest", "confirm", "auto"].includes(input.mode)) throw err(422, "invalid_mode");
      if (input.mode === "auto" && st.mode !== "auto" && !input.acknowledgeAutoMode) throw err(422, "auto_mode_ack_required");
      const l = input.limits;
      const okInt = (v: number | null, min: number, max: number) => v === null || (Number.isInteger(v) && v >= min && v <= max);
      if (
        !okInt(l.monthlyTokenLimit, 1, 100_000_000_000) ||
        !okInt(l.monthlyRequestLimit, 1, 10_000_000) ||
        !okInt(l.maxOutputTokensPerRequest, 64, 8192) ||
        l.maxOutputTokensPerRequest === null
      )
        throw err(422, "invalid_limits");

      let prompt: string | null = null;
      if (input.promptAddendum !== null) {
        const text = normalizePrompt(input.promptAddendum);
        if (promptLength(text) > MAX_PROMPT) throw err(422, "prompt_addendum_too_long");
        if (FORBIDDEN.test(text) || text.split("\n").some((x) => x.trimStart().startsWith("===")))
          throw err(422, "prompt_addendum_forbidden");
        if (SECRET.test(text)) throw err(422, "prompt_addendum_contains_secret");
        prompt = text === "" || text === normalizePrompt(MOCK_DEFAULT_PROMPT) ? null : text;
      }
      const warnings = prompt === null ? [] : warningsFor(prompt);
      st.chat = { ...input.chat };
      st.analytics = { ...input.analytics };
      st.mode = input.mode;
      st.prompt = prompt;
      st.limits = { ...l };
      st.updatedAt = now().toISOString();
      return delay({ settings: settings(), warnings });
    },

    setAiKeySource: async (keySource) => {
      assertAccess(true);
      st.keySource = keySource;
      st.updatedAt = now().toISOString();
      return delay(settings());
    },

    getAiModels: async () => {
      assertAccess(false);
      return delay(catalog);
    },

    saveAiKey: async ({ provider, apiKey, forRole, acknowledgeAffectedRoles }) => {
      assertAccess(true);
      rateLimit();
      const p = providerOf(provider);
      if (!p) throw err(422, "unknown_provider");
      if (!p.available) throw err(422, "provider_not_available");
      if (!KEY_FORMAT.test(apiKey) || (provider === "anthropic" && !apiKey.startsWith("sk-ant-"))) throw err(422, "invalid_key_format");
      const others = usedBy(provider).filter((r) => r !== forRole);
      if (st.keySource === "own" && st.keys.has(provider) && others.length > 0 && !acknowledgeAffectedRoles)
        throw err(409, "key_affects_other_roles", undefined, others);
      if (/unavailable/i.test(apiKey)) throw err(502, "provider_unavailable");
      if (/rejected/i.test(apiKey)) throw err(422, "key_rejected");
      const updatedAt = now().toISOString();
      st.keys.set(provider, { last4: apiKey.slice(-4), updatedAt });
      st.keySource = "own";
      return delay({ provider, hasKey: true, last4: apiKey.slice(-4), updatedAt, keySource: st.keySource, usedByRoles: usedBy(provider) });
    },

    deleteAiKey: async ({ provider, forRole, acknowledgeAffectedRoles }) => {
      assertAccess(true);
      rateLimit();
      if (!st.keys.has(provider)) return delay(undefined);
      const others = usedBy(provider).filter((r) => r !== forRole);
      if (st.keySource === "own" && others.length > 0 && !acknowledgeAffectedRoles) throw err(409, "key_affects_other_roles", undefined, others);
      st.keys.delete(provider);
      return delay(undefined);
    },

    testAiKey: async (provider, apiKey) => {
      assertAccess(true);
      rateLimit();
      const p = providerOf(provider);
      if (!p) throw err(422, "unknown_provider");
      if (!p.available) throw err(422, "provider_not_available");
      const modelId = st.chat.provider === provider ? st.chat.model : st.analytics.provider === provider ? st.analytics.model : (defaultModel(provider, "chat")?.id ?? "");
      const base = { provider, model: modelId, checkedAt: now().toISOString() };
      if (apiKey === undefined) {
        if (!st.keys.has(provider)) return delay({ ok: false, reason: "no_key", ...base });
        return delay({ ok: true, ...base });
      }
      if (!KEY_FORMAT.test(apiKey) || (provider === "anthropic" && !apiKey.startsWith("sk-ant-"))) throw err(422, "invalid_key_format");
      if (/unavailable/i.test(apiKey)) return delay({ ok: false, reason: "provider_unavailable", ...base });
      if (/rejected/i.test(apiKey)) return delay({ ok: false, reason: "key_rejected", ...base });
      return delay({ ok: true, ...base });
    },

    getAiUsage: async (months = 1) => {
      assertAccess(false);
      const d = now();
      const y = d.getUTCFullYear();
      const mo = d.getUTCMonth();
      const ymd = (dt: Date) => dt.toISOString().slice(0, 10);
      const chat = usedBase.chat;
      const an = usedBase.analytics;
      const byModel: AiUsage["byModel"] = [
        { role: "chat", provider: st.chat.provider, model: st.chat.model, keySource: st.keySource, requests: chat.requests, inputTokens: chat.input, outputTokens: chat.output, estimatedCost: cost(model(st.chat.model), chat.input, chat.output) },
        { role: "analytics", provider: st.analytics.provider, model: st.analytics.model, keySource: st.keySource, requests: an.requests, inputTokens: an.input, outputTokens: an.output, estimatedCost: cost(model(st.analytics.model), an.input, an.output) },
      ];
      const costs = byModel.map((r) => r.estimatedCost);
      const lim = effectiveLimits();
      const history = Array.from({ length: Math.min(12, Math.max(1, months)) }, (_, i) => {
        const dt = new Date(Date.UTC(y, mo - i, 1));
        const f = i === 0 ? 1 : 1 + (i % 3) * 0.4;
        return {
          period: `${dt.getUTCFullYear()}-${String(dt.getUTCMonth() + 1).padStart(2, "0")}`,
          requests: Math.round((chat.requests + an.requests) * f),
          inputTokens: Math.round((chat.input + an.input) * f),
          outputTokens: Math.round((chat.output + an.output) * f),
        };
      });
      return delay<AiUsage>({
        keySource: st.keySource,
        period: { from: ymd(new Date(Date.UTC(y, mo, 1))), to: ymd(d) },
        limits: { monthlyTokens: lim.monthlyTokens, monthlyRequests: lim.monthlyRequests, source: lim.source },
        current: {
          requests: chat.requests + an.requests,
          errors: 3,
          inputTokens: chat.input + an.input,
          outputTokens: chat.output + an.output,
          estimatedCost: costs.some((c) => c === null) ? null : Math.round(costs.reduce<number>((a, c) => a + (c ?? 0), 0) * 10_000) / 10_000,
          currency: "USD",
        },
        byModel,
        history,
      });
    },
  };
}
