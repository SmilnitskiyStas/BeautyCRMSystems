# Beauty CRM — Database Schema & RLS

**Current version:** 0.4 (TASK-685, 2026-10-07)
**Database:** PostgreSQL 16+ (RLS-enabled)
**ORM:** EF Core 8.0 with Npgsql 8.0.11

---

## 1. Schema Overview

All `beauty_*` tables enforce **Row-Level Security (RLS)** with `FORCE` — even the table owner cannot bypass. The runtime PostgreSQL role must not be `superuser` or have `BYPASSRLS`.

**Tenant isolation:** Every `beauty_*` row has `tenant_id` (UUID); a session-scoped PostgreSQL setting `app.tenant_id` filters all access via the policy:

```sql
tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
```

---

## 2. Table Reference

### Catalog

#### beauty_locations
```sql
id              UUID PRIMARY KEY
tenant_id       UUID NOT NULL (RLS filtered)
name            VARCHAR NOT NULL
address         VARCHAR
phone           VARCHAR
timezone        VARCHAR NOT NULL (IANA: "Europe/Kyiv")
is_active       BOOLEAN DEFAULT true
created_at      TIMESTAMPTZ
updated_at      TIMESTAMPTZ
```
**Indexes:** (tenant_id, id); (tenant_id, is_active).
**RLS Policy:** `tenant_isolation` (USING/WITH CHECK).

#### beauty_specialists
```sql
id              UUID PRIMARY KEY
tenant_id       UUID NOT NULL (RLS filtered)
full_name       VARCHAR NOT NULL
title           VARCHAR
phone           VARCHAR
email           VARCHAR
photo_url       VARCHAR
is_active       BOOLEAN DEFAULT true
created_at      TIMESTAMPTZ
updated_at      TIMESTAMPTZ
```
**Indexes:** (tenant_id, id); (tenant_id, is_active).
**RLS Policy:** `tenant_isolation`.

#### beauty_specialist_locations
```sql
id              UUID PRIMARY KEY
tenant_id       UUID NOT NULL (RLS filtered)
specialist_id   UUID NOT NULL (FK beauty_specialists)
location_id     UUID NOT NULL (FK beauty_locations)
working_hours   JSONB (nullable, e.g. {"mon":[{"from":"09:00","to":"18:00"}],...})
is_active       BOOLEAN DEFAULT true
created_at      TIMESTAMPTZ
updated_at      TIMESTAMPTZ

UNIQUE (tenant_id, specialist_id, location_id)
```
**Format of working_hours:**
```json
{
  "mon": [{"from": "09:00", "to": "13:00"}, {"from": "14:00", "to": "18:00"}],
  "tue": [...],
  ...
  "sun": []  // empty = day off
}
```
All times are local to the location's timezone.

**RLS Policy:** `tenant_isolation`.

#### beauty_services
```sql
id              UUID PRIMARY KEY
tenant_id       UUID NOT NULL (RLS filtered)
name            VARCHAR NOT NULL
description     VARCHAR
category        VARCHAR
duration_minutes INT NOT NULL
is_active       BOOLEAN DEFAULT true
created_at      TIMESTAMPTZ
updated_at      TIMESTAMPTZ
```
**RLS Policy:** `tenant_isolation`.

#### beauty_service_prices
```sql
id              UUID PRIMARY KEY
tenant_id       UUID NOT NULL (RLS filtered)
service_id      UUID NOT NULL (FK beauty_services)
location_id     UUID (nullable: null = network-wide price, else location override)
price           NUMERIC(10, 2) NOT NULL
created_at      TIMESTAMPTZ
updated_at      TIMESTAMPTZ

CHECK (price >= 0)
UNIQUE (tenant_id, service_id, location_id)
```
**Lookup:** If a location override exists, use it; else use network price (location_id = NULL).

**RLS Policy:** `tenant_isolation`.

---

### Appointments & Clients

