# Beauty CRM — Database Schema & RLS

**Current version:** 0.8 (TASK-697/698, 2026-10-08)
**Database:** PostgreSQL 16+ (RLS-enabled)
**ORM:** EF Core 8.0 with Npgsql 8.0.11

---

## 1. Schema Overview

All `beauty_*` tables enforce **Row-Level Security (RLS)** with `FORCE` — even the table owner cannot bypass. The runtime PostgreSQL role must not be `superuser` or have `BYPASSRLS`.

**Tenant isolation:** Every `beauty_*` row has `tenant_id` (UUID); a session-scoped PostgreSQL setting `app.tenant_id` filters all access via the policy:

```sql
tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
```

**Total tables:** 25+ including auth/catalog/appointments/staff/messaging.

---

## 2. Table Reference

### Locations & Infrastructure

#### beauty_locations
```sql
id              UUID PRIMARY KEY
tenant_id       UUID NOT NULL (RLS filtered)
name            VARCHAR(200) NOT NULL
address         VARCHAR(500)
phone           VARCHAR(32)
timezone        VARCHAR(64) NOT NULL (IANA: "Europe/Kyiv"; validated per §16)
is_active       BOOLEAN DEFAULT true
created_at      TIMESTAMPTZ NOT NULL
updated_at      TIMESTAMPTZ NOT NULL

UNIQUE (tenant_id, name) -- case-insensitive via lower()
CHECK (char_length(name) >= 1 AND char_length(name) <= 200)
CHECK (char_length(address) IS NULL OR char_length(address) <= 500)
```

**Indexes:** (tenant_id, id); (tenant_id, is_active).
**RLS Policy:** `tenant_isolation` (ENABLE ROW LEVEL SECURITY FORCE).

**Notes:** Deactivation blocked if has future pending/confirmed appointments (TASK-697). Timezone change blocked similarly. No delete — only soft deactivation.

---

### Staff Management

#### beauty_specialists
```sql
id              UUID PRIMARY KEY
tenant_id       UUID NOT NULL (RLS filtered)
full_name       VARCHAR(200) NOT NULL
title           VARCHAR(200)
phone           VARCHAR(32)
email           VARCHAR(320)
photo_url       VARCHAR(2048)
position        VARCHAR (text, added TASK-691, e.g. "Hairdresser", "Colorist")
is_active       BOOLEAN DEFAULT true
created_at      TIMESTAMPTZ NOT NULL
updated_at      TIMESTAMPTZ NOT NULL

UNIQUE (tenant_id, id)
```

**Indexes:** (tenant_id, id); (tenant_id, is_active).
**RLS Policy:** `tenant_isolation` (ENABLE ROW LEVEL SECURITY FORCE).

**Notes:** Deactivation (isActive=false) in transaction: user→is_active=false (except owner), refresh tokens revoked, pending invites→revoked. Does NOT delete appointments.

#### beauty_specialist_locations
```sql
id              UUID PRIMARY KEY
tenant_id       UUID NOT NULL (RLS filtered)
specialist_id   UUID NOT NULL (FK beauty_specialists, composite FK (tenant_id, specialist_id))
location_id     UUID NOT NULL (FK beauty_locations, composite FK (tenant_id, location_id))
working_hours   JSONB (nullable, format per §9: {"mon":[{"from":"09:00","to":"18:00"}], ...})
is_active       BOOLEAN DEFAULT true
created_at      TIMESTAMPTZ NOT NULL
updated_at      TIMESTAMPTZ NOT NULL

UNIQUE (tenant_id, specialist_id, location_id)
ON DELETE RESTRICT (preserve schedule history)
```

**Format of working_hours:**
```json
{
  "mon": [{"from": "09:00", "to": "13:00"}, {"from": "14:00", "to": "18:00"}],
  "tue": [...],
  "wed": [],  // empty or missing = day off
  ...
  "sun": []
}
```

All times are local to the location's timezone.

**RLS Policy:** `tenant_isolation` (ENABLE ROW LEVEL SECURITY FORCE).

#### beauty_specialist_services
```sql
tenant_id       UUID NOT NULL (RLS filtered)
specialist_id   UUID NOT NULL (FK beauty_specialists, composite FK)
service_id      UUID NOT NULL (FK beauty_services, composite FK)
created_at      TIMESTAMPTZ NOT NULL

PRIMARY KEY (tenant_id, specialist_id, service_id)
ON DELETE CASCADE
```

Many-to-many mapping. Empty table for specialist = no services assigned (→ no slots).

