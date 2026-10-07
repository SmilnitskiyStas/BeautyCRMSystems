import { NextResponse, type NextRequest } from "next/server";

/** Лише для route handlers BFF (`app/api/session/*`): серверні налаштування й cookie refresh-токена. */

export const REFRESH_COOKIE = "beauty_rt";
const REFRESH_MAX_AGE = 14 * 24 * 3600; // = Auth__RefreshTokenDays за замовчуванням

export const backendUrl = () =>
  (process.env.API_URL ?? process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5000").replace(/\/+$/, "");

const secure = () => process.env.NODE_ENV === "production";

export function setRefreshCookie(res: NextResponse, token: string) {
  res.cookies.set(REFRESH_COOKIE, token, {
    httpOnly: true,
    secure: secure(),
    sameSite: "strict",
    path: "/",
    maxAge: REFRESH_MAX_AGE,
  });
}

export function clearRefreshCookie(res: NextResponse) {
  res.cookies.set(REFRESH_COOKIE, "", { httpOnly: true, secure: secure(), sameSite: "strict", path: "/", maxAge: 0 });
}

/** CSRF-захист: змінюючі запити BFF приймаються лише з того самого origin. */
export function isSameOrigin(req: NextRequest): boolean {
  const origin = req.headers.get("origin");
  if (!origin) return false;
  const host = req.headers.get("x-forwarded-host") ?? req.headers.get("host");
  try {
    return new URL(origin).host === host;
  } catch {
    return false;
  }
}

export const forbidden = () =>
  NextResponse.json({ code: "forbidden_origin", message: "Cross-origin request rejected." }, { status: 403 });

/** Прокидаємо IP клієнта, щоб rate limit backend рахував користувачів, а не сервер Next. */
function forwardedFor(req: NextRequest): string | undefined {
  const xff = req.headers.get("x-forwarded-for");
  return xff ?? req.headers.get("x-real-ip") ?? undefined;
}

/** POST до backend із JSON-тілом; мережеву помилку перетворює на 502 `api_unreachable`. */
export async function callBackend(req: NextRequest, path: string, body: unknown): Promise<Response | NextResponse> {
  try {
    const headers: Record<string, string> = { "Content-Type": "application/json" };
    const ip = forwardedFor(req);
    if (ip) headers["X-Forwarded-For"] = ip;
    return await fetch(`${backendUrl()}${path}`, {
      method: "POST",
      headers,
      body: JSON.stringify(body),
      cache: "no-store",
    });
  } catch {
    return NextResponse.json({ code: "api_unreachable", message: "Backend unreachable." }, { status: 502 });
  }
}

/** Передаємо помилку backend клієнту (статус, `{code,message}`, Retry-After). */
export async function passError(res: Response): Promise<NextResponse> {
  let body: unknown = { code: "", message: "Request failed." };
  try {
    body = await res.json();
  } catch {
    /* не JSON */
  }
  const out = NextResponse.json(body, { status: res.status });
  const retry = res.headers.get("Retry-After");
  if (retry) out.headers.set("Retry-After", retry);
  return out;
}
