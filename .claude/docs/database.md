# Beauty CRM — Database Schema & RLS

**Current version:** 0.9 (ADR-022 public location catalog, 2026-11-09; TASK-697/698, 2026-10-08)
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
timezone        VARCHAR(64) NOT NULL (IANA: "Europe/Kyiv"; validated per domain-model §9)
is_active       BOOLEAN DEFAULT true
created_at      TIMESTAMPTZ NOT NULL
updated_at      TIMESTAMPTZ NOT NULL
closed_weekdays TEXT[] (migration 9, weekly closed days mon..sun)
working_hours   JSONB (migration 10, location working hours)
public_code     VARCHAR(8) NOT NULL (ADR-022, DEFAULT public.beauty_generate_public_code(), globally unique, immutable)
latitude        DOUBLE PRECISION NULL (ADR-022, -90..90)
longitude       DOUBLE PRECISION NULL (ADR-022, -180..180)
city            VARCHAR(100) NULL (ADR-022, city search only)
is_discoverable BOOLEAN NOT NULL DEFAULT false (ADR-022, opt-in to nearby and city search)

UNIQUE (tenant_id, name) -- case-insensitive via lower()
CHECK (char_length(name) >= 1 AND char_length(name) <= 200)
CHECK (char_length(address) IS NULL OR char_length(address) <= 500)
CHECK ck_beauty_locations_public_code: public_code ~ '^[0-9A-HJKMNP-TV-Z]{8}$'
CHECK ck_beauty_locations_latitude / ck_beauty_locations_longitude: NULL or range
CHECK ck_beauty_locations_coords_pair: (latitude IS NULL) = (longitude IS NULL)
CHECK ck_beauty_locations_closed_weekdays: closed_weekdays <@ ARRAY['mon',...,'sun']
```

**Indexes:** (tenant_id, id); (tenant_id, is_active);
`ux_beauty_locations_public_code` UNIQUE (public_code) — **global**, not per tenant (ADR-022);
`ix_beauty_locations_discoverable_coords` (latitude, longitude) WHERE is_discoverable AND is_active AND latitude IS NOT NULL;
`ix_beauty_locations_discoverable_city` (lower(city)) WHERE is_discoverable AND is_active AND city IS NOT NULL.

**RLS Policy:** `tenant_isolation` (ENABLE ROW LEVEL SECURITY FORCE). Cross-tenant public reads of the catalog columns
(`public_code`, `latitude`, `longitude`, `city`, `is_discoverable`, `name`, `address`, `timezone`, `is_active`) go only via
the `beautycrm_catalog` role policy `public_catalog_read` and the SECURITY DEFINER functions (§5).

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
practice_start_year INTEGER NULL  -- CHECK 1950..2100 (поточний рік перевіряє сервіс)
bio             VARCHAR(500) NULL -- CHECK char_length <= 500
is_active       BOOLEAN DEFAULT true
created_at      TIMESTAMPTZ NOT NULL
updated_at      TIMESTAMPTZ NOT NULL

UNIQUE (tenant_id, id)
```

**Indexes:** (tenant_id, id); (tenant_id, is_active).
**RLS Policy:** `tenant_isolation` (ENABLE ROW LEVEL SECURITY FORCE).
**Notes (public profile):** `photo_url` — лише https-URL до 500 символів перевіряє сервіс (у БД ліміт лишається 2048, щоб не ламати наявні рядки).

#### beauty_specialist_reviews
```sql
id              UUID PRIMARY KEY DEFAULT gen_random_uuid()
tenant_id       UUID NOT NULL (RLS filtered)
specialist_id   UUID NOT NULL (composite FK (tenant_id, specialist_id) -> beauty_specialists, ON DELETE CASCADE)
appointment_id  UUID NULL (composite FK -> beauty_appointments, RESTRICT)
client_id       UUID NULL (composite FK -> beauty_clients, RESTRICT)
author_name     VARCHAR(100) NOT NULL  -- вже у вигляді «Ім'я К.», обчислюється при створенні
rating          SMALLINT NOT NULL      -- CHECK 1..5
text            VARCHAR(1000) NULL
is_published    BOOLEAN NOT NULL DEFAULT true
created_at      TIMESTAMPTZ NOT NULL DEFAULT now()

UNIQUE (tenant_id, id)
```

**Indexes:** UNIQUE `ux_beauty_specialist_reviews_appointment` (tenant_id, appointment_id) WHERE appointment_id IS NOT NULL (один відгук на запис); `ix_beauty_specialist_reviews_specialist_published` (tenant_id, specialist_id, is_published, created_at DESC); (tenant_id, client_id).
**RLS Policy:** `tenant_isolation` (ENABLE + FORCE).
**Notes:** публічний API віддає лише `is_published`; client_id/appointment_id у відповідь не потрапляють. Створення відгуків клієнтом і адмін-модерація — окремі кроки (ще не реалізовані).

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
reminder_option VARCHAR(32) NOT NULL (CHECK IN ('none','1h','2h','24h'))
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
type            VARCHAR(32) NOT NULL (CHECK IN ('telegram','instagram','viber','whatsapp','facebook','widget','email'))
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

