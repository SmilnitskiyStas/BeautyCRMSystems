import { NextResponse, type NextRequest } from "next/server";
import { callBackend, forbidden, isSameOrigin, passError } from "@/features/beauty-auth/server";

/** BFF: запит на скидання пароля (§31.7.1). Відповідь завжди 202 без тіла (крім 422/429). */
export async function POST(req: NextRequest) {
  if (!isSameOrigin(req)) return forbidden();
  const body = (await req.json().catch(() => null)) as { email?: unknown } | null;
  if (!body || typeof body.email !== "string") {
    return NextResponse.json({ code: "invalid_email", message: "Invalid body." }, { status: 422 });
  }
  const res = await callBackend(req, "/api/auth/password/forgot", { email: body.email });
  const out = !res.ok ? (res instanceof NextResponse ? res : await passError(res)) : new NextResponse(null, { status: 202 });
  out.headers.set("Cache-Control", "no-store");
  return out;
}
