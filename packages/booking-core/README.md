# @beauty/booking-core

Internal TypeScript-source package shared by `frontend` (Next) and the future `mobile` app (ADR-021 §5).
Not published; consumers transpile it (Next `transpilePackages`, Metro monorepo support).

Rules: no React / Next / React Native / DOM / `process.env`. Configuration is injected
(`createHttpBookingApi({ baseUrl, fetch })`, `createKeyHolder(generate)`, `resolveTenant(route, envSlug)`).
Enforced by `tsconfig.json` (`lib: ["esnext"]`, `types: []`) via `npm run typecheck -w @beauty/booking-core`.
UI copy (`content.ts`), hooks, mock client and route helpers stay in each app.