**RLS Policy:** `tenant_isolation` (ENABLE ROW LEVEL SECURITY FORCE).

**Notes:** Backfilled on TASK-691 to assign all existing services to all existing specialists to avoid breaking demo (§13).

#### beauty_specialist_absences
```sql
id              UUID PRIMARY KEY
tenant_id       UUID NOT NULL (RLS filtered)
specialist_id   UUID NOT NULL (FK beauty_specialists, composite FK (tenant_id, specialist_id))
type            VARCHAR(16) NOT NULL (CHECK IN ('sick','vacation','day_off','other'))
date_from       DATE NOT NULL (inclusive, local to location timezone per specialist's location)
date_to         DATE NOT NULL (inclusive)
status          VARCHAR(16) NOT NULL (CHECK IN ('requested','approved','rejected','cancelled'))
note            VARCHAR(500) (optional, internal remarks, not logged/PII)
requested_by_user_id UUID (FK users, nullable, author of request)
decided_by_user_id UUID (FK users, nullable, who approved/rejected)
decided_at      TIMESTAMPTZ (nullable)
cancelled_by_user_id UUID (FK users, added TASK-696, nullable, who cancelled)
cancelled_at    TIMESTAMPTZ (added TASK-696, nullable)
created_at      TIMESTAMPTZ NOT NULL
updated_at      TIMESTAMPTZ NOT NULL

CHECK (date_to >= date_from)
CHECK (type IN ('sick','vacation','day_off','other'))
CHECK (status IN ('requested','approved','rejected','cancelled'))
CHECK (char_length(note) IS NULL OR char_length(note) <= 500)
EXCLUDE gist (tenant_id WITH =, specialist_id WITH =, daterange(date_from, date_to, '[]') WITH &&)
  WHERE (status IN ('requested','approved')) -- no overlaps for active absences
```

**RLS Policy:** `tenant_isolation` (ENABLE ROW LEVEL SECURITY FORCE).

**Notes:** 
- Specialist can create only `requested` (self, under lock, ≤10 active per specialist, rate limit 20/min).
- Owner/admin create as `approved` immediately.
- Advisory lock `hashtextextended(app.tenant_id || ':specialist:' || specialist_id, 0)` on create/approve/reject/cancel and appointment ops to avoid race conditions.
- Conflicts[] computed during create/approve: future pending/confirmed appointments whose local date intersects absence period.

---

### Services & Catalog

#### beauty_services
```sql
id              UUID PRIMARY KEY
tenant_id       UUID NOT NULL (RLS filtered)
name            VARCHAR(200) NOT NULL
description     TEXT
category        VARCHAR(100)
duration_minutes INT NOT NULL
is_active       BOOLEAN DEFAULT true
created_at      TIMESTAMPTZ NOT NULL
updated_at      TIMESTAMPTZ NOT NULL

CHECK (duration_minutes > 0)
```

**RLS Policy:** `tenant_isolation` (ENABLE ROW LEVEL SECURITY FORCE).

#### beauty_service_prices
```sql
id              UUID PRIMARY KEY
tenant_id       UUID NOT NULL (RLS filtered)
service_id      UUID NOT NULL (FK beauty_services, composite FK (tenant_id, service_id))
location_id     UUID (nullable: null = network-wide price, else location override)
price           NUMERIC(12, 2) NOT NULL
created_at      TIMESTAMPTZ NOT NULL
updated_at      TIMESTAMPTZ NOT NULL

UNIQUE (tenant_id, service_id, location_id)
CHECK (price >= 0)
ON DELETE CASCADE (if location or service deleted)
```

**Lookup:** If location override exists, use it; else use network price (location_id = NULL).

**RLS Policy:** `tenant_isolation` (ENABLE ROW LEVEL SECURITY FORCE).

#### beauty_promotions
```sql
id              UUID PRIMARY KEY
tenant_id       UUID NOT NULL (RLS filtered)
name            VARCHAR(200) NOT NULL
description     TEXT
discount_type   VARCHAR(32) NOT NULL (CHECK IN ('percent','fixed'))
discount_value  NUMERIC(12, 2) NOT NULL
starts_at       TIMESTAMPTZ (nullable)
ends_at         TIMESTAMPTZ (nullable)
is_active       BOOLEAN DEFAULT true
created_at      TIMESTAMPTZ NOT NULL
updated_at      TIMESTAMPTZ NOT NULL

CHECK (discount_type IN ('percent','fixed'))
CHECK (discount_value > 0 AND (discount_type <> 'percent' OR discount_value <= 100))
CHECK (ends_at IS NULL OR starts_at IS NULL OR ends_at > starts_at)
```

