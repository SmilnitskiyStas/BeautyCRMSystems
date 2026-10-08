import type { NextConfig } from "next";

const isProd = process.env.NODE_ENV === "production";

/** Origin backend для `connect-src` (з `NEXT_PUBLIC_API_URL`); порожньо = той самий origin, додаткового дозволу не треба. */
function apiOrigin(): string | null {
  const raw = process.env.NEXT_PUBLIC_API_URL;
  if (!raw) return null;
  try {
    return new URL(raw).origin;
  } catch {
    return null;
  }
}

/**
 * CSP без nonce: суворий `default-src 'self'`. Inline-скрипти потрібні Next для гідратації (RSC-payload), тому
 * `script-src` містить `'unsafe-inline'`; nonce вимагає динамічного рендеру кожної сторінки (несумісно зі статичною
 * оболонкою `cacheComponents`) — окрема задача посилення. `'unsafe-eval'` лише в dev (React debug).
 */
function contentSecurityPolicy(): string {
  const api = apiOrigin();
  const directives = [
    "default-src 'self'",
    `script-src 'self' 'unsafe-inline'${isProd ? "" : " 'unsafe-eval'"}`,
    "style-src 'self' 'unsafe-inline'",
    "img-src 'self' data: blob:",
    "font-src 'self'",
    `connect-src 'self'${api ? ` ${api}` : ""}${isProd ? "" : " ws: wss:"}`,
    "object-src 'none'",
    "base-uri 'self'",
    "form-action 'self'",
    "frame-ancestors 'none'",
    ...(isProd ? ["upgrade-insecure-requests"] : []),
  ];
  return directives.join("; ");
}

const securityHeaders = [
  { key: "Content-Security-Policy", value: contentSecurityPolicy() },
  { key: "X-Content-Type-Options", value: "nosniff" },
  { key: "Referrer-Policy", value: "strict-origin-when-cross-origin" },
  { key: "X-Frame-Options", value: "DENY" },
  { key: "Permissions-Policy", value: "camera=(), microphone=(), geolocation=()" },
  ...(isProd ? [{ key: "Strict-Transport-Security", value: "max-age=63072000; includeSubDomains" }] : []),
];

const nextConfig: NextConfig = {
  cacheComponents: true,
  partialPrefetching: true,
  poweredByHeader: false,
  async headers() {
    return [
      { source: "/:path*", headers: securityHeaders },
      // Публічний токен запису в шляху: не віддаємо його у Referer і не кешуємо (узгоджено з §12).
      {
        source: "/book/appointment/:path*",
        headers: [
          { key: "Referrer-Policy", value: "no-referrer" },
          { key: "Cache-Control", value: "no-store" },
        ],
      },
    ];
  },
  turbopack: {
    rules: {
      "*.css": {
        loaders: ["@tailwindcss/turbopack"],
        as: "*.css",
      },
    },
  },
};

export default nextConfig;