#### beauty_appointments
```sql
id              UUID PRIMARY KEY
tenant_id       UUID NOT NULL (RLS filtered)
location_id     UUID NOT NULL (FK beauty_locations)
specialist_id   UUID NOT NULL (FK beauty_specialists)
service_id      UUID NOT NULL (FK beauty_services)
client_id       UUID NOT NULL (FK beauty_clients)
starts_at       TIMESTAMPTZ NOT NULL (UTC)
duration_minutes INT NOT NULL (copied from service at booking time)
ends_at         TIMESTAMPTZ NOT NULL (DB trigger: starts_at + duration, read-only for app)
status          VARCHAR NOT NULL (CHECK IN ('pending','confirmed','completed','cancelled','no_show'))
source          VARCHAR NOT NULL (CHECK IN ('admin','online','telegram','instagram'))
price_original  NUMERIC(10, 2)
price_final     NUMERIC(10, 2)
promotion_id    UUID (nullable, FK beauty_promotions)
reminder_option VARCHAR (CHECK IN ('none','1h','2h'))
payment_method  VARCHAR (nullable, CHECK IN ('card','cash'))
cancelled_at    TIMESTAMPTZ (nullable, set by CancellationService)
created_at      TIMESTAMPTZ
updated_at      TIMESTAMPTZ

CHECK (starts_at < ends_at)
CONSTRAINT ex_beauty_appointments_specialist_no_overlap EXCLUDE
  USING gist (tenant_id WITH =, specialist_id WITH =, tstzrange(starts_at, ends_at, '[)') WITH &&)
  WHERE (status IN ('pending', 'confirmed', 'completed'))
```

**Trigger:** `trg_beauty_appointments_set_ends_at` (BEFORE INSERT/UPDATE) — computes `ends_at := starts_at + duration_minutes`.

**Indexes:** (tenant_id, client_id); (tenant_id, specialist_id, starts_at); (tenant_id, location_id, starts_at).

**RLS Policy:** `tenant_isolation`.

#### beauty_clients
```sql
id              UUID PRIMARY KEY
tenant_id       UUID NOT NULL (RLS filtered)
full_name       VARCHAR NOT NULL
phone           VARCHAR (nullable)
email           VARCHAR (nullable)
birth_date      DATE (nullable)
marketing_consent BOOLEAN DEFAULT false (only included in AI audiences if true)
unsubscribed    BOOLEAN DEFAULT false (if true, excluded from all campaigns)
deleted_at      TIMESTAMPTZ (nullable, soft delete)
created_at      TIMESTAMPTZ
updated_at      TIMESTAMPTZ

UNIQUE (tenant_id, phone) DEFERRABLE INITIALLY DEFERRED
CHECK (phone IS NULL OR phone ~ '^\+?[\d\s\-()]+$')
```

**Soft delete:** Rows with `deleted_at IS NOT NULL` are hidden from UI but retained for history.

**RLS Policy:** `tenant_isolation`.

#### beauty_client_notes
```sql
id              UUID PRIMARY KEY
tenant_id       UUID NOT NULL (RLS filtered)
client_id       UUID NOT NULL (FK beauty_clients)
author_user_id  UUID (nullable, FK users.id from auth schema)
body            TEXT NOT NULL
created_at      TIMESTAMPTZ
updated_at      TIMESTAMPTZ

ON DELETE CASCADE (if client deleted)
```

**RLS Policy:** `tenant_isolation`.

---

### Promotions

#### beauty_promotions
```sql
id              UUID PRIMARY KEY
tenant_id       UUID NOT NULL (RLS filtered)
name            VARCHAR NOT NULL
description     VARCHAR
discount_type   VARCHAR NOT NULL (CHECK IN ('percent','fixed'))
discount_value  NUMERIC(10, 2) NOT NULL
starts_at       TIMESTAMPTZ (nullable)
ends_at         TIMESTAMPTZ (nullable)
is_active       BOOLEAN DEFAULT true
created_at      TIMESTAMPTZ
updated_at      TIMESTAMPTZ

CHECK (discount_value > 0)
CHECK (starts_at IS NULL OR ends_at IS NULL OR starts_at < ends_at)
```

**RLS Policy:** `tenant_isolation`.