**RLS Policy:** `tenant_isolation` (ENABLE ROW LEVEL SECURITY FORCE).

#### beauty_promotion_locations
```sql
promotion_id    UUID NOT NULL (FK beauty_promotions, composite FK)
location_id     UUID NOT NULL (FK beauty_locations, composite FK)
tenant_id       UUID NOT NULL (RLS filtered)
created_at      TIMESTAMPTZ NOT NULL

PRIMARY KEY (promotion_id, location_id)
ON DELETE CASCADE
```

Empty list (no rows) = promotion applies to all locations.

**RLS Policy:** `tenant_isolation` (ENABLE ROW LEVEL SECURITY FORCE).

#### beauty_promotion_services
```sql
promotion_id    UUID NOT NULL (FK beauty_promotions, composite FK)
service_id      UUID NOT NULL (FK beauty_services, composite FK)
tenant_id       UUID NOT NULL (RLS filtered)
created_at      TIMESTAMPTZ NOT NULL

PRIMARY KEY (promotion_id, service_id)
ON DELETE CASCADE
```

Empty list (no rows) = promotion applies to all services.

**RLS Policy:** `tenant_isolation` (ENABLE ROW LEVEL SECURITY FORCE).

---

### Appointments & Clients

#### beauty_appointments
```sql
id              UUID PRIMARY KEY
tenant_id       UUID NOT NULL (RLS filtered)
location_id     UUID NOT NULL (FK beauty_locations, composite FK)
specialist_id   UUID NOT NULL (FK beauty_specialists, composite FK)
service_id      UUID NOT NULL (FK beauty_services, composite FK)
client_id       UUID NOT NULL (FK beauty_clients, composite FK)
starts_at       TIMESTAMPTZ NOT NULL (UTC)
duration_minutes INT NOT NULL (copied from service at booking time)
ends_at         TIMESTAMPTZ NOT NULL (computed by trigger, read-only for app)
status          VARCHAR(32) NOT NULL (CHECK IN ('pending','confirmed','completed','cancelled','no_show'))
source          VARCHAR(32) NOT NULL (CHECK IN ('admin','online','telegram','instagram'))
price_original  NUMERIC(12, 2)
price_final     NUMERIC(12, 2)
promotion_id    UUID (nullable, FK beauty_promotions)
reminder_option VARCHAR(32) NOT NULL (CHECK IN ('none','1h','2h'))
payment_method  VARCHAR(32) (nullable, CHECK IN ('card','cash'))
cancelled_at    TIMESTAMPTZ (nullable, set at cancellation)
cancelled_by_type VARCHAR(16) (nullable, added TASK-697, CHECK IN ('client','staff','system'))
cancelled_by_user_id UUID (nullable, FK users, TASK-697, only for staff; RESTRICT on delete)
cancel_reason   VARCHAR(300) (nullable, added TASK-697)
public_token_hash VARCHAR(64) (nullable, added TASK-688, SHA-256 of base64url(HMAC(...)))
idempotency_key_hash VARCHAR(64) (nullable, added TASK-688, SHA-256, unique per tenant)
idempotency_request_hash VARCHAR(64) (nullable, added TASK-688, SHA-256 of request body for replay detection)
created_at      TIMESTAMPTZ NOT NULL
updated_at      TIMESTAMPTZ NOT NULL

CHECK (starts_at < ends_at)
CHECK (duration_minutes > 0)
CHECK (price_original >= 0 AND price_final >= 0)
CHECK ((cancelled_by_type IS NULL AND cancel_reason IS NULL) OR status = 'cancelled') -- TASK-697
CHECK (cancelled_by_type IS NULL OR cancelled_by_type IN ('client','staff','system'))
CHECK (cancelled_by_user_id IS NULL OR cancelled_by_type = 'staff')
CHECK (cancel_reason IS NULL OR char_length(cancel_reason) <= 300)
CONSTRAINT ex_beauty_appointments_specialist_no_overlap EXCLUDE
  USING gist (tenant_id WITH =, specialist_id WITH =, tstzrange(starts_at, ends_at, '[)') WITH &&)
  WHERE (status IN ('pending', 'confirmed', 'completed'))
UNIQUE PARTIAL (tenant_id, public_token_hash) WHERE public_token_hash IS NOT NULL
UNIQUE PARTIAL (tenant_id, idempotency_key_hash) WHERE idempotency_key_hash IS NOT NULL
```

