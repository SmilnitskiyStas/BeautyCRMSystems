import { BeautyApiError, readApiError } from "./errors";
import { ROLE_PRESETS, parsePermissions } from "./access";
import { API_URL, USE_MOCK, type Role, type SessionResponse, type SessionUser } from "./types";

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
const userListeners = new Set<(u: SessionUser) => void>();
/** Відповідь `me` у польоті: single-flight, щоб заголовок `X-Access-Version` не запускав каскад перечитувань. */
let inflightMe: Promise<SessionUser> | null = null;
const ME_MIN_INTERVAL_MS = 5_000;
let lastMeAt = 0;
const EARLY_REFRESH_MS = 30_000;

export const getUser = () => currentUser;

/** Підписка на завершення сесії (refresh не вдався / вихід). Повертає функцію відписки. */
export function onSessionEnd(fn: () => void): () => void {
  endListeners.add(fn);
  return () => endListeners.delete(fn);
}

/** Підписка на зміну користувача сесії (нові права після перечитування `me`). */
export function onUserChange(fn: (u: SessionUser) => void): () => void {
  userListeners.add(fn);
  return () => userListeners.delete(fn);
}

/** Поля доступу переносимо зі старого користувача, якщо refresh/login їх не віддав (той самий `id`). */
function mergeAccess(prev: SessionUser | null, next: SessionUser): SessionUser {
  if (!prev || prev.id !== next.id || next.permissions || next.role !== prev.role) return next;
  return {
    ...next,
    permissions: prev.permissions,
    accessStamp: prev.accessStamp,
    accessVersion: prev.accessVersion,
    catalogVersion: prev.catalogVersion,
    modules: prev.modules,
  };
}

function store(s: SessionResponse) {
  accessToken = s.accessToken;
  expiresAtMs = Date.now() + s.expiresInSeconds * 1000;
  currentUser = mergeAccess(currentUser, s.user);
}

/** Розбір `GET /api/auth/me` (§30.9.1). Відсутній/некоректний `permissions` лишається `undefined` (не вигадуємо права). */
export function toSessionUser(d: Record<string, unknown>): SessionUser {
  const role = d.role as Role;
  const modules = Array.isArray(d.modules) ? d.modules.filter((m): m is string => typeof m === "string") : undefined;
  return {
    id: String(d.id ?? ""),
    email: String(d.email ?? ""),
    fullName: String(d.fullName ?? ""),
    role,
    specialistId: typeof d.specialistId === "string" ? d.specialistId : null,
    permissions: parsePermissions(d.permissions),
    ...(typeof d.accessStamp === "string" ? { accessStamp: d.accessStamp } : {}),
    ...(typeof d.accessVersion === "number" ? { accessVersion: d.accessVersion } : {}),
    ...(typeof d.catalogVersion === "number" ? { catalogVersion: d.catalogVersion } : {}),
    ...(modules ? { modules } : {}),
  };
}

/**
 * Перечитує `GET /api/auth/me` і оновлює права в пам'яті (§30.8): після login/refresh, коли `X-Access-Version`
 * розходиться з `accessStamp`, після `403 forbidden_role {permission}` / `403 module_disabled` і при поверненні фокуса.
 * Single-flight; помилка пробрасується (виклики «на тлі» її ігнорують).
 */
export function reloadMe(): Promise<SessionUser> {
  if (inflightMe) return inflightMe;
  if (USE_MOCK) {
    const u = currentUser;
    return Promise.resolve(
      u ?? { id: "mock-user", email: "demo@beauty.test", fullName: "Демо користувач", role: "owner", specialistId: null },
    );
  }
  const run = async (): Promise<SessionUser> => {
    const res = await apiFetch("/api/auth/me", { cache: "no-store" });
    const next = mergeAccess(currentUser, toSessionUser((await res.json()) as Record<string, unknown>));
    currentUser = next;
    lastMeAt = Date.now();
    userListeners.forEach((fn) => fn(next));
    return next;
  };
  const p = run().finally(() => {
    inflightMe = null;
  });
  inflightMe = p;
  return p;
}

/** Перечитування «на тлі» (focus/заголовок): не частіше за раз на кілька секунд, без помилок назовні. */
export function reloadMeSoon(force = false): void {
  if (inflightMe || !currentUser) return;
  if (!force && Date.now() - lastMeAt < ME_MIN_INTERVAL_MS) return;
  reloadMe().catch(() => undefined);
}

/** Розбіжність `X-Access-Version` із `accessStamp` у пам'яті = права або модулі змінились (§30.9.1). */
export function accessStampStale(header: string | null, stamp: string | undefined): boolean {
  return !!header && !!stamp && header !== stamp;
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
  return loadAccessAfterAuth();
}

/** Після login/прийняття запрошення права беремо з `me`; без них сесію не лишаємо (fail-closed). */
async function loadAccessAfterAuth(): Promise<SessionUser> {
  try {
    return await reloadMe();
  } catch (e) {
    clearSession(false);
    throw e;
  }
}

/** Прийняття запрошення власника (§27.8): відповідь як у login, сесія встановлюється так само. */
export async function acceptOwnerInvite(input: { token: string; password: string }): Promise<SessionUser> {
  if (USE_MOCK) {
    const user: SessionUser = {
      id: "mock-user",
      email: "owner@beauty.test",
      fullName: "Демо директор",
      role: "owner",
      specialistId: null,
      permissions: { ...ROLE_PRESETS.owner },
    };
    store({ accessToken: "mock", expiresInSeconds: 900, user });
    return user;
  }
  const res = await postSession("owner-invite/accept", input);
  if (!res.ok) throw await readApiError(res);
  const s = (await res.json()) as SessionResponse;
  store(s);
  return loadAccessAfterAuth();
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
  noteAccessHeader(res);
  if (!res.ok) {
    const err = await readApiError(res);
    // §30.8: нестача конкретного права або вимкнений модуль = права могли змінитись; ранг/owner-only (без `permission`) - ні.
    if (err.status === 403 && ((err.code === "forbidden_role" && err.permission) || err.code === "module_disabled")) reloadMeSoon(true);
    throw err;
  }
  return res;
}

function noteAccessHeader(res: Response) {
  if (res.status === 401 || inflightMe || !currentUser) return;
  if (accessStampStale(res.headers.get("X-Access-Version"), currentUser.accessStamp)) reloadMeSoon(true);
}

export async function apiJson<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await apiFetch(path, init);
  if (res.status === 204) return undefined as T;
  return (await res.json()) as T;
}