#### beauty_promotion_locations
```sql
tenant_id       UUID (RLS filtered)
promotion_id    UUID NOT NULL (FK beauty_promotions)
location_id     UUID NOT NULL (FK beauty_locations)
created_at      TIMESTAMPTZ

PRIMARY KEY (tenant_id, promotion_id, location_id)
```
Empty list (no rows) = promotion applies to all locations.

**RLS Policy:** `tenant_isolation`.

#### beauty_promotion_services
```sql
tenant_id       UUID (RLS filtered)
promotion_id    UUID NOT NULL (FK beauty_promotions)
service_id      UUID NOT NULL (FK beauty_services)
created_at      TIMESTAMPTZ

PRIMARY KEY (tenant_id, promotion_id, service_id)
```
Empty list (no rows) = promotion applies to all services.

**RLS Policy:** `tenant_isolation`.

---

### Channels & Messaging

#### beauty_channels
```sql
id              UUID PRIMARY KEY
tenant_id       UUID NOT NULL (RLS filtered)
location_id     UUID (nullable, FK beauty_locations)
type            VARCHAR NOT NULL (CHECK IN ('telegram','instagram','viber','whatsapp','facebook','widget'))
name            VARCHAR NOT NULL
credentials_encrypted BYTEA (nullable, AES-256-GCM encrypted JSON of {token, webhookSecret})
credentials_last4 VARCHAR(4) (nullable, masked display "****abcd")
settings        JSONB (nullable, non-secret settings)
is_active       BOOLEAN DEFAULT true
created_at      TIMESTAMPTZ
updated_at      TIMESTAMPTZ
```

**Encryption:** `credentials_encrypted` is AES-256-GCM using `Channels__EncryptionKey` (.env, base64-32-bytes). Decryption happens in `Infrastructure/Integrations/Channels/AesGcmSecretProtector`.

**RLS Policy:** `tenant_isolation`.

#### beauty_conversations
```sql
id              UUID PRIMARY KEY
tenant_id       UUID NOT NULL (RLS filtered)
channel_id      UUID NOT NULL (FK beauty_channels)
client_id       UUID (nullable, FK beauty_clients; may be unknown initially)
external_chat_id VARCHAR NOT NULL (channel's internal chat/thread ID)
status          VARCHAR (CHECK IN ('open','closed','archived'))
last_message_at TIMESTAMPTZ (nullable)
created_at      TIMESTAMPTZ
updated_at      TIMESTAMPTZ

UNIQUE (tenant_id, channel_id, external_chat_id)
```

**RLS Policy:** `tenant_isolation`.

#### beauty_messages
```sql
id              UUID PRIMARY KEY
tenant_id       UUID NOT NULL (RLS filtered)
conversation_id UUID NOT NULL (FK beauty_conversations)
direction       VARCHAR NOT NULL (CHECK IN ('inbound','outbound'))
sender_type     VARCHAR NOT NULL (CHECK IN ('client','assistant','specialist','system'))
body            TEXT NOT NULL
external_message_id VARCHAR (nullable, channel's message ID for editing)
sent_at         TIMESTAMPTZ
status          VARCHAR (CHECK IN ('received','draft','pending','sent','failed'))
attempts        INT DEFAULT 0
last_error      VARCHAR (nullable)
idempotency_key VARCHAR (nullable, unique per tenant for deduplication)

UNIQUE (tenant_id, idempotency_key) DEFERRABLE INITIALLY DEFERRED

ON DELETE CASCADE (if conversation deleted)
```

**Status flow:**
- Inbound: webhook → `received`
- Outbound: `draft` → `pending` (queued) → `sent` / `failed`

**RLS Policy:** `tenant_isolation`.

#### beauty_ai_actions
```sql
id              UUID PRIMARY KEY
tenant_id       UUID NOT NULL (RLS filtered)
conversation_id UUID (nullable, FK beauty_conversations)
appointment_id  UUID (nullable, FK beauty_appointments)
tool_name       VARCHAR NOT NULL (e.g. "create_appointment")
payload         JSONB NOT NULL (tool arguments)
status          VARCHAR (CHECK IN ('proposed','approved','rejected','executed','reverted','error'))
result          JSONB (nullable, execution result)
error           VARCHAR (nullable, if status='error')
confirmed_by_user_id UUID (nullable, FK users.id)
confirmed_at    TIMESTAMPTZ (nullable)
executed_at     TIMESTAMPTZ (nullable)
created_at      TIMESTAMPTZ
updated_at      TIMESTAMPTZ
```