**Trigger:** `trg_beauty_appointments_set_ends_at` (BEFORE INSERT/UPDATE) — computes `ends_at := starts_at + duration_minutes`.

**Indexes:** (tenant_id, client_id); (tenant_id, specialist_id, starts_at); (tenant_id, location_id, starts_at); (tenant_id, cancelled_by_user_id).

**RLS Policy:** `tenant_isolation` (ENABLE ROW LEVEL SECURITY FORCE).

**Notes:**
- `cancelled_by_type` = 'client' → public cancellation via token; 'staff' → admin/specialist cancel; 'system' → AI revert or payment failure at creation.
- `cancelled_by_user_id` only for staff; backfilled to 'system' for existing cancelled rows (TASK-697).
- Backfill (TASK-697) runs with `NO FORCE ROW LEVEL SECURITY` then `FORCE` to allow mass UPDATE.

#### beauty_clients
```sql
id              UUID PRIMARY KEY
tenant_id       UUID NOT NULL (RLS filtered)
full_name       VARCHAR(200) NOT NULL
phone           VARCHAR(32) (nullable)
email           VARCHAR(320) (nullable)
birth_date      DATE (nullable)
marketing_consent BOOLEAN DEFAULT false (for AI segments/campaigns)
unsubscribed    BOOLEAN DEFAULT false (excluded from outreach)
deleted_at      TIMESTAMPTZ (nullable, soft delete)
created_at      TIMESTAMPTZ NOT NULL
updated_at      TIMESTAMPTZ NOT NULL

UNIQUE (tenant_id, phone) DEFERRABLE INITIALLY DEFERRED (null phone allowed multiple times)
CHECK (phone IS NULL OR phone ~ '^\+?[\d\s\-()]+$')
```

**Soft delete:** Rows with `deleted_at IS NOT NULL` are hidden from UI but retained for history.

**RLS Policy:** `tenant_isolation` (ENABLE ROW LEVEL SECURITY FORCE).

**Deduplication:** Public API dedups by normalized phone (E.164); existing name NOT overwritten (§12).

#### beauty_client_notes
```sql
id              UUID PRIMARY KEY
tenant_id       UUID NOT NULL (RLS filtered)
client_id       UUID NOT NULL (FK beauty_clients, composite FK (tenant_id, client_id))
author_user_id  UUID (nullable, FK users)
body            TEXT NOT NULL
created_at      TIMESTAMPTZ NOT NULL
updated_at      TIMESTAMPTZ NOT NULL

ON DELETE CASCADE (if client soft-deleted or hard-deleted)
```

**RLS Policy:** `tenant_isolation` (ENABLE ROW LEVEL SECURITY FORCE).

---

### Channels & Messaging

#### beauty_channels
```sql
id              UUID PRIMARY KEY
tenant_id       UUID NOT NULL (RLS filtered)
location_id     UUID (nullable, FK beauty_locations)
type            VARCHAR(32) NOT NULL (CHECK IN ('telegram','instagram','viber','whatsapp','facebook','widget'))
name            VARCHAR(200) NOT NULL
credentials_encrypted TEXT (nullable, AES-256-GCM encrypted JSON of {token, webhookSecret, appSecret, verifyToken})
credentials_last4 VARCHAR(4) (nullable, masked display "****abcd")
settings        JSONB (nullable, non-secret settings)
is_active       BOOLEAN DEFAULT true
created_at      TIMESTAMPTZ NOT NULL
updated_at      TIMESTAMPTZ NOT NULL

UNIQUE (tenant_id, id)
```

**Encryption (TASK-689):** `credentials_encrypted` is AES-256-GCM using `Channels__EncryptionKey` (.env, base64 32+ bytes). Decryption in `Infrastructure/Integrations/Channels/AesGcmSecretProtector`. Credentials JSON includes `{token, webhookSecret, appSecret, verifyToken}` (Instagram M5: separate appSecret for HMAC, verifyToken for GET, not webhookSecret anymore).

**RLS Policy:** `tenant_isolation` (ENABLE ROW LEVEL SECURITY FORCE).
**Webhook Policy:** `channel_webhook_lookup` (SELECT only, by channel ID).

#### beauty_conversations
```sql
id              UUID PRIMARY KEY
tenant_id       UUID NOT NULL (RLS filtered)
channel_id      UUID NOT NULL (FK beauty_channels, composite FK)
client_id       UUID (nullable, FK beauty_clients; may be unknown initially)
external_chat_id VARCHAR NOT NULL (channel's internal chat/thread ID)
status          VARCHAR (CHECK IN ('open','closed','archived'))
last_message_at TIMESTAMPTZ (nullable)
created_at      TIMESTAMPTZ NOT NULL
updated_at      TIMESTAMPTZ NOT NULL

UNIQUE (tenant_id, channel_id, external_chat_id)
```

