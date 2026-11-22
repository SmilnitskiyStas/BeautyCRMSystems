import { NextResponse, type NextRequest } from "next/server";
import {
  TICKET_COOKIE,
  callBackend,
  clearTicketCookie,
  forbidden,
  isSameOrigin,
  passError,
  setRefreshCookie,
} from "@/features/beauty-auth/server";

/** BFF: вибір бізнесу (§31.3). Квиток береться з cookie `beauty_lt` і одноразовий: cookie видаляється за будь-якого результату. */
export async function POST(req: NextRequest) {
  if (!isSameOrigin(req)) return forbidden();
  const body = (await req.json().catch(() => null)) as { tenantId?: unknown } | null;
  if (!body || typeof body.tenantId !== "string") {
    return NextResponse.json({ code: "validation_failed", message: "Invalid body." }, { status: 422 });
  }
  const ticket = req.cookies.get(TICKET_COOKIE)?.value;
  if (!ticket) {
    const out = NextResponse.json({ code: "login_ticket_invalid", message: "No login ticket." }, { status: 401 });
    out.headers.set("Cache-Control", "no-store");
    clearTicketCookie(out);
    return out;
  }

  const res = await callBackend(req, "/api/auth/login/select", { loginTicket: ticket, tenantId: body.tenantId });
  let out: NextResponse;
  if (!res.ok) {
    out = res instanceof NextResponse ? res : await passError(res);
  } else {
    const t = (await res.json()) as { accessToken: string; expiresInSeconds: number; refreshToken: string; user: unknown };
    out = NextResponse.json({ accessToken: t.accessToken, expiresInSeconds: t.expiresInSeconds, user: t.user });
    setRefreshCookie(out, t.refreshToken);
  }
  out.headers.set("Cache-Control", "no-store");
  clearTicketCookie(out);
  return out;
}
