# ADR-021: Mobile Booking Client (React Native + Expo, Universal App, Guest Model)

**Date:** 2026-11-09

**Status:** Proposed (human gate: new client surface + guest data model; stack and guest model already chosen by the user, see Context)

**Implementation state (worktree, 2026-11-09):** `mobile/` exists with Expo SDK 58 and Phase 1 screens (location
selection, service/time, checkout, appointments). Section 1 versions, section 3 web fallback and section 5 workspace
layout below describe the actual state. Phase 2 is not started.

**Author:** project-architect

**Deciders:** pending (user decisions on stack / single app / guest model recorded in plan `humming-snacking-kitten.md`)

**Related:** ADR-001 (stack), ADR-002 (RLS), ADR-006 (no self-registration), ADR-009 (locations), ADR-022 (public location code, catalog, mobile booking protection)

---

## Context

- Web online booking exists (`frontend/app/(public)/book`, `frontend/features/beauty-booking/`) and talks to the
  public API `api/public/{tenantSlug}/...` (`PublicBookingController`, `PublicTenantResolver`: tenant must be active
  and have the `beauty_booking` module; any failure = uniform `404 not_found`).
- There is no mobile client. The goal is the shortest possible flow:
  **location -> service -> specialist (or "any") + calendar/slots -> name/phone + confirm**.
- Location choice must work like Altegio-style links: a link/QR with a public location ID opens exactly that salon
  (each branch of a network has its own link). Otherwise: last used location on the device; otherwise geolocation and
  the nearest location (ADR-022).
- User decisions (plan, 2026-11): React Native + Expo; **one** universal app + deep links; **guest** with phone,
  appointments kept locally. MVP is phased: Phase 1 = book + cancel; Phase 2 = push + reschedule.
- ADR-006 forbids self-registration for staff. End-clients never had accounts: the public API is already
  guest-based (phone + per-appointment `publicToken`), so the mobile client must not introduce client accounts.
- Payments: `IPaymentService` has no real provider yet (gate decision A3) — only "pay at venue" / `cash` is usable.

---

## Decision

### 1. Stack and placement

- **React Native + Expo** (managed workflow, Continuous Native Generation / config plugins; no hand-edited
  `ios/` / `android/` folders committed). Builds and store submission via **EAS Build / EAS Submit**.
- TypeScript `strict`. Libraries: `expo-router` (file-based routing + deep links), TanStack Query (server state),
  `zustand` (small client state: selected location, draft booking), `zod` (runtime validation of API responses),
  `expo-secure-store`, `expo-location`, `expo-linking`, `i18next` + `react-i18next` + `expo-localization`
  (Ukrainian first; no hardcoded copy in components). Also in use, not named above: `expo-camera` (QR scan),
  `expo-crypto` (idempotency UUID), `@react-native-async-storage/async-storage` (non-secret local cache).
- Code lives in **`mobile/`** in this repository (modular monolith repo per ADR-001).
- **Versions (from `mobile/package.json`, 2026-11-09):** Expo `~58.0.23` (SDK 58), `expo-router ~58.0.30`,
  `react-native 0.88.1`, `react 19.3.0`, TypeScript `~6.0.3`, `zod ^4.6.7`, `zustand ^5.1.0`, `i18next ^27.0.1`,
  `@tanstack/react-query ^5.107.1`, Jest `~30.5.2` with `jest-expo ~58.0.14`. The versions were checked at setup
  (CLAUDE.md rule); bumping them is a normal change and does not need a new ADR.

### 2. One universal app

- A single store listing / bundle ID for the platform. The salon context is chosen at runtime, never baked into the
  build. Per-tenant white-label apps are **out of scope**; introducing them requires a new ADR.
- Location selection precedence (first match wins):
  1. **Deep link** `/l/{publicCode}` (cold or warm start) — becomes the new saved location;
  2. **Saved last location** on the device;
  3. **Nearby** via geolocation (permission requested only at this step, ADR-022 §4);
  4. **Manual**: scan QR / enter code (fallback when permission denied or nothing nearby).
- A saved location that stops resolving (deactivated, ADR-009) is cleared and the flow falls through to step 3.

### 3. Deep links

- Canonical shareable form: **`https://{domain}/l/{publicCode}`** — iOS Universal Link + Android App Link.
  `apple-app-site-association` and `/.well-known/assetlinks.json` are served from `{domain}` by the web frontend
  (owner: `devops-engineer`). QR codes and links in the admin always use the https form.
- Custom scheme **`beautycrm://l/{publicCode}`** is registered as a secondary entry point (in-app/dev use, fallback).
  It is not verified by the OS (any app can claim it), so it must carry nothing but the public code.
