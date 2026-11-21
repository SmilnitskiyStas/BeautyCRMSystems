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

/** BFF: перемикання бізнесу (§31.5). Refresh-токен поточної сесії додається з cookie й замінюється новим. */
export async function POST(req: NextRequest) {
  if (!isSameOrigin(req)) return forbidden();
  const body = (await req.json().catch(() => null)) as { tenantId?: unknown } | null;
  if (!body || typeof body.tenantId !== "string") {
    return NextResponse.json({ code: "validation_failed", message: "Invalid body." }, { status: 422 });
  }
  const auth = req.headers.get("authorization");
  const rt = req.cookies.get(REFRESH_COOKIE)?.value;
  if (!auth || !rt) {
    const out = NextResponse.json({ code: "invalid_token", message: "No session." }, { status: 401 });
    out.headers.set("Cache-Control", "no-store");
    return out;
  }

  const res = await callBackend(req, "/api/auth/switch", { tenantId: body.tenantId, refreshToken: rt }, { Authorization: auth });
  if (!res.ok) {
    const out = res instanceof NextResponse ? res : await passError(res);
    out.headers.set("Cache-Control", "no-store");
    if (res.status === 401) clearRefreshCookie(out);
    return out;
  }
  const t = (await res.json()) as { accessToken: string; expiresInSeconds: number; refreshToken: string; user: unknown };
  const out = NextResponse.json({ accessToken: t.accessToken, expiresInSeconds: t.expiresInSeconds, user: t.user });
  out.headers.set("Cache-Control", "no-store");
  setRefreshCookie(out, t.refreshToken);
  return out;
}