**Audit trail:** Every AI action is logged, including who approved/rejected it and when.

**RLS Policy:** `tenant_isolation`.

#### beauty_reminders
```sql
id              UUID PRIMARY KEY
tenant_id       UUID NOT NULL (RLS filtered)
appointment_id  UUID NOT NULL (FK beauty_appointments)
scheduled_at    TIMESTAMPTZ NOT NULL
status          VARCHAR (CHECK IN ('scheduled','sent','failed'))
sent_at         TIMESTAMPTZ (nullable)
error           VARCHAR (nullable)
created_at      TIMESTAMPTZ
updated_at      TIMESTAMPTZ

ON DELETE CASCADE (if appointment deleted)
```

**RLS Policy:** `tenant_isolation`.

#### beauty_payments
```sql
id              UUID PRIMARY KEY
tenant_id       UUID NOT NULL (RLS filtered)
appointment_id  UUID NOT NULL (FK beauty_appointments)
amount          NUMERIC(10, 2) NOT NULL
method          VARCHAR NOT NULL (CHECK IN ('card','cash'))
status          VARCHAR (CHECK IN ('pending','paid','failed','refunded'))
provider        VARCHAR (nullable, e.g. "stripe")
provider_payment_id VARCHAR (nullable)
paid_at         TIMESTAMPTZ (nullable)
created_at      TIMESTAMPTZ
updated_at      TIMESTAMPTZ

ON DELETE CASCADE (if appointment deleted)
```

**RLS Policy:** `tenant_isolation`.

---

### Cancellation Settings

#### beauty_cancellation_settings
```sql
id              UUID PRIMARY KEY
tenant_id       UUID NOT NULL UNIQUE (one row per tenant)
window_hours    INT NOT NULL (0..720)
refund_percent_in_window INT NOT NULL (0..100, %within time window)
refund_percent_outside INT NOT NULL (0..100, % after time window)
deduct_fee      BOOLEAN DEFAULT false
fee_percent     INT NOT NULL (0..100, only applied if deduct_fee=true)
created_at      TIMESTAMPTZ
updated_at      TIMESTAMPTZ

CHECK (window_hours BETWEEN 0 AND 720)
CHECK (refund_percent_in_window BETWEEN 0 AND 100)
CHECK (refund_percent_outside BETWEEN 0 AND 100)
CHECK (fee_percent BETWEEN 0 AND 100)
```

**Default values (if row absent):** `window_hours=12`, `refund_percent_in_window=50`, `refund_percent_outside=100`, `deduct_fee=false`, `fee_percent=0`.

**RLS Policy:** `tenant_isolation` (ENABLE ROW LEVEL SECURITY FORCE).

---

### Authentication (separate schema: `public` or `auth`)

#### tenants (auth schema)
```sql
id              UUID PRIMARY KEY
name            VARCHAR NOT NULL
slug            VARCHAR NOT NULL UNIQUE
modules         TEXT[] (array of 'beauty_booking', 'beauty_catalog', etc.)
status          VARCHAR (CHECK IN ('active','suspended'))
created_at      TIMESTAMPTZ
updated_at      TIMESTAMPTZ
```

**No RLS** (runtime role can SELECT; row-level access controlled by login flow).

#### users (auth schema)
```sql
id              UUID PRIMARY KEY
tenant_id       UUID NOT NULL (FK tenants)
email           VARCHAR NOT NULL
full_name       VARCHAR NOT NULL
role            VARCHAR NOT NULL (CHECK IN ('owner','admin','specialist'))
specialist_id   UUID (nullable, FK beauty_specialists)
is_active       BOOLEAN DEFAULT true
password_hash   VARCHAR NOT NULL (bcrypt)
created_at      TIMESTAMPTZ
updated_at      TIMESTAMPTZ

UNIQUE (tenant_id, email) DEFERRABLE INITIALLY DEFERRED
```