**RLS Policy:** `tenant_isolation` (ENABLE ROW LEVEL SECURITY FORCE).

#### beauty_messages
```sql
id              UUID PRIMARY KEY
tenant_id       UUID NOT NULL (RLS filtered)
conversation_id UUID NOT NULL (FK beauty_conversations, composite FK)
direction       VARCHAR NOT NULL (CHECK IN ('inbound','outbound'))
sender_type     VARCHAR NOT NULL (CHECK IN ('client','assistant','specialist','system'))
body            TEXT NOT NULL
external_message_id VARCHAR (nullable, channel's message ID for editing)
sent_at         TIMESTAMPTZ
status          VARCHAR (CHECK IN ('received','draft','pending','sent','failed'))
attempts        INT DEFAULT 0
last_error      VARCHAR (nullable)
idempotency_key VARCHAR (nullable, unique per tenant for deduplication, SHA-256)

UNIQUE (tenant_id, idempotency_key) DEFERRABLE INITIALLY DEFERRED

ON DELETE CASCADE (if conversation deleted)
```

**Status flow:**
- Inbound: webhook → `received`
- Outbound: `draft` → `pending` (queued) → `sent` / `failed`
- L7: `DraftReplyAdapter.SendAsync` atomically claims `draft → sending` before inline-send, so worker only sees `pending`.

**RLS Policy:** `tenant_isolation` (ENABLE ROW LEVEL SECURITY FORCE).

#### beauty_ai_actions
```sql
id              UUID PRIMARY KEY
tenant_id       UUID NOT NULL (RLS filtered)
conversation_id UUID (nullable, FK beauty_conversations)
appointment_id  UUID (nullable, FK beauty_appointments)
tool_name       VARCHAR NOT NULL (e.g. "create_appointment")
payload         JSONB NOT NULL (tool arguments)
status          VARCHAR (CHECK IN ('pending_confirmation','executing','done','rejected','reverting'))
result          JSONB (nullable, execution result)
error           VARCHAR (nullable, if failed)
confirmed_by_user_id UUID (nullable, FK users)
confirmed_at    TIMESTAMPTZ (nullable)
executed_at     TIMESTAMPTZ (nullable)
created_at      TIMESTAMPTZ NOT NULL
updated_at      TIMESTAMPTZ NOT NULL
```

**Status (M2, TASK-689):** 
- `pending_confirmation` → approve → `executing` → `done` (success) or `executing` → `pending_confirmation` (error, retry)
- `pending_confirmation` → reject → `rejected`
- `done` → revert → `reverting` → `done` (success) or `reverting` → `done` (error, retry)
- Atomically via `IAiActionJournal.TryTransitionAsync` (UPDATE ... WHERE result->>'Status' = from).

**RLS Policy:** `tenant_isolation` (ENABLE ROW LEVEL SECURITY FORCE).

#### beauty_reminders
```sql
id              UUID PRIMARY KEY
tenant_id       UUID NOT NULL (RLS filtered)
appointment_id  UUID NOT NULL (FK beauty_appointments)
scheduled_at    TIMESTAMPTZ NOT NULL
status          VARCHAR (CHECK IN ('scheduled','sent','failed'))
sent_at         TIMESTAMPTZ (nullable)
error           VARCHAR (nullable)
created_at      TIMESTAMPTZ NOT NULL
updated_at      TIMESTAMPTZ NOT NULL

ON DELETE CASCADE (if appointment deleted)
```

**RLS Policy:** `tenant_isolation` (ENABLE ROW LEVEL SECURITY FORCE).

#### beauty_payments
```sql
id              UUID PRIMARY KEY
tenant_id       UUID NOT NULL (RLS filtered)
appointment_id  UUID NOT NULL (FK beauty_appointments)
amount          NUMERIC(12, 2) NOT NULL
method          VARCHAR NOT NULL (CHECK IN ('card','cash'))
status          VARCHAR (CHECK IN ('pending','paid','failed','refunded'))
provider        VARCHAR (nullable, e.g. "stripe")
provider_payment_id VARCHAR (nullable)
paid_at         TIMESTAMPTZ (nullable)
refunded_at     TIMESTAMPTZ (nullable)
created_at      TIMESTAMPTZ NOT NULL
updated_at      TIMESTAMPTZ NOT NULL

ON DELETE CASCADE (if appointment deleted)
```