- **Web fallback** (app not installed): the Next.js route `/l/[code]` resolves the code via ADR-022 resolve and
  redirects to the existing web booking flow for that tenant/location. One link works for app and web.
  *State (2026-11-09):* the route is **not present** in `frontend/app/` in this worktree; `apple-app-site-association`
  and `assetlinks.json` are not present either. Not implemented yet.
- **Mobile link parsing** is implemented in `mobile/src/lib/deeplink.ts` (strict: other domains, `http`, userinfo,
  ports and extra path segments are rejected; the code is normalized with the ADR-022 rules). Zod is used for API responses,
  not for link payloads.
- Link payload is **untrusted input**: validated against the PublicCode format (ADR-022 §1) with `zod` before any
  request; links never contain appointment tokens, phone numbers, or other PII.

### 4. Guest model and local data

- No client account, no login, no server-side client profile created by the app beyond what the existing public
  booking API already stores per appointment.
- Stored on device:
  | Data | Storage | Notes |
  |---|---|---|
  | Name, phone | `expo-secure-store` | PII; prefilled in step 4, editable; consent checkbox never pre-checked (same rule as web) |
  | `publicToken` per appointment | `expo-secure-store`, **one key per appointment** | bearer credential for view/cancel; per-key storage avoids SecureStore value-size limits |
  | Appointment index (local id, tenantSlug, location name, service name, `startsAt` snapshot) | app storage (AsyncStorage or equivalent) | no tokens, no phone; display/offline cache only |
  | Last location (`publicCode`, display name) | app storage | never raw coordinates |
- **Server is the source of truth** for appointment status: "My appointments" refreshes each entry via the existing
  get-by-token endpoint; the local snapshot is only shown offline and marked as possibly stale. Booking and cancel
  are **online-only** (no offline mutation queue in Phase 1).
- Cancel shows refund terms **as returned by the API** (ADR-007); no percentages hardcoded in the app.
- A "Delete my data on this device" action wipes all of the above.
- Accepted limitation: uninstall / new phone loses the local list. Recovery (e.g. phone OTP) is out of scope of this ADR.

### 5. Shared booking core: `packages/booking-core`

- Extract the framework-free TypeScript from `frontend/features/beauty-booking/` into an **npm workspace package
  `packages/booking-core`**: `api/client.ts`, `api/http-client.ts`, `types.ts`, `errors.ts`, `idempotency.ts`,
  `dates.ts`. UI copy (`content.ts`), React hooks, the mock client and Next-specific code stay in each app.
- Package rules (enforced by lint/tsconfig in the package):
  - no imports of React, Next.js, React Native, DOM (`window`, `document`) or `process.env`;
  - configuration is injected: `baseUrl`, `fetch` implementation, idempotency-key generator, clock;
  - shipped as TypeScript source (internal, never published); consumers transpile it
    (Next `transpilePackages`, Metro monorepo support).
- A root `package.json` with workspaces is introduced. **Actual state:** `workspaces: ["frontend", "packages/*"]`.
  `mobile/` is **not** a root workspace: it has its own `mobile/package.json` (`@beauty/mobile`) and is installed
  separately. The original text listed `mobile` among the workspaces; that is replaced by this state. A `mobile/`
  lock file is not present in this worktree (not verified whether it is tracked in git). The Node `worker` joins the
  workspace only if/when it needs the package (not required by this ADR).
- **Consumption:** `frontend/package.json` depends on `"@beauty/booking-core": "*"` (workspace link) and uses
  `transpilePackages` in `frontend/next.config.ts`. `mobile/` does **not** list the package in its dependencies; it
  resolves `@beauty/booking-core` through two aliases: the TypeScript path `../packages/booking-core/src/index.ts`
  in `mobile/tsconfig.json`, and Metro `extraNodeModules` plus `watchFolders` in `mobile/metro.config.js`.
  Metro also sets `disableHierarchicalLookup` and `nodeModulesPaths` to `mobile/node_modules`, so that `react` and
  `zod` are not loaded twice from the web root (a duplicate React breaks the app).
- Package entry is `packages/booking-core/src/index.ts`. Files: `client.ts`, `http-client.ts`, `types.ts`,
  `errors.ts`, `idempotency.ts`, `dates.ts`, `tenant.ts` (flat `src/`, not `api/`). Rule enforcement by lint/tsconfig
  in the package: `packages/booking-core/tsconfig.json` exists; the lint rule was not verified.
