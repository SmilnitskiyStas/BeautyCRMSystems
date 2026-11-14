# ADR-022: Public Location Code, Cross-Tenant Public Catalog over RLS, Geolocation Privacy, Mobile Booking Protection

**Date:** 2026-11-09

**Status:** Proposed. **Human gate:** touches the tenant-isolation / security boundary (ADR-002) and adds a DB role.
`security-reviewer` review is mandatory before `Accepted`.

**Implementation status (worktree, 2026-11-09):** implemented in backend (migration
`20261109075625_beauty_public_location_catalog`, `PublicCatalogController`, `PublicCatalogService`, `DbRoleGuard` checks)
and in `mobile/` (`src/api/catalog.ts`, `src/lib/deeplink.ts`, `src/lib/locationSource.ts`). Status stays `Proposed`
until the security review is done. Known gaps between this text and the code are listed in "Implementation notes".

**Author:** project-architect (text aligned to the implementation by documentation-writer)

**Deciders:** pending

**Related:** ADR-002 (RLS, FORCE, no BYPASSRLS), ADR-006 (public endpoints, rate limits), ADR-009 (`beauty_locations`,
deactivation), ADR-021 (mobile client, deep links)

---

## Context

- ADR-021 needs: (a) a link/QR `https://{domain}/l/{publicCode}` that identifies **one location of one tenant**;
  (b) "nearest location" from the device position when nothing is saved and no link was opened; (c) a manual fallback
  when geolocation is denied (city search or code entry).
- Today the only public identifier is `tenants.slug`; `beauty_locations` had no public code and no coordinates.
  Every existing public endpoint runs **inside one tenant** (`PublicTenantResolver` -> `UseTenant`, then RLS).
- Both (a) and (b)/(c) need a **cross-tenant read**: the tenant is unknown until the code/position/city is resolved.
  ADR-002 forbids BYPASSRLS for the runtime role (`DbRoleGuard` fails fast), and every `beauty_*` table and
  `tenants` is `FORCE ROW LEVEL SECURITY` (the table owner is subject to RLS too).
- Existing precedents for pre-tenant lookups are session-variable policies on the runtime role
  (`tenants_login_lookup` by `app.tenant_slug`, `channel_webhook_lookup` by `app.channel_id`). They open exactly one
  row by an exact key; they cannot serve a geographic "nearest N" or a city search.
- Device position is personal data. The plan requires: not stored on the server, used once for nearby.
- Public booking protection today: optional CAPTCHA (`PublicBooking:CaptchaRequired`, `ICaptchaVerifier`,
  default `RejectAllCaptchaVerifier` = fail closed), honeypot field `Website`, per-IP fixed-window limits
  (`public-read` 120 / `public-token` 30 / `public-write` 10 per minute), `MaxActiveBookingsPerPhone` (3),
  `MaxCreatesPerPhonePerHour` (5), idempotency keys. A web CAPTCHA widget does not work in a native app.

---

## Decision

### 1. `Location.PublicCode`

- Column `beauty_locations.public_code varchar(8) NOT NULL`, **globally unique** (unique index
  `ux_beauty_locations_public_code` across all tenants), CHECK `ck_beauty_locations_public_code`:
  `public_code ~ '^[0-9A-HJKMNP-TV-Z]{8}$'`.
- Format: **8 characters of Crockford Base32** (no I, L, O, U) = 40 bits. Generation: the column has a DB default
  `public.beauty_generate_public_code()` (VOLATILE SQL function; 5 bits per character taken from
  `gen_random_uuid()` bytes). The same function backfills existing rows during the migration. The application does not
  generate codes itself (`EfBeautyStore.CreateAsync` does not set `PublicCode`).
- Collision handling: a unique violation on `ux_beauty_locations_public_code` (SQLSTATE 23505) re-runs the whole
  create transaction, up to 5 attempts in total (`EfBeautyStore.Locations.cs`, `RetryOnPublicCodeCollisionAsync`,
  `MaxPublicCodeAttempts = 5`); other errors are not retried. After the 5th failure the INSERT error propagates.
  A collision is negligible at this size.
- Input normalization: trim, uppercase, drop `-` and whitespace, Crockford aliases (`O`->`0`, `I`/`L`->`1`).
  The normalization is done **inside the resolve function** (`beauty_public_resolve_location`); the service only
  checks the shape (`^[0-9A-HJKMNP-TV-ZOIL]{4}-?[0-9A-HJKMNP-TV-ZOIL]{4}$`, case-insensitive, length <= 9).
  The mobile app normalizes links and manual input in `mobile/src/lib/deeplink.ts` (same rules).
  Displayed to humans as `XXXX-XXXX`; URLs use the bare 8 characters.
- The code is a **public identifier, not a secret and not a credential**: it grants nothing beyond what the
  location's public booking page already shows.
- **Immutable** after issue in this ADR's scope (printed QR codes must keep working). `PublicCode` is never written by
  the update path (`SetAfterSaveBehavior(Ignore)`, LocationService does not accept it). Rotation/revocation is a future
  decision.

