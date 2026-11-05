import { NextResponse, type NextRequest } from "next/server";
import { callBackend, forbidden, isSameOrigin, passError } from "@/features/beauty-auth/server";

/** BFF: перегляд запрошення власника (§27.8). Токен лише в тілі POST; не логується, відповіді не кешуються. */
export async function POST(req: NextRequest) {
  if (!isSameOrigin(req)) return forbidden();
  const body = (await req.json().catch(() => null)) as { token?: unknown } | null;
  if (!body || typeof body.token !== "string") {
    return NextResponse.json({ code: "invite_invalid", message: "Invalid invite." }, { status: 410, headers: { "Cache-Control": "no-store" } });
  }
  const res = await callBackend(req, "/api/auth/invite/inspect", { token: body.token });
  if (!res.ok) {
    const out = res instanceof NextResponse ? res : await passError(res);
    out.headers.set("Cache-Control", "no-store");
    return out;
  }
  const d = (await res.json()) as { tenantName: string; email: string; expiresAt: string };
  return NextResponse.json(
    { tenantName: d.tenantName, email: d.email, expiresAt: d.expiresAt },
    { headers: { "Cache-Control": "no-store" } },
  );
}