**Email channel:** `type='email'` (one lazily created row per tenant, no credentials; SMTP is global in the worker). Client address = `beauty_conversations.external_chat_id` (lowercase), `last_message_at` stays NULL so a messenger conversation wins in the worker's `CLIENT_SQL`. Confirmation = `beauty_messages` (outbound, system, `pending`, `idempotency_key = 'confirmation:{appointmentId}'`).

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
show_terms_to_client BOOLEAN NOT NULL DEFAULT true  -- показ умов клієнту (лише презентація)
custom_terms_text TEXT NULL                          -- власний текст умов, ≤ 500
created_at      TIMESTAMPTZ NOT NULL
updated_at      TIMESTAMPTZ NOT NULL

CHECK (window_hours BETWEEN 0 AND 720)
CHECK (refund_percent_in_window BETWEEN 0 AND 100)
CHECK (refund_percent_outside BETWEEN 0 AND 100)
CHECK (fee_percent BETWEEN 0 AND 100)
CHECK (custom_terms_text IS NULL OR char_length(custom_terms_text) <= 500)  -- ck_beauty_cancellation_settings_terms_text
```

**Default values (if row absent):** `window_hours=12`, `refund_percent_in_window=50`, `refund_percent_outside=100`, `deduct_fee=false`, `fee_percent=0`, `show_terms_to_client=true`, `custom_terms_text=NULL`. Прапорець/текст керують лише показом клієнту й не впливають на розрахунок повернення.

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
external_ref    VARCHAR(64) NULL (CHECK `^[A-Za-z0-9_-]{1,64}$`; ShelfGuard enterprise id, ADR-018)
external_source VARCHAR(16) NULL (CHECK IN ('shelfguard'); set together with external_ref)
suspended_at    TIMESTAMPTZ NULL (only while status='suspended')
suspended_reason VARCHAR(500) NULL
created_at      TIMESTAMPTZ NOT NULL
updated_at      TIMESTAMPTZ NOT NULL

UNIQUE (external_source, external_ref) WHERE external_ref IS NOT NULL  -- ux_tenants_external_ref
```

**RLS:** ENABLE + FORCE RLS. Policies: `tenant_self` (`app.tenant_id`), `tenants_login_lookup` (SELECT by `app.tenant_slug`), `tenants_control_lookup` (SELECT by `app.tenant_external_ref`, only `external_source='shelfguard'`; see §3). Suspended tenant → `403 module_disabled` on staff module access; public booking/widget → `403 tenant_suspended` (ADR-018).

#### users
```sql
id              UUID PRIMARY KEY
tenant_id       UUID NOT NULL (FK tenants)
email           VARCHAR NOT NULL
full_name       VARCHAR NOT NULL
role            VARCHAR NOT NULL (CHECK IN ('owner','admin','deputy','specialist'))  -- deputy: ADR-023
access_version  INTEGER NOT NULL DEFAULT 1  -- ADR-023: +1 за зміни ролі, прав, активності (X-Access-Version)
specialist_id   UUID (nullable, FK beauty_specialists, composite FK (tenant_id, specialist_id))
is_active       BOOLEAN DEFAULT true
password_hash   VARCHAR NULL (bcrypt, workfactor 12; NULL only while state='invited')
state           VARCHAR NOT NULL DEFAULT 'active' (CHECK IN ('active','invited'))
identity_id     UUID NOT NULL (FK identities(id) ON DELETE RESTRICT; ADR-024; UNIQUE (tenant_id, identity_id) — одне членство на людину в бізнесі)
sessions_valid_after TIMESTAMPTZ NULL (ADR-024 E-5: токени з iat раніше за цей момент → 401 session_revoked; ставлять скидання пароля, злиття, звільнення пошти)
created_at      TIMESTAMPTZ NOT NULL
updated_at      TIMESTAMPTZ NOT NULL

UNIQUE (tenant_id, email) DEFERRABLE INITIALLY DEFERRED
CHECK (password_hash IS NOT NULL OR state = 'invited')  -- ADR-018, platform_control_plane
```

**Deactivation (TASK-696):** owner never auto-disabled; specialist deactivation → user.is_active=false (except owner), refresh tokens revoked, pending invites revoked.

**RLS Policy:** `tenant_isolation` (USING/WITH CHECK: `tenant_id = app.tenant_id`).