### 2. Cross-tenant public catalog: SECURITY DEFINER functions owned by a dedicated NOLOGIN role

**Chosen option:** three `SECURITY DEFINER` SQL functions owned by a new role **`beautycrm_catalog`**
(`NOLOGIN NOSUPERUSER NOBYPASSRLS NOINHERIT`). The role sees rows only through **role-scoped** SELECT policies and
**column-level** grants. The runtime role `beautycrm_app` gets `EXECUTE` on the three functions and nothing else.

```sql
CREATE ROLE beautycrm_catalog NOLOGIN NOSUPERUSER NOBYPASSRLS NOINHERIT;  -- if missing (needs CREATEROLE) or pre-provisioned by ops

-- Column-level grants: only public fields; no client phones, no settings, no other tables.
GRANT SELECT (id, tenant_id, name, address, city, timezone, is_active,
              public_code, latitude, longitude, is_discoverable) ON beauty_locations TO beautycrm_catalog;
GRANT SELECT (id, slug, status, modules) ON tenants TO beautycrm_catalog;

-- Role-scoped policies (TO beautycrm_catalog). NOTE: a policy applies to the named role AND to every role that has the
-- privileges of it through INHERIT (pg_has_role(.., 'USAGE')), not only when current_user = beautycrm_catalog. Hence every
-- member of beautycrm_catalog must be INHERIT FALSE (the migrator: WITH INHERIT FALSE, SET TRUE) - enforced by the migration
-- and DbRoleGuard. Existing policies TO PUBLIC also apply to the catalog role (see rule below).
CREATE POLICY public_catalog_read ON beauty_locations FOR SELECT TO beautycrm_catalog USING (is_active);
CREATE POLICY public_catalog_read ON tenants FOR SELECT TO beautycrm_catalog
  USING (status = 'active' AND 'beauty_booking' = ANY (modules));

-- Functions: STABLE, fixed search_path, schema-qualified, fixed output columns, server-side clamping.
CREATE FUNCTION public.beauty_public_resolve_location(p_code text)
  RETURNS TABLE (public_code text, tenant_slug text, location_id uuid, name text, address text,
                 city text, timezone text)
  LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp;

CREATE FUNCTION public.beauty_public_nearby_locations(p_lat double precision, p_lng double precision,
                                                      p_radius_km double precision, p_limit integer)
  RETURNS TABLE (public_code text, tenant_slug text, location_id uuid, name text, address text,
                 city text, timezone text, distance_km numeric)
  LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp;

CREATE FUNCTION public.beauty_public_search_locations_by_city(p_city text, p_limit integer)
  RETURNS TABLE (public_code text, tenant_slug text, location_id uuid, name text, address text,
                 city text, timezone text)
  LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp;

ALTER FUNCTION ... OWNER TO beautycrm_catalog;   -- all three (migration grants CREATE on schema public only for this step)
REVOKE ALL ON FUNCTION ... FROM PUBLIC;          -- all three
GRANT EXECUTE ON FUNCTION ... TO beautycrm_app; -- all three; done by the migration if beautycrm_app exists, else NOTICE
```

Function rules (binding for the implementation; the bodies are in the migration):

- Eligibility = the same rules as `PublicTenantResolver`: tenant `active` + `beauty_booking` module; location
  `is_active`. Nearby and city search additionally require `is_discoverable`; nearby also requires non-null coordinates.
- `resolve` matches the normalized code exactly; returns 0 or 1 row.
- `nearby`: coordinates outside -90..90 / -180..180 or NaN -> no rows (the service rejects them earlier with 422).
  Input is rounded to 3 decimals (~110 m) inside the function. Radius: `p_radius_km` NULL or <= 0 -> 30 km default;
  otherwise `least(p_radius_km, 30)` (so the cap is 30 km; the API may pass a smaller radius). Limit clamped to 1..10.
  Bounding-box prefilter on `(latitude, longitude)` then Haversine; `distance_km` rounded to 0.1. Ordered by distance.
  The cap in the function is the source of truth; changing it requires a migration.
- `search_by_city`: exact case-insensitive match on `city` (`lower(btrim(...))`), no wildcards, length 2..100 after
  trim; limit clamped to 1..10; ordered by name. Partial expression index `ix_beauty_locations_discoverable_city`.
- No dynamic SQL; output columns are fixed; the functions never return `tenant_id`, phone numbers, or any column
  not listed above.
- **Invariants checked at startup (`DbRoleGuard`, any environment) and in tests:** `beautycrm_catalog` is not
  superuser / BYPASSRLS / LOGIN; `beautycrm_app` is **not** a member of `beautycrm_catalog` (no `SET ROLE` path); the
  three functions are owned by `beautycrm_catalog`, are SECURITY DEFINER and are not executable by PUBLIC. Also:
  the role is NOINHERIT and is not a member of any role; **no member has `inherit_option`** (M1); each function has exactly
  `SET search_path = pg_catalog, pg_temp`; the set of `beauty_public_*` functions is exactly the three listed (an extra or
  missing one fails the start). The checks are skipped when the role does not exist yet (migration not applied).