**RLS Policy:** `tenant_isolation` (USING/WITH CHECK: `tenant_id = app.tenant_id`).

#### invites (auth schema)
```sql
id              UUID PRIMARY KEY
tenant_id       UUID NOT NULL (FK tenants)
email           VARCHAR NOT NULL
role            VARCHAR NOT NULL (CHECK IN ('admin','specialist'))
specialist_id   UUID (nullable, FK beauty_specialists)
token_hash      VARCHAR NOT NULL (SHA-256, one-time)
is_used         BOOLEAN DEFAULT false
expires_at      TIMESTAMPTZ NOT NULL
created_at      TIMESTAMPTZ

UNIQUE (tenant_id, email, is_used=false)
```

**RLS Policy:** `tenant_isolation`.

#### refresh_tokens (auth schema)
```sql
id              UUID PRIMARY KEY
user_id         UUID NOT NULL (FK users)
token_hash      VARCHAR NOT NULL (SHA-256)
issued_at       TIMESTAMPTZ NOT NULL
expires_at      TIMESTAMPTZ NOT NULL
revoked_at      TIMESTAMPTZ (nullable, token invalidated)
```

**RLS Policy:** `tenant_isolation` (user_id FK to users which is already filtered).

---

## 3. Row-Level Security (RLS)

### Policy: `tenant_isolation`

Applied to all `beauty_*`, `users`, `invites`, `refresh_tokens` tables.

```sql
CREATE POLICY tenant_isolation ON <table_name>
  USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
  WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
```

**Usage:**
1. Before **any** database operation in a request, set the PostgreSQL session variable:
   ```csharp
   await connection.ExecuteAsync("SELECT set_config('app.tenant_id', @tid::text, true)",
       new { tid = tenantId.ToString() });
   ```
2. All SELECT/INSERT/UPDATE/DELETE operations on RLS-protected tables will be automatically filtered.
3. The runtime role must **not** have `BYPASSRLS` privilege; use `FORCE ROW LEVEL SECURITY` to prevent bypass.

### Special Policy: `channel_webhook_lookup` (beauty_channels, SELECT only)

For webhooks (which receive a channel ID but not a tenant ID), a custom policy allows resolving the channel's tenant:

```sql
CREATE POLICY channel_webhook_lookup ON beauty_channels
  FOR SELECT
  USING (id = NULLIF(current_setting('app.channel_id', true), '')::uuid);
```

**Webhook flow:**
1. Webhook handler reads the channel ID from URL.
2. Sets `app.channel_id` to that ID, leaves `app.tenant_id` empty.
3. Executes `SELECT * FROM beauty_channels WHERE id = ...` → policy returns exactly that row.
4. Reads `tenant_id` from the row.
5. Sets `app.tenant_id` to the resolved tenant ID.
6. Executes remaining queries (conversations, messages, etc.) under full RLS.

---

## 4. Migrations (in order of application)

### Migration 1: `20261007115130_add_beauty_schema`

**What it does:**
- Creates all `beauty_*` tables with columns, constraints, and indexes.
- Creates trigger `trg_beauty_appointments_set_ends_at` for `ends_at` calculation.
- Creates EXCLUDE constraint `ex_beauty_appointments_specialist_no_overlap` (requires `btree_gist` extension).
- Applies RLS ENABLE/FORCE and `tenant_isolation` policy to all 17 tables.

**Extensions required:**
```sql
CREATE EXTENSION IF NOT EXISTS btree_gist;
```

**File:**
- `backend/BeautyCrm.Infrastructure/Data/Migrations/20261007115130_add_beauty_schema.cs` (EF-generated)
- `backend/BeautyCrm.Infrastructure/Data/Migrations/20261007115130_add_beauty_schema.Sql.cs` (hand-written RLS & trigger SQL)

**Run:**
```bash
cd backend
dotnet ef database update --project BeautyCrm.Infrastructure \
  --startup-project BeautyCrm.Api
```

### Migration 2: `20261007123323_beauty_messaging_and_consent`

