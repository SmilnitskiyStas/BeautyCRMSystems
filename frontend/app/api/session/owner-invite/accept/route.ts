import { NextResponse, type NextRequest } from "next/server";
import { callBackend, forbidden, isSameOrigin, passError, setRefreshCookie } from "@/features/beauty-auth/server";

/** BFF: прийняття запрошення власника (§27.8). Відповідь як у login: refresh-токен лише в httpOnly-cookie. */
export async function POST(req: NextRequest) {
  if (!isSameOrigin(req)) return forbidden();
  const body = (await req.json().catch(() => null)) as { token?: unknown; password?: unknown } | null;
  if (!body || typeof body.token !== "string" || typeof body.password !== "string") {
    return NextResponse.json({ code: "invite_invalid", message: "Invalid invite." }, { status: 410, headers: { "Cache-Control": "no-store" } });
  }
  const res = await callBackend(req, "/api/auth/invite/accept", { token: body.token, password: body.password });
  if (!res.ok) {
    const out = res instanceof NextResponse ? res : await passError(res);
    out.headers.set("Cache-Control", "no-store");
    return out;
  }
  const t = (await res.json()) as { accessToken: string; expiresInSeconds: number; refreshToken: string; user: unknown };
  const out = NextResponse.json({ accessToken: t.accessToken, expiresInSeconds: t.expiresInSeconds, user: t.user });
  out.headers.set("Cache-Control", "no-store");
  setRefreshCookie(out, t.refreshToken);
  return out;
}