- **Rule for every future definer function owned by `beautycrm_catalog`:** existing policies `TO PUBLIC` (tenant isolation
  etc.) also apply to the catalog role, and the role-scoped policies only *add* visibility. The function body must itself
  check `is_active` / `status = 'active'` / `'beauty_booking' = ANY (modules)` in its WHERE - never rely on a policy for eligibility.
- `beauty_locations.public_code` is immutable at DB level (trigger `trg_beauty_locations_public_code_immutable`, `restrict_violation`).
- Role creation needs `CREATEROLE` (or a pre-provisioning script run by ops). The migration creates the role if it is
  missing and makes the migrator a member of it `WITH INHERIT FALSE, SET TRUE` (the SET option is needed to hand function ownership to the role; INHERIT must be false). A pre-provisioned membership with other options is re-granted by the migration or the migration fails with instructions. Owner:
  `database-engineer` + `devops-engineer`. Runbook: `.claude/docs/runbook.md` §1a.
- **Several grantors (`pg_auth_members`, PostgreSQL 16):** a membership can have one row per grantor, each with its own
  `inherit_option`. The migration only checks the migrator's own row (`set_option AND NOT inherit_option`) and does not
  remove other rows. `DbRoleGuard` checks every row of `beautycrm_catalog` members and fails startup if any has
  `inherit_option = true`. So a second grantor who adds an INHERIT TRUE membership is not fixed by re-running the
  migration; ops must revoke that grant (`REVOKE ... GRANTED BY`). Not verified against a live PG16 in this worktree.

Why this option: the runtime role's effective row visibility is **unchanged** — every existing policy and grant
stays as is; the only new cross-tenant path is three reviewed functions with fixed, public-only outputs, executed by a
role that cannot log in, cannot bypass RLS, and can read only whitelisted columns of two tables. Even a bug inside a
function is bounded by the column grants and the role-scoped policies. It also needs no second connection pool or
credential.

### 3. Schema additions on `beauty_locations`

| Column | Type | Rule |
|---|---|---|
| `public_code` | `varchar(8) NOT NULL` | §1, unique index `ux_beauty_locations_public_code`, CHECK `ck_beauty_locations_public_code` |
| `latitude` | `double precision NULL` | CHECK `ck_beauty_locations_latitude`: NULL or -90..90 |
| `longitude` | `double precision NULL` | CHECK `ck_beauty_locations_longitude`: NULL or -180..180; CHECK `ck_beauty_locations_coords_pair`: both NULL or both set |
| `city` | `varchar(100) NULL` | free text, trimmed by the service; used only by city search (exact match, case-insensitive) |
| `is_discoverable` | `boolean NOT NULL DEFAULT false` | location appears in nearby and city search only if `true` |

- Coordinates are the **business address** of the salon, entered by owner/admin in the location form (`PUT /api/beauty/locations/{id}`) —
  not personal data.
- Indexes: `ix_beauty_locations_discoverable_coords` on `(latitude, longitude)` partial
  `WHERE is_discoverable AND is_active AND latitude IS NOT NULL`; `ix_beauty_locations_discoverable_city` on
  `lower(city)` partial `WHERE is_discoverable AND is_active AND city IS NOT NULL`. PostGIS / `earthdistance` may
  replace Haversine later **behind the same function signature** without a new ADR.
- `is_discoverable` defaults to `false`: a location is never listed to strangers without opt-in. The switch is
  **per location** (not per tenant).

### 4. Public endpoints (`PublicCatalogController`, `api/public`, no `{tenantSlug}`, no `PublicTenantAttribute`)

| Endpoint | Body / query | Response | Rate limit (per IP, fixed window 60 s) |
|---|---|---|---|
| `GET /api/public/resolve/{publicCode}` | path | `200 {publicCode, tenantSlug, locationId, name, address, city, timezone}` | `public-token` (30, anti-guessing) |
| `POST /api/public/locations/nearby` | JSON `{lat, lng, radiusKm?}` (max body 1024 bytes) | `200 {items: [{publicCode, tenantSlug, locationId, name, address, city, timezone, distanceKm}]}` | `public-nearby` (10) |
| `POST /api/public/locations/search` | body `{city}`, 2..100 chars | `200 {items: [PublicLocationRef]}` (max 10) | `public-search` (20) |

- `resolve`: any failure (bad format, unknown code, inactive location, suspended tenant, module disabled) returns the
  same `404 not_found` as the existing public API — no oracle distinguishing "exists but inactive".