#### invites
```sql
id              UUID PRIMARY KEY
tenant_id       UUID NOT NULL (FK tenants)
email           VARCHAR NOT NULL
role            VARCHAR NOT NULL (CHECK IN ('admin','deputy','specialist'))  -- deputy: ADR-023
permissions     JSONB NOT NULL (CHECK jsonb_typeof = 'object'; ADR-023: повний знімок прав, обрізаний стелею запрошувача; міграція backfill-ить пресет ролі; DEFAULT '{}' лишено для сумісності зі старими образами API, див. known-issues)
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

#### user_permissions (ADR-023, міграція 20261115120000)
```sql
tenant_id          UUID NOT NULL
user_id            UUID NOT NULL
permission         TEXT NOT NULL      -- 'calendar.view'; CHECK ^[a-z_]{1,32}\.[a-z_]{1,32}$
level              TEXT NOT NULL      -- 'own'; CHECK ^[a-z_]{1,16}$
updated_at         TIMESTAMPTZ NOT NULL DEFAULT now()
updated_by_user_id UUID NULL          -- NULL = міграція/система; FK RESTRICT
PRIMARY KEY (tenant_id, user_id, permission)
FK (tenant_id, user_id) -> users (tenant_id, id) ON DELETE CASCADE
```
- Матеріалізовані значення: що в БД, те й діє. Для owner рядків немає (права неявні). Валідність ключів і рівнів перевіряє Application за каталогом; БД перевіряє лише формат.
- **RLS:** ENABLE + FORCE, `tenant_isolation` (`app.tenant_id`).

#### user_access_audit (ADR-023, append-only)
```sql
id              UUID PRIMARY KEY
tenant_id       UUID NOT NULL
actor_user_id   UUID NULL      -- NULL = міграція/система; FK RESTRICT
target_user_id  UUID NULL      -- NULL для подій запрошення (Q-C2); FK RESTRICT
invite_id       UUID NULL      -- FK invites (tenant_id, id) RESTRICT
action          VARCHAR(32) NOT NULL  -- permissions_changed|role_changed|preset_reset|invited|invite_revoked|invite_accepted|activated|deactivated|migrated
changes         JSONB NOT NULL        -- array або object (diff: [{permission, from, to}] / {role:{from,to}})
created_at      TIMESTAMPTZ NOT NULL DEFAULT now()
CHECK (target_user_id IS NOT NULL OR invite_id IS NOT NULL)
```
- **RLS:** ENABLE + FORCE, `tenant_isolation`. Додатково політики `audit_purge_select` і `audit_purge_delete` лише `TO beautycrm_audit_purge` (`USING (true)`, для retention між tenant-ами).
- **Append-only:** `BEFORE UPDATE OR DELETE` (row) і `BEFORE TRUNCATE` (statement) тригер `user_access_audit_block_mutation`, що кидає `42501`. Єдиний виняток: рядковий `DELETE`, коли `current_user = 'beautycrm_audit_purge'`. Роль застосунку має лише `SELECT, INSERT` (`REVOKE UPDATE, DELETE` у `grants.sql`).
- Індекси: `(tenant_id, target_user_id, created_at DESC)`, `(tenant_id, created_at DESC, id DESC)`, `(tenant_id, actor_user_id)`, часткові `(tenant_id, invite_id, created_at DESC) WHERE invite_id IS NOT NULL`.
- **Journal не містить ПІІ клієнтів, паролів, токенів чи email.**

#### beauty_appointments.created_by_user_id (ADR-023)
`uuid NULL`, композитний FK `(tenant_id, created_by_user_id) -> users` RESTRICT, частковий індекс `(tenant_id, created_by_user_id) WHERE NOT NULL`. Наявні рядки NULL. Використовується scope `clients.view = own` (автор скасованого запису scope не розширює). Заповнює `POST /appointments` (TASK-852).

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

### Global identity (ADR-024, міграція `20261117140201_auth_global_identities`, TASK-862)

Перша глобальна (не-tenant) група таблиць з ПІІ й хешами. Виняток з ADR-002, компенсований deny-all RLS і функціями `SECURITY DEFINER`. Ідентичність — людина; членство — рядок `users` (без змін, `users.id` і 30 FK лишаються).

#### identities
```sql
id                  UUID PRIMARY KEY DEFAULT gen_random_uuid()
email               VARCHAR(320) NOT NULL  -- нормалізована lower(btrim()); CHECK (email = lower(email)); UNIQUE (ux_identities_email)
password_hash       VARCHAR(255) NULL      -- bcrypt; NULL лише у pending
state               VARCHAR(16) NOT NULL   -- 'pending' (запрошений власник без пароля) | 'active'
last_login_at       TIMESTAMPTZ NULL
password_changed_at TIMESTAMPTZ NULL
created_at, updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
CHECK ((state = 'active') = (password_hash IS NOT NULL))
```
Лічильники блокування тут **немає** (E-2): блокування в `auth_login_attempts`.

#### auth_login_attempts (E-2)
Ключ `login_key = HMAC-SHA256(Auth:LoginAttemptKeyPepper, email_norm)` (сира пошта не зберігається). Лічильник невдач у вікні `Auth:LoginAttemptWindowSeconds` (900), блокування `Auth:LockoutMinutes`. Однаково для наявної, невідомої і pending пошти. Спливлі рядки видаляє `auth_identity_purge_expired`.

#### auth_login_tickets
`token_hash` (sha256 від 32 байтів; UNIQUE), `identity_id`, `expires_at` (TTL 180 с), `consumed_at`. Одноразовий, нова видача гасить попередній квиток ідентичності.

#### auth_password_resets
`token_hash` (sha256, NULL до першої спроби доставки), `identity_id`, `login_key`, `expires_at` (30 хв), `consumed_at`, `superseded_at`; черга доставки: `delivery_status`, `attempts`, `next_attempt_at`, `last_error` (код без токена й пошти); `notice_status` (`none | pending | sent | failed`) для листа «Пароль змінено» (TASK-872). **Сирого токена немає ніде** (E-4): його генерує воркер.

**Гранти й RLS (чекліст E-8, §31.16):**
- Роль `beautycrm_identity` (NOLOGIN NOSUPERUSER NOBYPASSRLS NOINHERIT) — власник функцій. Роль `beautycrm_identity_release` (NOLOGIN, та сама конфігурація) — лише для `auth_identity_release`. Мігратор має бути членом обох `WITH INHERIT FALSE, SET TRUE`.
- Чотири таблиці: `ENABLE + FORCE RLS`, політики лише `TO beautycrm_identity` (і `TO beautycrm_identity_release` для звільнення). Для `beautycrm_app` і воркера — deny-all. `REVOKE ALL` на цих таблицях робить `deploy/staging/db/grants.sql` і `scripts/dev/setup-db.ps1` **після** `GRANT ON ALL TABLES` (порядок важливий).
- Колонкові гранти власнику функцій: `users` (SELECT `id, tenant_id, identity_id, role, is_active, state, access_version`; UPDATE `password_hash` — до фази 3), `tenants` (SELECT `id, name, slug, status`), `refresh_tokens` (SELECT `tenant_id, user_id, revoked_at`; UPDATE `revoked_at`).
- Функції `public.auth_*` (20 шт., `SECURITY DEFINER`, `search_path = pg_catalog, pg_temp`, `REVOKE ALL FROM PUBLIC`, явний `GRANT EXECUTE` лише `beautycrm_app`, воркеру — `claim/attach_token/mark/purge`, `auth_identity_release` — лише `beautycrm_identity_release`/ops). Жодна не повертає список пошт чи хеші інакше, ніж за точною поштою.
- Тригер узгодженості: `users.email` = `identities.email` для того самого `identity_id` (SECURITY DEFINER, `23514`). Зміна пошти — лише разом в обох таблицях.
- Стартова перевірка ізоляції API: `DbRoleGuard` (фейлить, якщо app-роль має доступ до глобальних таблиць).

**Міграція:** `auth_global_identities` — backfill однієї ідентичності на `lower(btrim(email))`; групи з кількома членствами зливаються **лише** за allowlist `beautycrm.identity_merge_allowlist` (8-hex `email_key` з preflight; синонім `beautycrm.identity_merge_allow`; різні значення → помилка). Без allowlist при дублікатах — `RAISE EXCEPTION`. Групи з invited-власником або без активного членства з паролем — `RAISE` (вручну до міграції). `users.password_hash` не змінює. У злитій групі в тій самій транзакції відкликаються refresh-токени всіх членств і ставиться `sessions_valid_after = now`. `Down` відмовляє, якщо є активне членство з `users.password_hash IS NULL`. Процедура — runbook §2b.

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

### Special Policy: `tenants_control_lookup` (tenants, SELECT only, TASK-816 / ADR-018)

For the ShelfGuard control plane, which knows only `external_ref` (not `tenant_id`). `tenants` is FORCE RLS, so without this
the registry cannot be read by external id:

```sql
CREATE POLICY tenants_control_lookup ON tenants
  FOR SELECT
  USING (external_source = 'shelfguard'
         AND external_ref = NULLIF(current_setting('app.tenant_external_ref', true), ''));
