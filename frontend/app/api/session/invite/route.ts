import { NextResponse, type NextRequest } from "next/server";
import { callBackend, forbidden, isSameOrigin, passError } from "@/features/beauty-auth/server";

/** BFF: прийняття запрошення (токен запрошення не проходить через CORS і не логуватиметься на клієнті). */
export async function POST(req: NextRequest) {
  if (!isSameOrigin(req)) return forbidden();
  const body = (await req.json().catch(() => null)) as { token?: unknown; fullName?: unknown; password?: unknown } | null;
  if (!body || typeof body.token !== "string" || typeof body.fullName !== "string" || typeof body.password !== "string") {
    return NextResponse.json({ code: "validation_failed", message: "Invalid body." }, { status: 422 });
  }
  const res = await callBackend(req, "/api/auth/invites/accept", {
    token: body.token,
    fullName: body.fullName,
    password: body.password,
  });
  if (!res.ok) return res instanceof NextResponse ? res : passError(res);
  return new NextResponse(null, { status: 201 });
}