**Idempotency (M1, TASK-689):** Refund idempotency key = `payment.Id` (passed to `IPaymentService.RefundAsync`). Failed refund (402) → advisory lock released, appointment reverted to active (if claimed), payment stays `paid`.

**RLS Policy:** `tenant_isolation` (ENABLE ROW LEVEL SECURITY FORCE).

---

### Cancellation Settings

#### beauty_cancellation_settings
```sql
id              UUID PRIMARY KEY
tenant_id       UUID NOT NULL UNIQUE (one row per tenant)
window_hours    INT NOT NULL
refund_percent_in_window INT NOT NULL
refund_percent_outside INT NOT NULL
deduct_fee      BOOLEAN DEFAULT false
fee_percent     INT NOT NULL
created_at      TIMESTAMPTZ NOT NULL
updated_at      TIMESTAMPTZ NOT NULL

CHECK (window_hours BETWEEN 0 AND 720)
CHECK (refund_percent_in_window BETWEEN 0 AND 100)
CHECK (refund_percent_outside BETWEEN 0 AND 100)
CHECK (fee_percent BETWEEN 0 AND 100)
```

**Default values (if row absent):** `window_hours=12`, `refund_percent_in_window=50`, `refund_percent_outside=100`, `deduct_fee=false`, `fee_percent=0`.

**Calculation (TASK-685):** 
```
in_window = (starts_at - now) <= windowHours ? refund_percent_in_window : refund_percent_outside
fee = deduct_fee ? feePercent : 0
refundAmount = round_down(paid * in_window / 100 * (100 - fee) / 100)
```
Round **down to kopiyky** (toward zero). If refundAmount = 0, no refund call.

**RLS Policy:** `tenant_isolation` (ENABLE ROW LEVEL SECURITY FORCE).

---

### Authentication (auth schema)

#### tenants
```sql
id              UUID PRIMARY KEY
name            VARCHAR NOT NULL
slug            VARCHAR NOT NULL UNIQUE (3-64, a-z0-9-)
modules         TEXT[] (array of feature flags)
status          VARCHAR (CHECK IN ('active','suspended'))
created_at      TIMESTAMPTZ NOT NULL
updated_at      TIMESTAMPTZ NOT NULL
```

**No RLS** (public read for login lookup). Suspended tenant → `403 module_disabled` on any module access.

#### users
```sql
id              UUID PRIMARY KEY
tenant_id       UUID NOT NULL (FK tenants)
email           VARCHAR NOT NULL
full_name       VARCHAR NOT NULL
role            VARCHAR NOT NULL (CHECK IN ('owner','admin','specialist'))
specialist_id   UUID (nullable, FK beauty_specialists, composite FK (tenant_id, specialist_id))
is_active       BOOLEAN DEFAULT true
password_hash   VARCHAR NOT NULL (bcrypt, workfactor 12)
created_at      TIMESTAMPTZ NOT NULL
updated_at      TIMESTAMPTZ NOT NULL

UNIQUE (tenant_id, email) DEFERRABLE INITIALLY DEFERRED
```

**Deactivation (TASK-696):** owner never auto-disabled; specialist deactivation → user.is_active=false (except owner), refresh tokens revoked, pending invites revoked.

**RLS Policy:** `tenant_isolation` (USING/WITH CHECK: `tenant_id = app.tenant_id`).

#### invites
```sql
id              UUID PRIMARY KEY
tenant_id       UUID NOT NULL (FK tenants)
email           VARCHAR NOT NULL
role            VARCHAR NOT NULL (CHECK IN ('admin','specialist'))
specialist_id   UUID (nullable, FK beauty_specialists, TASK-691)
token_hash      VARCHAR NOT NULL (SHA-256, one-time)
is_used         BOOLEAN DEFAULT false
status          VARCHAR (TASK-696, CHECK IN ('pending','revoked')) -- added for revocation tracking
expires_at      TIMESTAMPTZ NOT NULL
created_at      TIMESTAMPTZ NOT NULL

UNIQUE (tenant_id, email) PARTIAL WHERE is_used=false AND status='pending'
```

**Lifecycle (TASK-691, TASK-696):**
- Created: pending
- Accepted: is_used=true
- Revoked: status=revoked (on specialist deactivation or new invite for same specialist)
- One active per specialist_id (new invite revokes pending for same profile, advisory lock)

**RLS Policy:** `tenant_isolation`.