```

Set `app.tenant_external_ref` with `set_config(..., true)` (transaction-local), resolve `tenant_id`, then set `app.tenant_id`
(also transaction-local) for writes. No `BYPASSRLS` role is involved.

---

## 4. Migrations (24 total in the Migrations directory; 15 described below)

> Примітка: детально описані міграції 1–15 нижче; не описані тут (джерело істини — `backend/BeautyCrm.Infrastructure/Data/Migrations/`): `20261024091314_beauty_ai_settings_and_usage`, `20261024102153_beauty_chat_assistant`, `20261024193048_beauty_reminders_contacts_inbox`, `20261025080340_beauty_chat_buttons`, `20261026091203_beauty_service_costs`, `20261026093343_beauty_client_phone_capture`, `20261026203521_beauty_marketing`, `20261107134706_beauty_chat_booking_flow`, `20261107145615_beauty_chat_booking_flow_step_checks`.


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

### 9. `20261010130434_beauty_location_closures` (TASK-701)

Adds beauty_locations.closed_weekdays (TEXT[], CHECK on weekday codes). Creates beauty_location_closures (date-range closures per location) with FKs and indexes.

### 10. `20261010135605_beauty_location_working_hours` (TASK-703)

Adds beauty_locations.working_hours (JSONB).

### 11. `20261109075625_beauty_public_location_catalog` (ADR-022)

Adds beauty_locations.public_code (backfilled by `public.beauty_generate_public_code()`), city, is_discoverable; unique index `ux_beauty_locations_public_code`; CHECK `ck_beauty_locations_public_code`. Columns `latitude`/`longitude` (`numeric(9,6)`) and CHECK `ck_beauty_locations_coordinates` already come from `20261107134706_beauty_chat_booking_flow` (agent-system-v2) and are NOT re-created here; partial indexes for nearby and city search. Creates role `beautycrm_catalog` if missing, column grants, `public_catalog_read` policies, and the three SECURITY DEFINER functions (§5). Down drops functions, policies, indexes and checks; the cluster role is kept.

### 12. `20261109135452_beauty_specialist_profile_reviews` (мобільний потік «послуга → майстер → дата → час»)

Adds beauty_specialists.practice_start_year, bio (+ CHECKs). Creates beauty_specialist_reviews (§ вище) with RLS ENABLE+FORCE, `tenant_isolation`, guarded `GRANT SELECT, INSERT, UPDATE, DELETE ... TO beautycrm_app` (лише якщо роль існує) і DO-блоком, що падає, якщо FORCE RLS не ввімкнено. Down робить DROP TABLE відгуків і прибирає колонки/CHECK-и майстра. Покрито `SpecialistReviewsMigrationTests` (Up/Down/Up, RLS, унікальність запису, CHECK-и).

### 13. `20261109150000_beauty_reminder_24h_email_channel` (написана вручну)

Лише заміна CHECK-ів: `ck_beauty_appointments_reminder_option` + `'24h'` (набір: `none, 1h, 2h, custom, 24h` — `custom` з `20261024193048` збережено), `ck_beauty_channels_type` + `'email'`. Down (RAISE EXCEPTION, якщо є рядки з `24h`/`email`) повертає старі CHECK-и. Покрито `ReminderEmailMigrationTests`.

### 14. `20261110090000_beauty_cancellation_terms_visibility` (написана вручну)

`beauty_cancellation_settings` + `show_terms_to_client boolean NOT NULL DEFAULT true`, `custom_terms_text text NULL`, CHECK `ck_beauty_cancellation_settings_terms_text` (≤ 500). `ADD COLUMN IF NOT EXISTS`; наявні рядки = true/NULL. Down видаляє CHECK і колонки. Покрито `CancellationTermsMigrationTests`.

### 16. `20261115120000_auth_user_permissions` (TASK-849, ADR-023; написана вручну)

- `users.access_version` (DEFAULT 1); `ck_users_role` + `deputy`. `invites.role` CHECK + `deputy`; `invites.permissions jsonb` (спочатку NULL, після backfill NOT NULL, `DEFAULT '{}'`, `ck_invites_permissions` jsonb object).
- `beauty_appointments.created_by_user_id` (див. розділ 2).
- Таблиці `user_permissions`, `user_access_audit` (див. розділ 2), тригер append-only, RLS FORCE.
- Backfill: для кожного `admin` і `specialist` вставляються рядки пресету (`ON CONFLICT DO NOTHING`); у журнал `migrated` (`actor = NULL`) лише за фактично вставлені рядки, тож повторний запуск нічого не дублює. Наявні запрошення отримують знімок пресету своєї ролі (лише `permissions IS NULL`). Для owner нічого не пишеться.
- Роль `beautycrm_audit_purge` (NOLOGIN NOSUPERUSER NOBYPASSRLS NOINHERIT): створюється міграцією, якщо мігратор має `CREATEROLE`; інакше міграція падає з інструкцією ops. Мігратор має бути членом `WITH INHERIT FALSE, SET TRUE` (міграція видає членство сама, якщо може).
- Функція `public.purge_user_access_audit(p_before timestamptz) RETURNS bigint`, `SECURITY DEFINER`, `search_path = pg_catalog, pg_temp`, власник `beautycrm_audit_purge`. Відмовляє (`22023`), якщо `p_before IS NULL` або `p_before > now() - interval '3 years'`. `REVOKE ALL ... FROM PUBLIC`; EXECUTE лише `beautycrm_worker` (`grants.sql`, `setup-db.ps1`). Викликів з воркера ще немає (див. known-issues).
- GRANT для `beautycrm_audit_purge`: `USAGE` на schema `public`, `SELECT, DELETE` на `user_access_audit`.
- **Down (human gate):** `RAISE EXCEPTION` (відкат цілком), якщо є користувачі або запрошення `deputy`, запрошення з нестандартним знімком, права користувачів, що відрізняються від пресетів, або права owner/невідомі ключі. Інакше видаляє таблиці, функцію, тригер, колонки й CHECK-и, повертає `ck_users_role`/`ck_invites_role` до старого набору. Роль `beautycrm_audit_purge` не видаляється (кластерна).

### 15. `20261104182836_platform_control_plane` (TASK-816, ADR-018; за часом застосування стоїть між marketing і chat_booking_flow)

- `tenants`: `external_ref varchar(64) NULL`, `external_source varchar(16) NULL` ('shelfguard'), `suspended_at`, `suspended_reason varchar(500)`. Partial `UNIQUE (external_source, external_ref) WHERE external_ref IS NOT NULL` (`ux_tenants_external_ref`). CHECK: ref format `^[A-Za-z0-9_-]{1,64}$`, source in ('shelfguard'), ref and source set together, `suspended_*` only while `status = 'suspended'`. Policy `tenants_control_lookup` (see section 3).
- `users`: `password_hash` is NULL-able; new `state` ('active' default | 'invited'); CHECK `password_hash IS NOT NULL OR state = 'invited'`. Login for a user without a hash must be rejected (done in `AuthService`).
- `platform_owner_invites` (platform table, no tenant RLS): `token_hash bytea` (sha256, 32 bytes, UNIQUE), composite FK `(tenant_id, user_id)` -> users, partial unique "one active invite per user" (`ux_platform_owner_invites_active_user`).
- `platform_audit_log` (platform table, no tenant RLS, no FK on `tenant_id`): append-only by trigger (UPDATE/DELETE/TRUNCATE -> 42501) and by `REVOKE UPDATE, DELETE` for `beautycrm_app`. `details` must be a JSON object without secrets/tokens/passwords/message text.
- `control_plane_nonces` (platform table): PK `(key_id, nonce)`, index on `seen_at` for the worker cleanup (> 15 min).
- `beauty_platform_feedback` (tenant table, ENABLE + FORCE `tenant_isolation`): doubles as the delivery queue (`delivery_status`, `attempts`, `next_attempt_at`, `last_error`, `delivered_at`, `external_lead_id`). There is no generic outbox table in this schema.
- GRANTs (`deploy/staging/db/grants.sql` and `scripts/dev/setup-db.ps1`): app `REVOKE UPDATE, DELETE ON platform_audit_log`; worker `SELECT, UPDATE` on feedback, `SELECT, DELETE` on nonces, column-level `SELECT (id, tenant_id, email, full_name, role)` on users (no `password_hash`).
- Down is a human gate: it refuses (RAISE) while any user has `state = 'invited'` / NULL hash, and it drops the audit log, invites, nonces, feedback and the `external_ref` link.

---

### 17. `20261117140201_auth_global_identities` (TASK-862, ADR-024; написана вручну, Designer прибрано)
- Ролі `beautycrm_identity`, `beautycrm_identity_release` (створюються або падають з інструкцією для ops; `init-catalog-role.sql`).
- Таблиці `identities`, `auth_login_attempts`, `auth_login_tickets`, `auth_password_resets` (deny-all RLS); `users.identity_id` (NULL → backfill → NOT NULL, UNIQUE (tenant_id, identity_id), FK RESTRICT), `users.sessions_valid_after`; тригер пошти; CHECK `platform_audit_log` (дії `identity.*`, `actor_type = identity`).
- 20 функцій `auth_*` за матрицею §31.16; backfill за allowlist злиття (див. розділ «Global identity» вище).
- **Down (human gate):** `RAISE`, якщо є активні членства з `users.password_hash IS NULL`; інакше прибирає колонки, таблиці, функції, політики, гранти (роль не видаляє).
- **Застосування на staging/prod:** runbook §2b.

### 18. `20261118080034_auth_users_identity_id_immutable` (TASK-874, review R-3; Up написано вручну, Designer прибрано)
- Тригер `users_identity_id_immutable` (BEFORE UPDATE OF identity_id ON users) + функція `users_identity_id_immutable_guard()` (SECURITY INVOKER, без звернень до таблиць, `search_path` зафіксовано, REVOKE FROM PUBLIC): зміна `users.identity_id` на інше значення → `23514`. Роль runtime має UPDATE на users, а тригер пошти перевіряє лише пошту. Окрема функція, бо `users_identity_email_guard` належить `beautycrm_identity`.
- Down: прибирає тригер і функцію, даних не змінює. Нових прав і ролей немає.

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

### Public catalog role `beautycrm_catalog` (ADR-022, migration `20261109075625_beauty_public_location_catalog`)
- Functions (signatures as in the migration; output columns: `public_code, tenant_slug, location_id, name, address, city, timezone` plus `distance_km` for nearby):
  `beauty_public_resolve_location(p_code text)`, `beauty_public_nearby_locations(p_lat, p_lng, p_radius_km, p_limit)`,
  `beauty_public_search_locations_by_city(p_city text, p_limit integer)`.
- Radius is capped at 30 km inside the function; limit at 1..10. Changing the cap requires a new migration.
- `NOLOGIN NOSUPERUSER NOBYPASSRLS NOINHERIT`; owns the SECURITY DEFINER functions `beauty_public_resolve_location(text)`,
  `beauty_public_nearby_locations(double precision, double precision, double precision, integer)`,
  `beauty_public_search_locations_by_city(text, integer)`. Column-level SELECT on public columns of `beauty_locations` and `tenants`
  plus role-scoped (`TO beautycrm_catalog`) read policies `public_catalog_read`. `beautycrm_app` has EXECUTE only (no membership).
- The migration creates the role if missing (idempotent; needs `CREATEROLE`). Without `CREATEROLE` ops pre-provision it **before** migrating:
  ```sql
  CREATE ROLE beautycrm_catalog NOLOGIN NOSUPERUSER NOBYPASSRLS NOINHERIT;
  GRANT beautycrm_catalog TO <migrator_role> WITH INHERIT FALSE, SET TRUE;   -- SET: hand function ownership; INHERIT FALSE is mandatory
  ```
  Migration fails loudly if the role is LOGIN/SUPERUSER/BYPASSRLS. It grants EXECUTE to `beautycrm_app` only if that role exists;
  with a differently named runtime role run `GRANT EXECUTE ON FUNCTION ... TO <runtime_role>` for the three functions.
  Re-creating a function (Down/Up) resets its grants: repeat the GRANT.
- **Policy scope:** a policy `TO beautycrm_catalog` applies not only when `current_user = beautycrm_catalog` but to every role that
  inherits its privileges (member with `inherit_option`). Therefore all members (the migrator included) are `WITH INHERIT FALSE, SET TRUE`.
  The migration (re)grants the migrator's membership so; if a non-admin migrator cannot, it fails with instructions.
- `DbRoleGuard` also checks (when the role exists): catalog role NOLOGIN/not superuser/not BYPASSRLS/NOINHERIT, no memberships of its own,
  runtime role not a member, **no member with `inherit_option`**, exactly the three `beauty_public_*` functions (no extra/missing), each owned by
  the catalog role, SECURITY DEFINER, `proconfig` exactly `search_path=pg_catalog, pg_temp`, not executable by PUBLIC.
- **Rule for future definer functions (owned by `beautycrm_catalog`):** the existing policies `TO PUBLIC` (tenant isolation) also
  apply to the catalog role, and the role-scoped policies only add visibility. Every such function MUST check `is_active` /
  `status = 'active'` / `'beauty_booking' = ANY (modules)` explicitly in its WHERE; eligibility must never rely on a policy.
  New `beauty_public_*` functions also require updating the expected set in `DbRoleGuard`.
- `beauty_locations.public_code` is immutable: trigger `trg_beauty_locations_public_code_immutable` (BEFORE UPDATE OF public_code,
  `restrict_violation` 23001; no-op writes of the same value pass). Down drops the trigger and `beauty_forbid_public_code_change()`.
- Down drops functions/policies/privileges but does **not** drop the cluster-level role.

---

### Role `beautycrm_audit_purge` (ADR-023)
- `NOLOGIN NOSUPERUSER NOBYPASSRLS NOINHERIT`; власник `purge_user_access_audit`. Мігратор (`beautycrm_owner`) членство `WITH INHERIT FALSE, SET TRUE`.
- Створюється до міграції `20261115120000`: staging — `deploy/staging/db/init-catalog-role.sql` від superuser (ідемпотентний, також видає членство); локально — `scripts/dev/setup-db.ps1`.
- GRANT-и: `grants.sql` / `setup-db.ps1`: `REVOKE UPDATE, DELETE ON user_access_audit FROM beautycrm_app`; `REVOKE ALL ON FUNCTION public.purge_user_access_audit(timestamptz) FROM PUBLIC`; `GRANT EXECUTE ... TO beautycrm_worker`. `user_permissions` для app: повний набір DML. Воркер і control plane до `user_permissions` і `user_access_audit` доступу не мають.
- Тригер append-only, а не лише GRANT, захищає журнал (defense in depth).

### Roles `beautycrm_identity` і `beautycrm_identity_release` (ADR-024)

- Обидві: `NOLOGIN NOSUPERUSER NOBYPASSRLS NOINHERIT`, кластерні (створює ops, не застосунок). Мігратор має бути членом `WITH INHERIT FALSE, SET TRUE`.
- `beautycrm_identity` — власник `auth_*`-функцій (без `EXECUTE` для app до `GRANT`); `beautycrm_identity_release` — лише `auth_identity_release`.
- `beautycrm_app` і `beautycrm_worker`: `REVOKE ALL` на `identities`, `auth_login_attempts`, `auth_login_tickets`, `auth_password_resets`; `EXECUTE` на `auth_*` — явно за матрицею. Права на `auth_identity_release` немає в app і worker.
- `REVOKE CREATE ON SCHEMA public` для нових ролей (без `CREATE` на `public`, E-8).
- Тестова схема воркера (`worker/src/testing/schema.sql`) перегенерована; тести БД перевіряють deny-all і вихід функцій (`AuthGlobalIdentitiesMigrationTests`, `IdentityIsolationGuardTests`).

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

### Склад, етап 2a (`beauty_inventory`, ADR-025 §11, контракт §32.15–§32.25)

Міграція `beauty_inventory_stage2a` (TASK-883, адитивна; `Down` відмовляє, якщо є рух `is_shortage` або операція з `appointment_id`). FORCE RLS на всіх нових таблицях (`DO`-блок міграції падає без нього).

- **Норми:** `beauty_service_material_sets` (набір на мережу або заклад; `NULLS NOT DISTINCT` за закладом; порожній набір = «без списання в закладі») і `beauty_service_materials` (рядки: товар, `quantity`; лише товари `salon_use|both`).
- **`beauty_stock_levels`:** + `shortage_quantity numeric(14,3) NOT NULL DEFAULT 0` CHECK `>= 0`. `quantity >= 0` лишається.
- **`beauty_stock_movements`:** + `is_shortage boolean NOT NULL DEFAULT false`; CHECK `(batch_id IS NULL) = is_shortage` (замість попереднього `ck_beauty_stock_movements_batch`); `ck_beauty_stock_movements_shortage` (рядок нестачі — лише `service_consumption`, або додатна нога погашення `receipt|transfer_in|stocktake_adjust`).
- **`beauty_stock_operations`:** + `appointment_id uuid NULL` (композитний FK `(tenant_id, appointment_id)` → `beauty_appointments` RESTRICT); CHECK `(kind = 'service_consumption') = (appointment_id IS NOT NULL)`; UNIQUE `ux_beauty_stock_operations_appointment (tenant_id, appointment_id) WHERE appointment_id IS NOT NULL` — ідемпотентність автосписання за візитом.
- **`beauty_stocktake_lines`:** + `expected_shortage numeric(14,3) NULL` CHECK `>= 0`.
- **`beauty_staff_alerts`** (нова): `kind` `low_stock|expiring|shortage`; `location_id`, `product_id`, `batch_id NULL`, `operation_id NULL`; CHECK `(kind = 'expiring') = (batch_id IS NOT NULL)`, `(kind = 'shortage') = (operation_id IS NOT NULL)`; знімок `quantity`, `threshold`, `expires_on`, `days_bucket 0..365`; `dedupe_key varchar(200)`, UNIQUE `(tenant_id, dedupe_key)`; `status` `open|read|resolved` з CHECK на `resolved_at`/`read_at`; без ПД клієнтів.

**Інваріанти (для кожної пари заклад×товар, після кожної транзакції):**
1. `level.quantity = Σ batches.quantity = Σ movements.quantity WHERE NOT is_shortage` (≥ 0);
2. `level.shortage_quantity = −Σ movements.quantity WHERE is_shortage` (≥ 0);
3. `Σ movements.quantity = quantity − shortage_quantity` (сальдо, може бути < 0).

Журнали `beauty_stock_movements` і `beauty_stock_operations` — append-only (тригери + `REVOKE UPDATE, DELETE` для app-ролі). Погашення дефіциту — нові рухи, не `UPDATE`.

**GRANT (синхронно `deploy/staging/db/grants.sql` і `scripts/dev/setup-db.ps1`; тест `staging-grants-sync`):**
- app-роль: DML на нові таблиці через наявний `GRANT … ON ALL TABLES`; REVOKE append-only з етапу 1 без змін.
- роль воркера (`beautycrm_worker`): `SELECT` на `beauty_stock_levels`, `beauty_stock_batches`, `beauty_products`, `beauty_product_location_settings`, `beauty_locations`; `SELECT, INSERT, UPDATE` на `beauty_staff_alerts`; без `DELETE`. Звуження за звітом security (L4): `UPDATE (status, resolved_at, updated_at)`, колонковий `SELECT` на `beauty_products` без `purchase_price` — у роботі.

**Схема для тестів:** `worker/src/testing/schema.sql` перегенеровано після міграції (без BOM, LF); інакше падає `schema-drift.test.ts`.

**Тест-вимоги:** див. контракт §32.22; RLS крос-tenant для нових таблиць; Up → Down → Up на копії.

### Склад, етап 2b — Telegram власнику (`beauty_inventory`, ADR-025 §12, контракт §32.24)

Міграція `20261119142456_beauty_inventory_stage2b` (TASK-895, адитивна). Чотири нові таблиці: RLS ENABLE + FORCE + `tenant_isolation`; FK композитні `(tenant_id, …)`.

- **`beauty_staff_telegram_links`** — прив'язка й одноразовий токен; один рядок = одна спроба. Стани `issued | claimed | active | revoked`. `token_hash` — SHA-256 від 32 байт (`octet_length = 32`), TTL 15 хв. `chat_id_encrypted` — AES-GCM (ключ каналів), `chat_id_hash` — HMAC для пошуку. Згода: `consent_version` (`^inv-tg-v[0-9]+$`), `consent_at`. Часткові UNIQUE: `user_open` (один незакритий рядок на користувача), `chat_open` (один користувач на чат у боті tenant), `token`. CHECK форми стану (`issued`: є токен, немає чату; `claimed`: є токен і чат; `active`: токена немає, є чат; `revoked`: токена і чату немає). `revoked` не видаляється — стирається лише чат. Індекс для прибирання прострочених `issued|claimed`.
- **`beauty_staff_telegram_link_events`** — append-only журнал (`issued | claimed | activated | declined | revoked | rejected`; `actor = user|bot|system`). Без `chat_id`, хешу, токена, username. `REVOKE UPDATE, DELETE, TRUNCATE` для app- і worker-ролей + тригер. Фактично `declined` не пишеться: відмова — `revoked` з `reason = declined` (§32.24.13).
- **`beauty_staff_notifications`** — службова черга (outbox, ADR-003): `kind alert|digest`, `status pending|sending|sent|failed|cancelled`, `attempts 0..10`, `next_attempt_at`, `dedupe_key` (UNIQUE `(tenant_id, dedupe_key)`), `body` 1..4096. Без `chat_id` (адреса береться з прив'язки в момент відправки). Фінальні рядки старше 30 днів видаляє воркер.
- **`beauty_staff_notification_alerts`** — зв'язок адресат × алерт: PK `(tenant_id, notification_id, alert_id)`, UNIQUE `(tenant_id, link_id, alert_id)` — кожен алерт кожному адресату не більше одного разу. FK `notification` CASCADE, `alert` і `link` RESTRICT.

**Інваріанти:** `(chat_id_encrypted IS NULL) = (chat_id_hash IS NULL)`; `(revoke_reason IS NULL) = (status <> 'revoked')`; `(status = 'sent') = (sent_at IS NOT NULL)`; один активний чат на користувача, один користувач на чат у боті tenant. Відв'язка стирає `chat_id_*` і `token_hash` в тому самому `UPDATE`.

**GRANT (синхронно `scripts/dev/setup-db.ps1`, `deploy/staging/db/grants.sql`; тест `staging-grants-sync`):**
- app-роль: DML на (a), (c), (d) через `ON ALL TABLES`; на журнал (b) — `SELECT, INSERT`.
- роль воркера (`beautycrm_worker`): (a) колонковий `SELECT (id, tenant_id, user_id, channel_id, status, chat_id_encrypted, token_expires_at, activated_at)` і колонковий `UPDATE (status, token_hash, chat_id_encrypted, chat_id_hash, revoked_at, revoke_reason, last_delivery_at, updated_at)`; (b) `INSERT`; (c) `SELECT, INSERT, UPDATE, DELETE`; (d) `SELECT, INSERT`; `users` — колонковий `SELECT (id, tenant_id, role, is_active, state)`; `beauty_products` — `name, unit` (без `purchase_price`).

**Відкат:** `Down` відмовляє (`inventory stage2b history exists`), якщо є прив'язка не в `issued` або рядок журналу. Видалення історії — лише ручно власником БД з `ALTER TABLE … DISABLE TRIGGER` (human gate, runbook §2d). Схема тестів `worker/src/testing/schema.sql` перегенерована.

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
