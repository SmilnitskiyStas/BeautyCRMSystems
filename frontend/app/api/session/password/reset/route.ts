import { NextResponse, type NextRequest } from "next/server";
import { callBackend, forbidden, isSameOrigin, passError } from "@/features/beauty-auth/server";

/** BFF: встановлення нового пароля за токеном (§31.7.2). `204` без автоматичного входу. */
export async function POST(req: NextRequest) {
  if (!isSameOrigin(req)) return forbidden();
  const body = (await req.json().catch(() => null)) as { token?: unknown; password?: unknown } | null;
  if (!body || typeof body.token !== "string" || typeof body.password !== "string") {
    return NextResponse.json({ code: "validation_failed", message: "Invalid body." }, { status: 422 });
  }
  const res = await callBackend(req, "/api/auth/password/reset", { token: body.token, password: body.password });
  const out = !res.ok ? (res instanceof NextResponse ? res : await passError(res)) : new NextResponse(null, { status: 204 });
  out.headers.set("Cache-Control", "no-store");
  return out;
}