#### refresh_tokens
```sql
id              UUID PRIMARY KEY
user_id         UUID NOT NULL (FK users)
tenant_id       UUID NOT NULL (from users; RLS filtered)
token_hash      VARCHAR NOT NULL (SHA-256)
issued_at       TIMESTAMPTZ NOT NULL
expires_at      TIMESTAMPTZ NOT NULL
revoked_at      TIMESTAMPTZ (nullable, token invalidated)
created_at      TIMESTAMPTZ NOT NULL
```

**Revocation (TASK-696):** On specialist deactivation or user disable, all tokens revoked (revoked_at set).

**RLS Policy:** `tenant_isolation` (user_id FK to users already filtered).

---

## 3. Row-Level Security (RLS)

### Policy: `tenant_isolation`

Applied to all `beauty_*`, `users`, `invites`, `refresh_tokens` tables with `ENABLE ROW LEVEL SECURITY FORCE`.

```sql
CREATE POLICY tenant_isolation ON <table_name>
  FOR ALL
  USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
  WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
```

**Usage (M4, TASK-689):**
1. Before any database operation, set PostgreSQL session variable (per connection, stays until returned to pool):
   ```csharp
   await connection.ExecuteAsync("SELECT set_config('app.tenant_id', @tid::text, true)",
       new { tid = tenantId.ToString() });
   ```
2. All SELECT/INSERT/UPDATE/DELETE on RLS-protected tables automatically filtered.
3. Runtime role must **not** have `BYPASSRLS`; use `FORCE ROW LEVEL SECURITY` to prevent bypass.
4. `set_config(..., true)` = session scope (persists until connection closed/returned to pool).
5. **No transaction scope:** PgBouncer transaction pooling + Npgsql Multiplexing forbidden (both break session state).

### Special Policy: `channel_webhook_lookup` (beauty_channels, SELECT only)

For webhooks (receive channel ID but not tenant ID), custom policy resolves tenant:

```sql
CREATE POLICY channel_webhook_lookup ON beauty_channels
  FOR SELECT
  USING (id = NULLIF(current_setting('app.channel_id', true), '')::uuid);
```

**Webhook flow:**
1. Handler reads channel ID from URL.
2. Sets `app.channel_id` to that ID, leaves `app.tenant_id` empty.
3. Executes `SELECT * FROM beauty_channels WHERE id = ...` → returns exactly that row.
4. Reads `tenant_id` from row.
5. Sets `app.tenant_id` to resolved ID.
6. Executes remaining queries (conversations, messages, etc.) under full RLS.

### Special Policy: `tenants_login_lookup` (tenants, SELECT only, TASK-684)

For login by slug (tenant unknown initially):

```sql
CREATE POLICY tenants_login_lookup ON tenants
  FOR SELECT
  USING (slug = NULLIF(current_setting('app.tenant_slug', true), ''));
```

Similar flow: set `app.tenant_slug`, resolve `tenant_id`, then set `app.tenant_id` for remaining queries.

---

## 4. Migrations (in order, 8 total)

### 1. `20261007115130_add_beauty_schema` (TASK-674)

Creates beauty_locations, beauty_specialists, beauty_specialist_locations, beauty_services, beauty_service_prices, beauty_appointments, beauty_clients, beauty_client_notes, beauty_promotions, beauty_promotion_locations, beauty_promotion_services, beauty_channels, beauty_conversations, beauty_messages, beauty_ai_actions, beauty_reminders, beauty_payments (17 tables). Trigger for ends_at. EXCLUDE constraint for no-overlap. RLS ENABLE+FORCE on all.

### 2. `20261007123323_beauty_messaging_and_consent` (TASK-675)

Adds beauty_messages.status/attempts/last_error/idempotency_key. Adds beauty_clients.marketing_consent/unsubscribed. Policy channel_webhook_lookup on beauty_channels.

### 3. `20261007134016_auth_tenants_users_invites` (TASK-684)

Creates tenants, users, invites, refresh_tokens (auth layer). RLS on users/invites/refresh_tokens with tenant_isolation. Policy tenants_login_lookup on tenants.

### 4. `20261007134748_beauty_cancellation_settings` (TASK-685)

Creates beauty_cancellation_settings (1 row/tenant). RLS ENABLE+FORCE.

### 5. `20261007153917_beauty_public_booking` (TASK-688)

Adds public_token_hash, idempotency_key_hash, idempotency_request_hash to beauty_appointments. Partial unique indexes.

### 6. `20261008061428_beauty_staff_management` (TASK-691)

