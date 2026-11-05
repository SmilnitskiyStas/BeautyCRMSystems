import { MIN_KEY_BYTES, decodeKey, newNonce, signedHeaders } from "./hmac";

/** TASK-821: delivery of beauty_platform_feedback rows to ShelfGuard (ADR-018 §3.4, §27.9). */
export const FEEDBACK_PATH = "/api/integrations/beauty/feedback";
export const MAX_ATTEMPTS = 12;
export const BACKOFF_BASE_MS = 60_000;
export const BACKOFF_CAP_MS = 4 * 3_600_000;
export const LEASE_MS = 5 * 60_000;
const REQUEST_TIMEOUT_MS = 10_000;

export interface FeedbackConfig { baseUrl: URL; kid: string; secretBase64: string }
export type ConfigResult =
  | { enabled: true; config: FeedbackConfig }
  | { enabled: false; reason: "disabled" | "misconfigured" };

const LOOPBACK_HOSTS = new Set(["localhost", "[::1]"]);

function isLoopbackHost(hostname: string): boolean {
  return LOOPBACK_HOSTS.has(hostname) || /^127(\.\d{1,3}){3}$/.test(hostname);
}

/**
 * TASK-826 (L2): outside Development only https (HMAC signs the request, but message/contact travel in the body);
 * plain http is allowed on loopback only. Development = ASPNETCORE_ENVIRONMENT=Development and NODE_ENV != production
 * (same rule as marketing-gate.ts); there any http is accepted.
 */
export function isAllowedBaseUrl(url: URL, env: NodeJS.ProcessEnv): boolean {
  if (url.protocol === "https:") return true;
  if (url.protocol !== "http:") return false;
  const development = env["ASPNETCORE_ENVIRONMENT"] === "Development" && env["NODE_ENV"] !== "production";
  return development || isLoopbackHost(url.hostname);
}

/** Env only. Anything but an explicit "true" = off (rows stay pending); enabled but invalid = off + "misconfigured" (fail closed). */
export function loadFeedbackConfig(env: NodeJS.ProcessEnv): ConfigResult {
  if (!/^true$/i.test(env.SHELFGUARD_FEEDBACK_ENABLED ?? "")) return { enabled: false, reason: "disabled" };
  const kid = env.SHELFGUARD_FEEDBACK_KEY_ID ?? "";
  const secret = env.SHELFGUARD_FEEDBACK_SECRET ?? "";
  let baseUrl: URL;
  try { baseUrl = new URL(env.SHELFGUARD_BASE_URL ?? ""); } catch { return { enabled: false, reason: "misconfigured" }; }
  const okUrl = isAllowedBaseUrl(baseUrl, env) && !baseUrl.search && !baseUrl.hash;
  if (!okUrl || !/^[a-z0-9-]{3,32}$/.test(kid) || decodeKey(secret).length < MIN_KEY_BYTES) {
    return { enabled: false, reason: "misconfigured" };
  }
  return { enabled: true, config: { baseUrl, kid, secretBase64: secret } };
}

export interface FeedbackRow {
  id: string;
  type: string;
  severity: string;
  message: string;
  pageUrl: string | null;
  attempts: number; // already includes the current attempt
  tenantName: string;
  tenantRef: string; // tenants.external_ref ?? tenant id
  userEmail: string | null;
  userRole: string | null;
}

export type Outcome =
  | { kind: "delivered"; leadId: string | null }
  | { kind: "retry"; code: string; retryAfterMs?: number }
  | { kind: "permanent"; code: string };

export function buildBody(r: FeedbackRow): Uint8Array {
  const body: Record<string, string> = {
    sourceTenantRef: r.tenantRef, tenantName: r.tenantName, type: r.type, message: r.message, feedbackId: r.id, severity: r.severity,
  };
  if (r.userEmail) body.contact = r.userEmail;
  if (r.userRole) body.userRole = r.userRole;
  if (r.pageUrl) body.pageUrl = r.pageUrl;
  return Buffer.from(JSON.stringify(body), "utf8");
}

export function endpoint(cfg: FeedbackConfig): { url: string; path: string } {
  const path = cfg.baseUrl.pathname.replace(/\/+$/, "") + FEEDBACK_PATH;
  return { url: `${cfg.baseUrl.origin}${path}`, path };
}

export interface DeliverDeps { fetch: typeof fetch; now: () => Date; nonce?: () => string }

/** One signed POST. Never throws and never puts message/contact/secret into the result. */
export async function deliver(cfg: FeedbackConfig, row: FeedbackRow, deps: DeliverDeps): Promise<Outcome> {
  const body = buildBody(row);
  const { url, path } = endpoint(cfg);
  const headers = signedHeaders({
    kid: cfg.kid, secretBase64: cfg.secretBase64, method: "POST", path, body,
    timestamp: Math.floor(deps.now().getTime() / 1000), nonce: (deps.nonce ?? newNonce)(),
  });
  let res: Response;
  try {
    res = await deps.fetch(url, {
      method: "POST", body: body as unknown as BodyInit, redirect: "manual", signal: AbortSignal.timeout(REQUEST_TIMEOUT_MS),
      headers: { ...headers, "Content-Type": "application/json; charset=utf-8" },
    });
  } catch (err) {
    return { kind: "retry", code: (err as { name?: string })?.name === "TimeoutError" ? "timeout" : "network" };
  }
  const s = res.status;
  if (s === 200 || s === 201) return { kind: "delivered", leadId: await readLeadId(res) };
  if (s === 429) return { kind: "retry", code: "http_429", retryAfterMs: parseRetryAfter(res.headers.get("retry-after")) };
  if (s === 408 || s >= 500) return { kind: "retry", code: `http_${s}` };
  return { kind: "permanent", code: `http_${s}` }; // 401, 422 and every other 4xx (incl. redirects): retrying cannot help
}

async function readLeadId(res: Response): Promise<string | null> {
  try {
    const j = (await res.json()) as Record<string, unknown>;
    const v = j.leadId ?? j.id;
    return typeof v === "string" && v.length > 0 && v.length <= 64 ? v : null;
  } catch { return null; }
}

function parseRetryAfter(v: string | null): number | undefined {
  const n = Number(v);
  return v && Number.isFinite(n) && n > 0 ? Math.min(n * 1000, BACKOFF_CAP_MS) : undefined;
}

/** Exponential backoff with +-20% jitter; `attempts` = attempts made so far (>= 1). `rand` in [0,1). */
export function backoffMs(attempts: number, rand: () => number = Math.random): number {
  const base = Math.min(BACKOFF_BASE_MS * 2 ** Math.max(0, attempts - 1), BACKOFF_CAP_MS);
  return Math.round(base * (0.8 + 0.4 * rand()));
}

export type Resolution =
  | { status: "delivered"; leadId: string | null }
  | { status: "pending"; nextAttemptAt: Date; error: string }
  | { status: "failed"; error: string };

export function resolve(o: Outcome, attempts: number, now: Date, rand: () => number = Math.random): Resolution {
  if (o.kind === "delivered") return { status: "delivered", leadId: o.leadId };
  if (o.kind === "permanent") return { status: "failed", error: o.code };
  if (attempts >= MAX_ATTEMPTS) return { status: "failed", error: `max_attempts:${o.code}` };
  const wait = Math.max(o.retryAfterMs ?? 0, backoffMs(attempts, rand));
  return { status: "pending", nextAttemptAt: new Date(now.getTime() + wait), error: o.code };
}
