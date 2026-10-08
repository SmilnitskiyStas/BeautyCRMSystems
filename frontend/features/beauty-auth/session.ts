import { BeautyApiError, readApiError } from "./errors";
import { API_URL, type SessionResponse, type SessionUser } from "./types";

/**
 * Клієнтське сховище сесії.
 * - access-токен живе ЛИШЕ в пам'яті цього модуля (не в localStorage/cookie/DOM), тож XSS не може
 *   витягти довгоживучий секрет, а перезавантаження сторінки просто робить тихий refresh;
 * - refresh-токен недоступний JS: він лежить в httpOnly SameSite=Strict cookie, яку ставить/читає лише BFF
 *   (`app/api/session/*`).
 */
let accessToken: string | null = null;
let expiresAtMs = 0;
let currentUser: SessionUser | null = null;
let inflight: Promise<SessionResponse> | null = null;

const endListeners = new Set<() => void>();
const EARLY_REFRESH_MS = 30_000;

export const getUser = () => currentUser;

/** Підписка на завершення сесії (refresh не вдався / вихід). Повертає функцію відписки. */
export function onSessionEnd(fn: () => void): () => void {
  endListeners.add(fn);
  return () => endListeners.delete(fn);
}

function store(s: SessionResponse) {
  accessToken = s.accessToken;
  expiresAtMs = Date.now() + s.expiresInSeconds * 1000;
  currentUser = s.user;
}

export function clearSession(notify = true) {
  accessToken = null;
  expiresAtMs = 0;
  currentUser = null;
  if (notify) endListeners.forEach((fn) => fn());
}

async function postSession(path: string, body?: unknown): Promise<Response> {
  try {
    return await fetch(`/api/session/${path}`, {
      method: "POST",
      credentials: "same-origin",
      headers: body ? { "Content-Type": "application/json" } : undefined,
      body: body ? JSON.stringify(body) : undefined,
    });
  } catch {
    throw new BeautyApiError(0, "api_unreachable", "network");
  }
}

export async function login(input: { tenant: string; email: string; password: string }): Promise<SessionUser> {
  const res = await postSession("login", input);
  if (!res.ok) throw await readApiError(res);
  const s = (await res.json()) as SessionResponse;
  store(s);
  return s.user;
}

/**
 * Оновлення access-токена. Single-flight у вкладці; між вкладками серіалізується через Web Locks, бо
 * ротація refresh-токена закриває всі сесії при повторному використанні старого.
 */
export function refreshSession(): Promise<SessionResponse> {
  if (inflight) return inflight;
  const run = async () => {
    const res = await postSession("refresh");
    if (!res.ok) throw await readApiError(res);
    const s = (await res.json()) as SessionResponse;
    store(s);
    return s;
  };
  const locks = typeof navigator !== "undefined" ? navigator.locks : undefined;
  const p: Promise<SessionResponse> = locks ? Promise.resolve(locks.request("beauty-session-refresh", run)) : run();
  const tracked: Promise<SessionResponse> = p.finally(() => {
    inflight = null;
  });
  inflight = tracked;
  return tracked;
}

export async function logout(): Promise<void> {
  try {
    await postSession("logout");
  } finally {
    clearSession(false);
  }
}

async function validToken(): Promise<string> {
  if (!accessToken || Date.now() >= expiresAtMs - EARLY_REFRESH_MS) {
    await refreshOrEnd();
  }
  return accessToken as unknown as string;
}

/**
 * Refresh; сесію завершуємо ЛИШЕ коли сервер відхилив її (401). Мережеві збої, 429 (rate limit), 423 і 5xx
 * сесію не завершують: користувач повторить пізніше (інакше rate limit розлогінював би всіх за одним NAT).
 */
async function refreshOrEnd(): Promise<void> {
  try {
    await refreshSession();
  } catch (e) {
    if (e instanceof BeautyApiError && e.status === 401) clearSession();
    throw e;
  }
}

/** Запит до backend з Bearer; на 401 — один refresh і повтор. Кидає `BeautyApiError`. */
export async function apiFetch(path: string, init: RequestInit = {}): Promise<Response> {
  const send = async (token: string) => {
    const headers = new Headers(init.headers);
    headers.set("Authorization", `Bearer ${token}`);
    if (init.body && !headers.has("Content-Type")) headers.set("Content-Type", "application/json");
    try {
      return await fetch(`${API_URL}${path}`, { ...init, headers });
    } catch {
      throw new BeautyApiError(0, "api_unreachable", "network");
    }
  };

  let res = await send(await validToken());
  if (res.status === 401) {
    await refreshOrEnd();
    res = await send(accessToken as unknown as string);
    if (res.status === 401) {
      clearSession();
    }
  }
  if (!res.ok) throw await readApiError(res);
  return res;
}

export async function apiJson<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await apiFetch(path, init);
  if (res.status === 204) return undefined as T;
  return (await res.json()) as T;
}
