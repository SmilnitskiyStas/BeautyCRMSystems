import { NextResponse, type NextRequest } from "next/server";

const REFRESH_COOKIE = "beauty_rt";

/**
 * Перший (грубий) бар'єр: /beauty/* без сесійної cookie -> /login?next=...
 * Справжню перевірку виконує backend (JWT на кожен запит) і AuthGate на клієнті.
 * У демо-режимі (`NEXT_PUBLIC_USE_MOCK=1`) захист вимкнено.
 */
export function proxy(req: NextRequest) {
  if (process.env.NEXT_PUBLIC_USE_MOCK === "1") return NextResponse.next();
  if (req.cookies.has(REFRESH_COOKIE)) return NextResponse.next();
  const url = req.nextUrl.clone();
  const next = req.nextUrl.pathname + req.nextUrl.search;
  url.pathname = "/login";
  url.search = "";
  url.searchParams.set("next", next);
  return NextResponse.redirect(url);
}

export const config = { matcher: ["/beauty", "/beauty/:path*"] };