Adds beauty_specialists.position. Creates beauty_specialist_services (PK: tenant_id, specialist_id, service_id; cascade). Creates beauty_specialist_absences (full table with dates, type, status, note, requested_by_user_id, decided_by_user_id, decided_at). EXCLUDE constraint for no-overlap. RLS ENABLE+FORCE on all. Backfill: assign all services to all specialists.

### 7. `20261008092926_beauty_staff_hardening` (TASK-696)

Adds cancelled_by_user_id, cancelled_at to beauty_specialist_absences. DO-block verifies FORCE RLS on beauty_specialist_absences, beauty_specialists, beauty_services, beauty_specialist_services.

### 8. `20261008124507_beauty_locations_and_cancellation_history` (TASK-697)

Adds cancelled_by_type, cancelled_by_user_id, cancel_reason to beauty_appointments. CHECK constraints for cancel metadata. FK RESTRICT on users. Backfill: set cancelled_by_type='system' for existing cancelled rows (under NO FORCE, then FORCE again). DO-block verifies FORCE on beauty_appointments, beauty_locations.

---

## 5. Runtime Roles & GRANTs

**Design:** App runs under non-superuser role (e.g. `beautycrm_app`).

### Create roles (DBA/initial setup)
```sql
CREATE ROLE beautycrm_owner SUPERUSER CREATEDB CREATEROLE;
CREATE ROLE beautycrm_app NOSUPERUSER NOBYPASSRLS;
GRANT CONNECT ON DATABASE beautycrm TO beautycrm_app;
```

### GRANT per migration
After each migration, grant SELECT/INSERT/UPDATE/DELETE on new tables:

**Migration 1 (beauty_*):**
```sql
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO beautycrm_app
  WHERE tablename LIKE 'beauty_%';
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO beautycrm_app;
GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA public TO beautycrm_app;
```

**Migration 3 (auth tables):**
```sql
GRANT SELECT, INSERT, UPDATE, DELETE ON tenants, users, invites, refresh_tokens TO beautycrm_app;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO beautycrm_app;
```

**Runtime check (TASK-689, H1):** API `DbRoleGuard` queries `pg_roles` on startup, fails if runtime role has `rolsuper` or `rolbypassrls` (except Development + `Beauty__AllowPrivilegedDbRole=true`).

---

## 6. Local Setup

See `.claude/docs/runbook.md` for detailed PostgreSQL setup, role creation, and migration commands.

---

## 7. Performance Considerations

- **Indexes:** Composite (tenant_id, id) for fast tenant-scoped lookups; (tenant_id, is_active) for filtered queries.
- **RLS overhead:** Minor in read-heavy workloads; write operations have small policy evaluation cost. Indexes are used within policy scope.
- **Exclusion constraint:** `ex_beauty_appointments_specialist_no_overlap` prevents overlaps at DB level (no race conditions). Requires btree_gist extension.
- **Advisory locks:** Python-style advisory locks on `hashtextextended(tenant_id || ':specialist:' || specialist_id, 0)` for absence/appointment race conditions (per spec §13.4, M4).
- **Session pooling requirement:** Tenant_id is session-scoped; PgBouncer transaction pooling + Npgsql Multiplexing forbidden.

---

## 8. Testing RLS

Unit tests verify tenant isolation, overlaps, RLS enforcement:

```bash
cd backend
export BEAUTY_TEST_REQUIRE_DB=1
export BEAUTY_TEST_PG_ADMIN="Host=localhost;Username=postgres;Password=..."
dotnet test BeautyCrm.Tests
```

Tests: `TenantIsolationTests.cs`, `CancellationHistoryRlsTests.cs`, `StaffHardeningMigrationTests.cs`, etc.

---

## 9. Known Constraints

- No composite primary keys across tenants (all PKs include tenant_id as first column or via UNIQUE constraint).
- ForeignKey RESTRICT on users (can't delete user if referenced as cancelled_by, requested_by, decided_by in absences).
- DEFERRABLE INITIALLY DEFERRED on (tenant_id, email) and idempotency keys (allow bulk upserts).
- Partial unique indexes for public_token_hash, idempotency_key_hash (allow NULL).
- CHECK constraint dates for absences (daterange EXCLUDE gist) — validates at DB level, not app.

---

## 10. Backup & Restore

```bash
# Backup
pg_dump -U postgres beautycrm --blobs --roles > beautycrm.sql

# Restore
psql -U postgres < beautycrm.sql
# Recreate roles: CREATE ROLE beautycrm_app NOSUPERUSER NOBYPASSRLS; GRANT ...
```
