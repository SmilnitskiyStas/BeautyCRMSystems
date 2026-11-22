import { NextResponse, type NextRequest } from "next/server";
import { callBackend, forbidden, isSameOrigin, passError, setRefreshCookie, setTicketCookie } from "@/features/beauty-auth/server";

/**
 * BFF: вхід (§31.2, §31.11). Refresh-токен і квиток входу йдуть лише в httpOnly-cookie й НЕ повертаються в JSON.
 * `tenant` пересилається, лише якщо це непорожній рядок (сумісність із кешованими збірками до фази 3).
 */
export async function POST(req: NextRequest) {
  if (!isSameOrigin(req)) return forbidden();
  const body = (await req.json().catch(() => null)) as { tenant?: unknown; email?: unknown; password?: unknown } | null;
  if (!body || typeof body.email !== "string" || typeof body.password !== "string") {
    return NextResponse.json({ code: "validation_failed", message: "Invalid body." }, { status: 422 });
  }
  const payload: Record<string, string> = { email: body.email, password: body.password };
  if (typeof body.tenant === "string" && body.tenant.trim()) payload.tenant = body.tenant;
  const res = await callBackend(req, "/api/auth/login", payload);
  if (!res.ok) return res instanceof NextResponse ? res : passError(res);

  const t = (await res.json()) as Record<string, unknown>;
  if (t.selectionRequired === true) {
    const ttl = Number(t.ticketExpiresInSeconds);
    const maxAge = Number.isFinite(ttl) && ttl > 0 ? ttl : 180;
    const out = NextResponse.json({ selectionRequired: true, ticketExpiresInSeconds: maxAge, businesses: t.businesses });
    out.headers.set("Cache-Control", "no-store");
    if (typeof t.loginTicket === "string") setTicketCookie(out, t.loginTicket, maxAge);
    return out;
  }
  const out = NextResponse.json({ accessToken: t.accessToken, expiresInSeconds: t.expiresInSeconds, user: t.user });
  out.headers.set("Cache-Control", "no-store");
  setRefreshCookie(out, String(t.refreshToken));
  return out;
}
