import { BeautyApiError, readApiError } from "./errors";
import { USE_MOCK } from "./types";

/** Токен запрошення: 32 байти base64url без padding (§27.8). */
export const INVITE_TOKEN_RE = /^[A-Za-z0-9_-]{43}$/;

export interface OwnerInviteInfo {
  tenantName: string;
  /** Уже маскований сервером (`o***@домен`). */
  email: string;
  expiresAt: string;
}

/** Мінімум як у backend `PasswordRules` (Auth:PasswordMinLength = 12) і максимум 128. */
export const MIN_PASSWORD = 12;
export const MAX_PASSWORD = 128;

/**
 * Дістає токен з фрагмента `#t=…` і ОДРАЗУ прибирає фрагмент з адресного рядка.
 * Повертає `null`, якщо токена немає або він має некоректний формат. Токен не логуємо й не зберігаємо.
 */
export function takeTokenFromHash(): string | null {
  if (typeof window === "undefined") return null;
  const raw = window.location.hash;
  if (raw) {
    window.history.replaceState(window.history.state, "", window.location.pathname + window.location.search);
  }
  const t = new URLSearchParams(raw.replace(/^#/, "")).get("t");
  return t && INVITE_TOKEN_RE.test(t) ? t : null;
}

async function post(path: string, body: unknown): Promise<Response> {
  try {
    return await fetch(`/api/session/owner-invite/${path}`, {
      method: "POST",
      credentials: "same-origin",
      cache: "no-store",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    });
  } catch {
    throw new BeautyApiError(0, "api_unreachable", "network");
  }
}

export async function inspectOwnerInvite(token: string): Promise<OwnerInviteInfo> {
  if (USE_MOCK) return mockInspect(token);
  const res = await post("inspect", { token });
  if (!res.ok) throw await readApiError(res);
  return (await res.json()) as OwnerInviteInfo;
}

/** Демо без backend: токен, що починається з `expired`/`suspended`, імітує помилки; решта - успіх. */
function mockInspect(token: string): Promise<OwnerInviteInfo> {
  if (token.startsWith("expired")) return Promise.reject(new BeautyApiError(410, "invite_invalid", "mock"));
  if (token.startsWith("suspended")) return Promise.reject(new BeautyApiError(403, "tenant_suspended", "mock"));
  return Promise.resolve({ tenantName: "Beauty Lab (демо)", email: "o***@beauty.test", expiresAt: new Date(Date.now() + 72 * 3600_000).toISOString() });
}
