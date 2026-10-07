import { NextResponse, type NextRequest } from "next/server";
import { REFRESH_COOKIE, callBackend, clearRefreshCookie, forbidden, isSameOrigin } from "@/features/beauty-auth/server";

/** BFF: вихід. Відкликає refresh-токен на сервері (best effort) і завжди чистить cookie. */
export async function POST(req: NextRequest) {
  if (!isSameOrigin(req)) return forbidden();
  const rt = req.cookies.get(REFRESH_COOKIE)?.value;
  if (rt) await callBackend(req, "/api/auth/logout", { refreshToken: rt });
  const out = new NextResponse(null, { status: 204 });
  clearRefreshCookie(out);
  return out;
}