- `nearby` is **POST** (deviation from the plan's `GET ?lat&lng`): keeps coordinates out of URLs, access logs,
  proxy/CDN logs and browser history. `lat`/`lng` must be finite and in range; `radiusKm` must be a positive number
  and is capped at 30 km (`422 invalid_coordinates` / `422 invalid_radius`). The request has **no `limit` field**: the
  response is always at most 10 items. An empty list is a valid answer.
- `search`: `POST /api/public/locations/search` only, body `{city}`. The city is not put in the URL, so it stays out of
  access logs (the earlier `GET ...?city=` was removed, not deprecated). `422 invalid_city` (trimmed length 2..100, no
  control characters). Matching is exact, not prefix or substring. Results: max 10, ordered by name; no distance.
- Validation errors use **`422`** with the standard error body `{code, message}` (the ADR previously said 400 `validation_error`;
  the code and tests use 422).
- Every catalog response carries `Cache-Control: no-store` and `Referrer-Policy: no-referrer`.
- `PublicCatalogController` carries `[PublicClient]`, so the `X-Booking-Client` / `X-App-Version` labels and the 426 kill
  switch (§6) apply to the catalog routes too. The startup check for mismatched protection modes (§6 item 5) applies to the
  whole public API, not only to catalog routes.
- Reserved tenant slugs: `resolve`, `locations`, `integrity` (`PublicTenantResolver.ReservedSlugs`,
  `PublicTokens.cs`; rejected on tenant creation in `UserAdminService`) so that `api/public/{tenantSlug}/...` never
  conflicts with the catalog routes. `integrity` is reserved for the Phase 2 challenge path (§6); no such route exists yet.
- Rate-limit key (`PublicRateLimit.KeyFor`, per policy, fixed window): IPv4 by address; IPv6 by its `/64` prefix
  (the low 64 bits are zeroed); IPv4-mapped IPv6 is mapped to IPv4. The client IP comes from `RemoteIpAddress` after
  `UseBeautyForwardedHeaders`, which trusts only the proxies listed in `Beauty:TrustedProxies`. Without that setting behind
  a reverse proxy, all clients share the proxy's key.
- After resolving, clients use the existing tenant-scoped endpoints with `tenantSlug` — the booking flow stays
  under full RLS exactly as today.
- The contract is `backend/openapi.json` (generated; contains the three `PublicCatalog` paths) and the hand-written
  section §29 of `.claude/docs/beauty-contracts.md` (added; endpoint table, error codes, rate-limit classes).

### 5. Geolocation is personal data

- **Client (ADR-021):** permission requested only at the "nearby" step (no deep link and no saved location);
  foreground "when in use" only (`expo-location`, `requestForegroundPermissionsAsync`); the position is sent once in the
  nearby request; the chosen `publicCode` is saved, coordinates are not. Denial -> manual city search / code / QR.
  Requirement "one fix at reduced accuracy, rounded to 3 decimals on the device": implemented (see Implementation notes).
  Implementation (checked 2026-11-09): `mobile/app/index.tsx` requests `Location.Accuracy.Low`; `mobile/src/api/catalog.ts`
  `roundCoord` rounds to 3 decimals and the nearby call sends the rounded values. "Low" is a platform hint, not a
  guaranteed reduced fix; not verified on devices.
- **Server:** coordinates live only for the duration of the request. They are **never** persisted, never written to
  logs (no request-body logging on this route; test `nearby_coordinates_never_appear_in_logs_or_responses`), never sent
  to analytics, outbox, AI or third parties. The server re-rounds to 3 decimals inside the function.
- Analytics may record "nearby used" + result count, never coordinates.
- Store declarations: approximate location, used for app functionality, not linked to identity, processed
  ephemerally, not stored.

### 6. Mobile booking protection instead of CAPTCHA

**Phase 1 (MVP) — baseline limits, honest about what they do:**

1. All existing controls stay and apply to every client: per-IP `public-write`, `MaxActiveBookingsPerPhone`,
   `MaxCreatesPerPhonePerHour`, idempotency keys.
2. **Blast-radius cap:** `PublicBooking:MaxPublicCreatesPerLocationPerHour` (default **60**; `0` disables) — counts
   online creates of the location in the last hour (any status); over the limit -> `422 booking_limit_reached`. Limits
   slot-squatting with rotating phones/IPs to a bounded window per location; staff can still book.
3. Clients send `X-Booking-Client: web | ios | android` and `X-App-Version: x.y.z` (absent or unknown client = `web`).
   This is a **label, not authentication**. It drives metrics, the policy choice in §4 and a minimum-supported-version
   kill switch: for `ios`/`android` with a known version below `PublicBooking:MinAppVersion:{Ios|Android}` every public
   route (catalog and booking) returns **`426 upgrade_required`**. A missing version is not blocked. No static "app key" is
   treated as a secret: anything shipped in the binary is extractable. The mobile app sends both headers
   (`mobile/src/services.ts`).
4. Client-proof abstraction, implemented as an interface: `IClientIntegrityVerifier.VerifyAsync(ClientProof { Kind: Captcha | AppAttest | PlayIntegrity, Token })`.
   The current `ICaptchaVerifier` backs the `Captcha` kind (`DefaultClientIntegrityVerifier`); `AppAttest` and
   `PlayIntegrity` have no verifier yet (Phase 2). Policy per client kind:
   `PublicBooking:Protection:{Web|Ios|Android} = none | monitor | enforce` (`enforce` fails closed without a valid proof;
   the proof token is read from `X-Client-Integrity`). For web, `PublicBooking:CaptchaRequired=true` forces `enforce`.
   MVP ships `none` for mobile.
5. Security note recorded deliberately: because the client label is self-declared, the effective protection of the
   public API is that of its **weakest accepted path**. The effective modes are `Web` (= `enforce` when
   `CaptchaRequired=true`, otherwise `PublicBooking:Protection:Web`), `Ios` and `Android`. The check is in
   `PublicBookingSetup.AddBeautyPublicBookingApi` / `DescribeWeakerClientPaths` and runs at startup:
   - All three effective modes equal: starts in any environment.
   - Modes differ and `PublicBooking:AllowWeakerClientPaths=false` (default): **Production** fails startup
     (`InvalidOperationException`); any other environment (Development, Staging, others) starts and logs a Warning
     (`WeakClientPathsWarning`).
   - Modes differ and `AllowWeakerClientPaths=true`: starts without exception or warning.
   Example: `CaptchaRequired=true` with `Protection:Ios` or `Protection:Android` not `enforce` is a mismatch. So with
   `CaptchaRequired=false` and MVP mobile `none`, the bar is unchanged. Turning on web CAPTCHA while mobile stays `none`
   does not raise the effective bar for the API, and in Production it stops startup unless `AllowWeakerClientPaths=true`.

**Phase 2 — device attestation (path, not implemented):**

- `POST /api/public/integrity/challenge` returns a single-use nonce (short TTL, stored hashed).
- **iOS — App Attest:** one-time key attestation per install (server verifies Apple chain, App ID, stores key ID,
  public key, counter); each booking create carries an assertion over `SHA-256(nonce || canonical request body)`;
  counter must increase.
- **Android — Play Integrity (standard request):** `requestHash = SHA-256(nonce || canonical request body)`;
  server decodes the verdict via Google's API and requires `PLAY_RECOGNIZED`, `MEETS_DEVICE_INTEGRITY`, matching
  package name, request hash and fresh timestamp.
- Rollout per platform: `monitor` (verify + log verdicts, no blocking) -> `enforce`. Devices that cannot attest
  (no Google Play services, custom ROMs) get a degraded path (stricter per-phone limits, or phone OTP once
  available) — not a hard block.
- Requires a native module through an Expo config plugin (custom dev client; not Expo Go). Library choice is
  made by `mobile-developer` with `security-reviewer`. Apple/Google credentials live in the server secret store,
  never in the app.
- Attestation proves "genuine app on a genuine device", **not** "the person owns this phone number". Booking with
  someone else's number is addressed only by **phone OTP**, which depends on an SMS/messenger provider not yet chosen
  and adds friction — a separate product decision.

---

## Consequences

### Positive

1. Short, readable, QR-friendly links that pin tenant + location; one code space across the platform.
2. ADR-002 invariants hold: no BYPASSRLS anywhere, runtime role visibility unchanged, FORCE RLS untouched.
3. The cross-tenant surface is three functions with a fixed output — easy to review and to test.
4. Coordinates of end users never land in storage or logs; low regulatory exposure.
5. Protection is pluggable: attestation and OTP slot into the same abstraction without changing the booking contract.

### Negative / accepted trade-offs

1. New DB role and `SECURITY DEFINER` functions add operational steps (CREATEROLE / provisioning, ownership,
   function review on every change) — mistakes in a definer function are security bugs.
2. A global unique index on a tenant table: a collision during code generation is detectable across tenants
   (negligible information). `EfBeautyStore.CreateAsync` retries the whole transaction up to 5 times on 23505 for
   `ux_beauty_locations_public_code`; after that the INSERT fails.
3. `nearby` and city search can be scraped to enumerate all **discoverable** locations (grid scan or city list);
   accepted because discoverable = opted into public listing; rate limits (10 and 20 per minute per IP) slow it down.
4. Haversine without a spatial index is fine for thousands of locations, not for millions (swap behind the signature).
5. **Lockout scenario (booking_limit_reached).** `MaxPublicCreatesPerLocationPerHour` counts every online create of a
   location in the last hour regardless of status, so an attacker with rotating phones/IPs can create up to the cap
   (60/h) of junk bookings and thereby block real clients of that location for the rest of the hour, repeatedly.
   Logic unchanged by design (a stricter cap or per-client fairness needs a product decision). Detection: each trigger
   increments the metric `beauty.public_booking.limit_reached` (meter `BeautyCrm.PublicBooking`, tag `scope` =
   `location_hourly|phone_active|phone_hourly`) and logs a Warning `booking_limit_reached scope=… tenant=… location=…`
   (ids only, no PII). A sustained `location_hourly` rate on one location is the alert signal. Mitigations:
   - Raising the cap: `PublicBooking:MaxPublicCreatesPerLocationPerHour` is one value for the whole deployment, read at
     startup, so a change needs a restart. It is not per location or per tenant.
   - Phase 2 attestation/OTP (not implemented).
   - **Temporarily disabling online booking for one location: not implemented, needs a decision.** There is no
     per-location online-booking switch. The only existing lever is `beauty_locations.is_active = false`, which
     affects the whole location; its full effect on public booking and staff was not checked.
6. Phase 1 mobile booking relies on limits only — a determined attacker with many IPs/phones can still create junk
   bookings up to the per-location cap. Accepted for MVP; attestation in Phase 2.
7. Immutable codes: a leaked/abused link cannot be rotated until a rotation feature exists.

---

## Alternatives Considered

### Cross-tenant read

1. **SECURITY DEFINER owned by a BYPASSRLS role** — rejected: reintroduces BYPASSRLS, which ADR-002 and `DbRoleGuard`
   exist to forbid; a function bug would expose every row of the table.
2. **Separate LOGIN role + second connection string for the catalog** — rejected: second pool and secret, another
   role to guard at startup, and the app could run arbitrary SQL under it; the definer-function approach bounds
   access to three queries.
3. **Session-variable lookup policy on the runtime role** (like `tenants_login_lookup`, e.g. by
   `app.location_public_code`) — rejected: works for resolve only, not for "nearest N" or city search; widens the
   runtime role's policy set on a tenant table.
4. **Denormalized public directory table without RLS, synced by triggers** — rejected: second source of truth,
   trigger maintenance, and the runtime role would need write grants on a cross-tenant table.
5. **Tenant slug + location UUID in the link** (`/b/{slug}/{locationId}`) — rejected: long URLs and dense QR,
   breaks on slug change, and nearby still needs a catalog.
6. **External search index (Elasticsearch/Meilisearch)** — rejected for MVP: new infrastructure for a few queries.

### Public code

7. **UUID in the link** — rejected: long, poor QR density, hard to type.
8. **Sequential or per-tenant numeric codes** — rejected: trivially enumerable.
9. **Owner-chosen vanity codes** — deferred: squatting, moderation, collisions; can coexist later as aliases.

### Coordinates transport

10. **`GET /nearby?lat&lng`** (plan wording) — rejected: coordinates end up in URLs and every access/proxy log.

### Mobile protection

11. **Static app key in the binary as authentication** — rejected: extractable; kept only as a non-secret label.
12. **CAPTCHA in a WebView** — rejected: poor UX and accessibility in a 4-screen flow, still needs a provider.
13. **Phone OTP in MVP** — deferred: needs an SMS/messenger provider (not chosen), cost per booking, friction;
    product decision.
14. **App Attest / Play Integrity in MVP** — deferred to Phase 2: Apple/Google setup, custom dev client, monitor
    period needed; does not block MVP since current web baseline is also limits-only.

---

## Implementation notes (state on 2026-11-09, not decisions)

Differences between the rules above and the code/clients in this worktree. Each one is either a deliberate
simplification to review or an open task; none of them is silently accepted by this ADR.

- Client-side coordinate rounding and reduced accuracy: **implemented (checked 2026-11-09).**
  `mobile/app/index.tsx:44` requests `Location.Accuracy.Low`; `mobile/src/api/catalog.ts` (`roundCoord`, 3 decimals)
  rounds the values before the nearby POST (line ~85). The server rounds again. Not verified on devices.
- Per-location code retry (§1): implemented. `EfBeautyStore.Locations.cs` retries the whole transaction up to 5 times
  on 23505 for `ux_beauty_locations_public_code`; no migration change. Test:
  `PublicHardeningFollowupTests.public_code_collision_is_retried_up_to_5_attempts`.
- Web fallback route `/l/[code]` (ADR-021 §3) and `apple-app-site-association` / `assetlinks.json`: still not present
  in `frontend/` (checked 2026-11-09). `mobile/app/l/[code].tsx` exists, but it is the in-app deep-link handler, not a web
  route. The domain is still a placeholder (`app.beautycrm.example` in `mobile/app.config.ts`).
- Admin UI (`frontend/`): still no fields for `publicCode`, `latitude`, `longitude`, `city`, `isDiscoverable` (checked
  2026-11-09: no matches in `frontend/`). The API supports them (`POST/PUT /api/beauty/locations`).
- Contract doc: `.claude/docs/beauty-contracts.md` §29 describes the public catalog (endpoints, error codes, rate-limit
  classes, rules for nearby/search). `.claude/docs/api.md` also lists the three routes. The machine-readable contract is
  `backend/openapi.json`.
- Mobile handling of `426 upgrade_required`: implemented. `mobile/src/services.ts` sends `X-Booking-Client` and
  `X-App-Version` and wraps fetch with `withUpgradeGuard`; `mobile/src/lib/upgrade.ts` (`UPGRADE_STATUS = 426`) sets a flag
  in `mobile/src/state/upgrade.ts`; `mobile/app/_layout.tsx` (around line 41) shows a blocking "update the app" screen.
  Unit test: `mobile/src/lib/__tests__/upgrade.test.ts`. Backend test:
  `PublicBookingApiTests.old_app_version_gets_426_everywhere_on_public_api_but_labels_never_block_otherwise`. Not verified
  in a running app.
- Phase 2 (§6): `IClientIntegrityVerifier`, `ClientProofKind`, `DefaultClientIntegrityVerifier` (captcha only) exist.
  No AppAttest or Play Integrity verifier, no `POST /api/public/integrity/challenge` (only a test fake exists).
- Values in code (checked 2026-11-09): nearby radius cap 30 km (SQL `least(p_radius_km, 30)`); `public-nearby` 10/min;
  `public-search` 20/min; `public-token` 30/min; `MaxPublicCreatesPerLocationPerHour` 60 (`PublicBookingModels.cs`,
  `PublicBookingSetup.cs`; the ADR proposal was 30). These values are still open for the human gate.

---

## Scope of Truth

Authoritative for: the `public_code` format, generation, normalization and immutability; the
`latitude`/`longitude`/`city`/`is_discoverable` columns and their meaning; the **only sanctioned mechanism for cross-tenant
public reads** (definer functions owned by `beautycrm_catalog`, role-scoped policies, column grants, invariants);
the resolve/nearby/search endpoint shapes, error uniformity and rate-limit classes; the geolocation privacy rules; the
mobile booking protection model (Phase 1 baseline, client-proof abstraction, Phase 2 attestation path).
Any new cross-tenant read must extend this mechanism via a new ADR, not add BYPASSRLS, new runtime-role policies or a
new login role.

---

## Open Questions (for the human gate)

1. **Universal-link domain** `{domain}` (must serve AASA / assetlinks; web fallback route lives there). Placeholder only.
2. **Nearby = cross-tenant listing.** Do tenants accept appearing next to competitors in one app? Code default is
   `is_discoverable = false` per location (opt-in); confirm the default and whether a tenant-level switch is wanted.
3. **Manual fallback after geolocation denial:** city search is implemented (`POST /api/public/locations/search`), so
   the remaining question is product acceptance, not the technical shape.
4. **Phone OTP**: wanted at all (friction vs. fake-number bookings)? Which provider/channel (SMS, Viber, Telegram)?
5. Values to confirm: nearby radius cap (30 km in code), `public-nearby` limit (10/min), `public-search` (20/min),
   `MaxPublicCreatesPerLocationPerHour` (60 in code, 30 proposed).
6. DB role provisioning in production: grant `CREATEROLE` to the migrator or a separate ops script (pre-provisioning
   is supported and documented in `database.md` §5 and the migration notes).
7. Is code rotation (lost/abused QR) needed before launch?

---

## Рішення, які потребують людини

Лише відкриті питання, для яких код дає значення за замовчуванням, але рішення не ухвалене. Кожне — з поточним
значенням у коді (станом на 2026-11-09). Статус ADR лишається `Proposed`, доки ці рішення не записані.

1. **Ліміт створень на заклад за годину:** код 60 (`PublicBooking:MaxPublicCreatesPerLocationPerHour`), ADR пропонував 30.
2. **Радіус nearby:** 30 км, вшитий у SQL-функцію (`least(p_radius_km, 30)`); зміна потребує міграції.
3. **Ліміти на IP (fixed window 60 с):** `public-nearby` 10/хв, `public-search` 20/хв (`PublicBooking:RateLimit:NearbyPermit` / `SearchPermit`);
   `public-token` 30/хв для resolve.
4. **Політика `PublicBooking:AllowWeakerClientPaths` і fail-fast:** зараз Production падає при розбіжності режимів
   Web/Ios/Android без `AllowWeakerClientPaths=true`; Staging і Development лише попереджають. Вирішити: чи робити fail-fast
   також у Staging; чи дозволяти `AllowWeakerClientPaths=true` у Production на період MVP (mobile = `none`).
5. **Домен:** універсальні посилання (AASA / assetlinks) і web-fallback `/l/{code}` не реалізовані; домен-заглушка
   `app.beautycrm.example` (`mobile/app.config.ts`).
6. **Згода tenant-ів на крос-tenant лістинг:** `is_discoverable` за замовчуванням `false` (opt-in на кожен заклад). Потрібен
   чи ні перемикач на рівні tenant-а.
7. **Phone OTP:** чи потрібен взагалі; провайдер/канал (SMS, Viber, Telegram) не обрано.
8. **Провізіонінг ролі `beautycrm_catalog` у production:** CREATEROLE для мігратора чи окремий скрипт ops
   (`.claude/docs/runbook.md` §1a).
9. **Ротація публічних кодів** (втрачений або зловжитий QR): не реалізовано; потрібне рішення до запуску.
10. **Прийняття ризику блокування ліміту на заклад** (`booking_limit_reached`, scope `location_hourly`): окремого перемикача
    онлайн-запису немає (не реалізовано). Варіанти: окремий перемикач, підвищення ліміту, `is_active` (вимикає весь заклад).
11. **Точність геолокації:** `Location.Accuracy.Low` є платформною підказкою; реальна точність на пристроях не перевірена.

## Testing

- RLS/isolation (`backend/BeautyCrm.Tests/Data/PublicCatalogRlsTests.cs`, `PostgresRlsFixture`): the runtime role sees no
  foreign rows directly (`App_role_sees_no_foreign_rows_directly_only_via_functions`); functions return only the public
  columns; resolve normalization and Crockford aliases; nearby filters, radius, ordering and clamping; exact,
  case-insensitive city search among discoverable locations only; catalog policies are scoped to the catalog role
  (`Catalog_policies_are_scoped_to_the_catalog_role_only`); `beautycrm_app` cannot assume `beautycrm_catalog` or read
  tables through it (`App_role_cannot_assume_catalog_role_or_read_tables_through_it`); `beautycrm_catalog` is NOLOGIN,
  NOBYPASSRLS and owns the functions (`Catalog_role_is_nologin_not_bypassrls_and_owns_the_functions`). Public code
  immutability: trigger and EF model (`Public_code_update_is_rejected_by_trigger_even_for_the_owner_but_noop_updates_pass`,
  `Public_code_cannot_be_changed_through_the_ef_model`).
- Role invariants (`backend/BeautyCrm.Tests/Data/CatalogRoleInvariantsTests.cs`): migrator membership is
  `INHERIT FALSE, SET TRUE`; `DbRoleGuard` rejects a member with INHERIT, an inheriting catalog role, a catalog role that
  is a member of another role, and an unexpected function or a missing `search_path`; a non-superuser CREATEROLE migrator
  provisions the role with INHERIT FALSE.
- Hardening follow-up (`backend/BeautyCrm.Tests/PublicBooking/PublicHardeningFollowupTests.cs`, `[Fact]` tests; database
  use not checked): nearby
  rounds coordinates to 3 decimals before the store; the `/64` IPv6 rate-limit key and the IPv4 key; uniform protection
  modes pass in Production; a weaker client path fails in Production unless allowed and only warns in Development;
  `integrity` is a reserved slug and does not resolve; public_code collisions retry up to 5 attempts; other DB errors are
  not retried.
- Migration: `PublicCatalogRlsTests.Migration_down_up_backfills_unique_valid_codes_for_existing_rows` migrates the
  test database down to `20261010135605_beauty_location_working_hours`, inserts 50 locations, migrates up and checks
  the backfill. It runs on the fixture's test database, not on a separate copy, and is a `SkippableFact` (skipped without
  PostgreSQL). Not run in this documentation pass.
