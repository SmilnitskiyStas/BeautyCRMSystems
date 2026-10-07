import { NextResponse, type NextRequest } from "next/server";
import {
  REFRESH_COOKIE,
  callBackend,
  clearRefreshCookie,
  forbidden,
  isSameOrigin,
  passError,
  setRefreshCookie,
} from "@/features/beauty-auth/server";

/** BFF: ротація refresh-токена з httpOnly-cookie, повертає новий access-токен. */
export async function POST(req: NextRequest) {
  if (!isSameOrigin(req)) return forbidden();
  const rt = req.cookies.get(REFRESH_COOKIE)?.value;
  if (!rt) return NextResponse.json({ code: "no_session", message: "No session." }, { status: 401 });

  const res = await callBackend(req, "/api/auth/refresh", { refreshToken: rt });
  if (!res.ok) {
    const out = res instanceof NextResponse ? res : await passError(res);
    // Cookie скидаємо лише коли сервер явно відхилив токен, а не при збої мережі/5xx/rate limit.
    if (res.status === 401) clearRefreshCookie(out);
    return out;
  }
  const t = (await res.json()) as { accessToken: string; expiresInSeconds: number; refreshToken: string; user: unknown };
  const out = NextResponse.json({ accessToken: t.accessToken, expiresInSeconds: t.expiresInSeconds, user: t.user });
  out.headers.set("Cache-Control", "no-store");
  setRefreshCookie(out, t.refreshToken);
  return out;
}