**What it does:**
- Adds `beauty_messages.status`, `attempts`, `last_error`, `idempotency_key` (unique per tenant).
- Adds `beauty_clients.marketing_consent`, `unsubscribed`.
- Creates policy `channel_webhook_lookup` on `beauty_channels`.

**File:**
- `backend/BeautyCrm.Infrastructure/Data/Migrations/20261007123323_beauty_messaging_and_consent.cs`

### Migration 3: `20261007134016_auth_tenants_users_invites`

**What it does:**
- Creates `tenants`, `users`, `invites`, `refresh_tokens` tables (auth layer).
- Applies RLS policy `tenant_isolation` on users/invites/refresh_tokens.
- Creates policy `tenants_login_lookup` on `tenants` (allow login lookup by slug).

**File:**
- `backend/BeautyCrm.Infrastructure/Data/Migrations/20261007134016_auth_tenants_users_invites.cs`

### Migration 4: `20261007134748_beauty_cancellation_settings`

**What it does:**
- Creates `beauty_cancellation_settings` table (one row per tenant).
- Applies RLS policy `tenant_isolation` with FORCE.

**File:**
- `backend/BeautyCrm.Infrastructure/Data/Migrations/20261007134748_beauty_cancellation_settings.cs`

---

## 5. Runtime Roles & GRANTs

**Design:** The application runs under a non-superuser PostgreSQL role (e.g. `beautycrm_app`).

### Create role
```sql
CREATE ROLE beautycrm_app LOGIN PASSWORD 'strong_password';
GRANT CONNECT ON DATABASE beautycrm TO beautycrm_app;
```

### Grant privileges per migration
After each migration, grant SELECT/INSERT/UPDATE/DELETE on new tables:

**For all `beauty_*` tables:**
```sql
GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE
  beauty_locations, beauty_specialists, beauty_specialist_locations,
  beauty_services, beauty_service_prices, beauty_appointments,
  beauty_clients, beauty_client_notes, beauty_promotions,
  beauty_promotion_locations, beauty_promotion_services,
  beauty_channels, beauty_conversations, beauty_messages,
  beauty_ai_actions, beauty_reminders, beauty_payments,
  beauty_cancellation_settings
  TO beautycrm_app;

-- Sequences for INSERT
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO beautycrm_app;
```

**For auth tables:**
```sql
GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE
  tenants, users, invites, refresh_tokens
  TO beautycrm_app;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO beautycrm_app;
```

**For functions/triggers:**
```sql
GRANT EXECUTE ON FUNCTION beauty_appointments_set_ends_at() TO beautycrm_app;
```

---

## 6. Local Setup (Docker/Manual)

See `runbook.md` for detailed PostgreSQL setup and migration commands.

---

## 7. Known Discrepancies (checked at TASK-683)

See `.claude/docs/known-discrepancies.md` for list of contract vs. code differences, if any are found during review.

---

## 8. Performance Considerations

- **Indexes:** Composite indexes on (tenant_id, id) for fast lookups within a tenant.
- **RLS overhead:** Minor in read-heavy workloads; write operations have small policy overhead.
- **Exclusion constraint:** `ex_beauty_appointments_specialist_no_overlap` prevents overlaps at database level, avoiding application-level race conditions.
- **Trigger computation:** `ends_at` calculation via trigger is STABLE (deterministic); can be used in indexes if needed later.

---

## 9. Backup & Restore

When backing up, ensure `pg_dump` includes all roles & extensions:

```bash
pg_dump -U postgres beautycrm --blobs --roles > beautycrm.sql
```

When restoring, recreate the `beautycrm_app` role before restoring data.

---

## 10. Testing RLS

Unit tests in `backend/BeautyCrm.Tests/Data/TenantIsolationTests.cs` verify:
- Tenant A cannot see Tenant B's data.
- Bulk operations (UPDATE/DELETE) respect tenant context.
- Overlapping appointments correctly blocked by exclusion constraint.

To run tests locally with PostgreSQL:

```bash
cd backend
BEAUTY_TEST_PG_ADMIN="Host=localhost;Username=postgres;Password=..." \
dotnet test BeautyCrm.Tests
```