- Admin/API geo fields: `PublicCatalogApiTests.admin_api_accepts_geo_fields_returns_immutable_public_code_and_keeps_fields_when_omitted`,
  `admin_geo_fields_are_tenant_isolated_and_specialist_cannot_write`.
- Mobile unit tests: `mobile/src/api/__tests__/catalog.test.ts` (`roundCoord`), `mobile/src/lib/__tests__/upgrade.test.ts`
  (426 flag), `mobile/src/lib/__tests__/deeplink.test.ts`, `mobile/src/lib/__tests__/locationSource.test.ts`.
  Not run in this pass.
- API (`PublicCatalogApiTests`): resolve 200/404 uniformity, normalization (`abcd-efgh`, `O`/`0`), rate limits per
  policy, nearby validation (`invalid_coordinates`, `invalid_radius`) and clamping, city validation (`invalid_city`),
  reserved slugs, 426 on old app versions (`PublicBookingApiTests`).
- Logging: `nearby_coordinates_never_appear_in_logs_or_responses` checks captured logs and responses.

---

## References

- Plan: `C:\Users\stass\.claude\plans\humming-snacking-kitten.md` — "Архітектурні рішення" items 1–5
- `backend/BeautyCrm.Infrastructure/Data/Migrations/20261109075625_beauty_public_location_catalog.cs` (+ `.Sql.cs`)
- `backend/BeautyCrm.Api/Controllers/PublicCatalogController.cs`, `backend/BeautyCrm.Application/Features/BeautyPublicCatalog/PublicCatalog.cs`
- `backend/BeautyCrm.Api/Infrastructure/DbRoleGuard.cs`
- `backend/BeautyCrm.Api/PublicBooking/PublicBookingSetup.cs` — rate-limit policies, client label, 426, protection config
- `backend/BeautyCrm.Application/Features/BeautyPublicBooking/PublicTokens.cs` — `PublicTenantResolver`, reserved slugs
- `backend/BeautyCrm.Infrastructure/Data/Migrations/20261007134016_auth_tenants_users_invites.cs` — lookup-policy precedent
- `backend/openapi.json` — generated contract, `PublicCatalog` tag
- [PostgreSQL: CREATE POLICY (TO role)](https://www.postgresql.org/docs/16/sql-createpolicy.html),
  [Writing SECURITY DEFINER functions safely](https://www.postgresql.org/docs/16/sql-createfunction.html#SQL-CREATEFUNCTION-SECURITY)