- Wire types stay hand-written in Phase 1; the contract source remains `.claude/docs/beauty-contracts.md` §12.
  Generating types from a static `openapi.json` is a follow-up, not a prerequisite.
- Web behaviour must not change: existing frontend unit + Playwright e2e are the regression gate for the extraction.

### 6. Phase boundaries

- **Phase 1:** booking (create, idempotent retry) and cancel by token; "My appointments"; deep links; nearby.
  Payment = "pay at venue" only — no payment SDK in the app until a provider is chosen (A3).
  *State (2026-11-09):* screens `app/index.tsx` (location), `service.tsx`, `time.tsx`, `checkout.tsx`,
  `appointments.tsx`, `l/[code].tsx` (deep link) exist; catalog client `src/api/catalog.ts`; unit tests in `src/**/__tests__`.
  Not verified in this pass: cancel-by-token flow in the UI and the "delete my data" action.
- **Phase 2:** push and reschedule. Push tokens will be registered **against appointment `publicToken`s**, not a
  user account, keeping the guest model. Reschedule needs a new public endpoint. Both get their own contract/ADR
  update; they are not decided here.

---

## Consequences

### Positive

1. One codebase for iOS + Android; Expo/EAS removes most native build maintenance.
2. One app for all tenants: a single store listing, one release train, salons onboard by printing a QR code.
3. The guest model keeps the trust boundary from ADR-006 intact (no client auth surface, no passwords).
4. `booking-core` gives web and mobile one HTTP client, one error model, one idempotency scheme — contract drift is
   caught in one place.
5. https deep links degrade gracefully to the web flow when the app is not installed.

### Negative / accepted trade-offs

1. Local-only history: lost on uninstall / device change; no cross-device sync.
2. `publicToken` on the device is a bearer credential — a compromised/unlocked device can cancel the bookings.
   Mitigated by SecureStore (Keychain / Keystore), but not eliminated.
3. Universal app means the platform brand, not the salon brand, is on the home screen; some networks may ask for
   white-label (new ADR if so).
4. Introducing npm workspaces touches the frontend build (path resolution, lockfile at the root) — a one-time
   migration with regression risk for the web.
5. Store compliance work: privacy labels / Data safety form (phone, name, approximate location), review cycles.
6. Custom scheme is spoofable; we rely on it carrying no secrets.

---

## Alternatives Considered

### 1. Flutter
**Rejected:** no code sharing with the existing TypeScript booking client; second language in the repo; the user chose RN + Expo.

### 2. Bare React Native (no Expo)
**Rejected:** more native maintenance (Xcode/Gradle upgrades) for no MVP benefit; Expo config plugins cover
location, secure storage, linking, and future push/attestation modules.

### 3. PWA / web-only (reuse the Next.js flow)
**Rejected:** user explicitly wants a store app; weaker push on iOS, no App Attest / Play Integrity (needed for
ADR-022 §5), no reliable QR/deep-link entry from the camera into an installed app.

### 4. White-label app per tenant
**Rejected for MVP:** N store listings, per-tenant signing and review cycles; contradicts "one universal app".

### 5. Client accounts (login with phone/password or OTP) and server-side "My appointments"
**Rejected for MVP:** new auth surface and PII store for end-clients; needs an SMS/OTP provider not yet chosen;
user chose guest + local storage. Revisit together with phone OTP (ADR-022 §5) if cross-device history is required.

### 6. Copy the booking client into `mobile/` instead of a shared package
**Rejected:** two diverging copies of the contract client, error codes and idempotency logic.

### 7. Publish `booking-core` to a registry / build step with compiled output
**Rejected:** unnecessary release overhead for an internal package with two consumers in the same repo.

---

## Scope of Truth

This ADR is authoritative for: the mobile stack and its placement (`mobile/`); the single-universal-app rule; the
location-selection precedence; the deep-link URL formats and the rule that links carry only a public code; the
guest/local-storage data model on the device; the existence, boundaries and rules of `packages/booking-core`;
the Phase 1 / Phase 2 split. The PublicCode format, the resolve/nearby endpoints, geolocation handling on the
server, and mobile booking protection are owned by **ADR-022**. Changes require a new ADR.

---

## References

- Plan: `C:\Users\stass\.claude\plans\humming-snacking-kitten.md` — "Архітектурні рішення", phases
- `frontend/features/beauty-booking/` — source of `booking-core`
- `backend/BeautyCrm.Api/Controllers/PublicBookingController.cs`, `backend/BeautyCrm.Application/Features/BeautyPublicBooking/`
- `.claude/docs/beauty-contracts.md` §12 — public booking contract
- Skill `offline-storage` — local storage design
