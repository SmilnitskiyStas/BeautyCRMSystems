import { NextResponse, type NextRequest } from "next/server";
import { callBackend, forbidden, isSameOrigin, passError, setRefreshCookie } from "@/features/beauty-auth/server";

/** BFF: вхід. Refresh-токен йде в httpOnly-cookie й НЕ повертається в JSON. */
export async function POST(req: NextRequest) {
  if (!isSameOrigin(req)) return forbidden();
  const body = (await req.json().catch(() => null)) as { tenant?: unknown; email?: unknown; password?: unknown } | null;
  if (!body || typeof body.tenant !== "string" || typeof body.email !== "string" || typeof body.password !== "string") {
    return NextResponse.json({ code: "validation_failed", message: "Invalid body." }, { status: 422 });
  }
  const res = await callBackend(req, "/api/auth/login", { tenant: body.tenant, email: body.email, password: body.password });
  if (!res.ok) return res instanceof NextResponse ? res : passError(res);

  const t = (await res.json()) as { accessToken: string; expiresInSeconds: number; refreshToken: string; user: unknown };
  const out = NextResponse.json({ accessToken: t.accessToken, expiresInSeconds: t.expiresInSeconds, user: t.user });
  out.headers.set("Cache-Control", "no-store");
  setRefreshCookie(out, t.refreshToken);
  return out;
}
