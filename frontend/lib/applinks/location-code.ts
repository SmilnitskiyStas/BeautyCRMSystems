/**
 * Публічний код закладу для веб-посилань `/l/{code}` (дзеркалить mobile/src/lib/deeplink.ts; ADR-011 §1).
 * Вхід недовірений: до будь-якого запиту проходить нормалізацію й перевірку формату.
 */

/** 8 символів Crockford Base32 (без I, L, O, U). */
export const PUBLIC_CODE_RE = /^[0-9A-HJKMNP-TV-Z]{8}$/;

/** trim, верхній регістр, без `-` і пробілів, аліаси Crockford (O->0, I/L->1). `null`, якщо формат хибний. */
export function normalizePublicCode(input: string | null | undefined): string | null {
  if (typeof input !== "string") return null;
  const code = input
    .trim()
    .toUpperCase()
    .replace(/[-\s]/g, "")
    .replace(/O/g, "0")
    .replace(/[IL]/g, "1");
  return PUBLIC_CODE_RE.test(code) ? code : null;
}

/** `XXXX-XXXX` для показу людині. */
export const formatPublicCode = (code: string): string => `${code.slice(0, 4)}-${code.slice(4)}`;

/** Власна схема мобільного застосунку. */
export const APP_SCHEME = "beautycrm";

/** `beautycrm://l/{code}` */
export const appDeepLink = (code: string): string => `${APP_SCHEME}://l/${code}`;

/** Відповідь `GET /api/public/resolve/{code}` (ADR-011): без tenantId, телефонів і налаштувань. */
export interface PublicLocationRef {
  publicCode: string;
  tenantSlug: string;
  locationId: string;
  name: string;
  address: string | null;
  city: string | null;
  timezone: string;
}

export type ResolveResult =
  | { status: "ok"; location: PublicLocationRef }
  | { status: "not_found" }
  | { status: "error" };

const SLUG_RE = /^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$/;

/** Лишає тільки відомі поля й типи; усе інше (у т.ч. можливі зайві дані) відкидається. */
export function toPublicLocationRef(raw: unknown): PublicLocationRef | null {
  if (typeof raw !== "object" || raw === null) return null;
  const r = raw as Record<string, unknown>;
  const str = (v: unknown): string | null => (typeof v === "string" && v.trim() ? v : null);
  const publicCode = normalizePublicCode(str(r.publicCode));
  const tenantSlug = str(r.tenantSlug)?.toLowerCase() ?? null;
  const locationId = str(r.locationId);
  const name = str(r.name);
  if (!publicCode || !tenantSlug || !SLUG_RE.test(tenantSlug) || !locationId || !name) return null;
  return {
    publicCode,
    tenantSlug,
    locationId,
    name,
    address: str(r.address),
    city: str(r.city),
    timezone: str(r.timezone) ?? "",
  };
}

/** Серверний резолв коду. 404 -> `not_found`; мережа/5xx/хибна відповідь -> `error`. Ніколи не кидає. */
export async function resolveLocationCode(
  code: string,
  opts: { baseUrl?: string; fetchImpl?: typeof fetch } = {},
): Promise<ResolveResult> {
  const base = (opts.baseUrl ?? process.env.API_INTERNAL_URL ?? process.env.NEXT_PUBLIC_API_URL ?? "").replace(/\/+$/, "");
  if (!base) return { status: "error" };
  const doFetch = opts.fetchImpl ?? fetch;
  try {
    const res = await doFetch(`${base}/api/public/resolve/${encodeURIComponent(code)}`, {
      cache: "no-store",
      headers: { Accept: "application/json" },
      signal: AbortSignal.timeout(5000),
    });
    if (res.status === 404) return { status: "not_found" };
    if (!res.ok) return { status: "error" };
    const location = toPublicLocationRef(await res.json());
    return location ? { status: "ok", location } : { status: "error" };
  } catch {
    return { status: "error" };
  }
}
