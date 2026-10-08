# Beauty CRM — REST API Reference

**Current version:** 0.7 (TASK-697/698, 2026-10-08)

**Base path:** `/api`

**Error format:**
```json
{
  "code": "error_code",
  "message": "Human-readable message"
}
```

HTTP codes: `200`, `201`, `204`, `400`, `401`, `402`, `403`, `404`, `409`, `422`, `423`, `429`.

**Authentication:** JWT bearer token in `Authorization: Bearer <token>` header (except public endpoints noted below).

---

## Authentication (`/auth`)

### POST /auth/login
**Access:** Public (rate limited: 10/min per IP)

Create access & refresh tokens.

**Body:**
```json
{
  "tenant": "acme-salon",
  "email": "owner@example.com",
  "password": "..."
}
```

**Response:** `200`
```json
{
  "accessToken": "eyJ...",
  "expiresInSeconds": 900,
  "refreshToken": "opaque...",
  "user": { "id": "uuid", "email", "fullName", "role", "specialistId": "uuid|null" }
}
```

**Errors:** `401 invalid_credentials`, `423 account_locked`, `422 validation_failed`, `429 rate_limited`.

**Notes:** 5 failed attempts → 15 min lockout (same error for unknown email).

### POST /auth/refresh
**Access:** Public (rate limited: 120/min per IP, 5/min per token, 600/min per tenant)

Rotate refresh token. Old token becomes invalid; reuse closes all sessions.

**Body:**
```json
{ "refreshToken": "opaque..." }
```

**Response:** `200` TokenResponse (new pair).

### POST /auth/logout
**Access:** Public

Revoke refresh token.

**Body:**
```json
{ "refreshToken": "opaque..." }
```

**Response:** `204 No Content`.

### POST /auth/invites/accept
**Access:** Public (rate limited)

Accept invitation and set password (first login).

**Body:**
```json
{
  "token": "one-time-invite-token",
  "fullName": "John Doe",
  "password": "..."
}
```

**Response:** `201`
```json
{ "id": "uuid", "email", "fullName", "role", "specialistId": "uuid|null", "isActive": true }
```

**Errors:** `404 invite_invalid`, `409 user_exists|specialist_linked`, `422 weak_password`.

### GET /auth/me
**Access:** owner, admin, specialist

Get current user.

**Response:** `200` UserDto.

---

## Users & Invites

### GET /users
**Access:** owner, admin

List users in tenant.

**Response:** `200` `UserDto[]`.

### PATCH /users/{id}/status
**Access:** owner, admin

Enable/disable user (invalidates refresh tokens if disabled).

**Body:**
```json
{ "isActive": true|false }
```

**Response:** `200` UserDto.

**Errors:** `403 forbidden_role` (owner can't disable themselves; admin can't modify equal/higher roles).

### POST /invites
**Access:** owner, admin

Create invitation (one-time token, shown once, new invite revokes previous pending for same specialist_id).

**Body:**
```json
{
  "email": "new@example.com",
  "role": "admin|specialist",
  "specialistId": "uuid (required for specialist)"
}
```

**Response:** `201`
```json
{
  "invite": { "id": "uuid", "email", "role", "specialistId": "uuid|null", "createdAt" },
  "token": "one-time-token (show to user out-of-band)"
}
```

**Headers:** `Cache-Control: no-store`.

**Errors:** `409 specialist_inactive|specialist_linked`, `422 validation_failed`.

### GET /invites
**Access:** owner, admin

List pending invitations.

**Response:** `200` `InviteDto[]`.

### DELETE /invites/{id}
**Access:** owner, admin

Revoke invitation.

**Response:** `204 No Content`.

---

## Platform API (Operator Only, X-Platform-Key header)

### POST /platform/tenants
**Access:** X-Platform-Key header only (rate limited: 10/min)

Create tenant + owner user.

**Body:**
```json
{
  "name": "Acme Salon",
  "slug": "acme-salon",
  "modules": ["beauty_booking", "beauty_catalog", "beauty_clients"],
  "owner": {
    "email": "owner@acme.com",
    "fullName": "Jane Owner",
    "password": "..."
  }
}
```

**Response:** `201`
```json
{
  "tenantId": "uuid",
  "name": "Acme Salon",
  "slug": "acme-salon",
  "modules": ["..."],
  "ownerUserId": "uuid"
}
```

**Errors:** `409 tenant_slug_taken`, `422 validation_failed`.

### PATCH /platform/tenants/{id}
**Access:** X-Platform-Key header only

Update tenant modules/status.

**Body:**
```json
{
  "modules": ["beauty_booking", "..."],
  "status": "active|suspended"
}
```

**Response:** `204 No Content`.

---

## Locations (`/beauty/locations`, requires `beauty_booking` module)

### GET /beauty/locations
**Access:** owner, admin, specialist (read; admin/owner see `includeInactive` param)

List locations.

**Query:**
- `includeInactive` (optional, default: false): bool; specialist always sees only active

**Response:** `200`
```json
[
  {
    "id": "uuid",
    "name": "Kyiv Main",
    "address": "123 Main St",
    "phone": "+380501234567",
    "timezone": "Europe/Kyiv",
    "isActive": true
  }
]
```

### POST /beauty/locations
**Access:** owner, admin

Create location.

**Body:**
```json
{
  "name": "Kyiv Main",
  "address": "123 Main St",
  "phone": "+380501234567",
  "timezone": "Europe/Kyiv"
}
```

**Response:** `201` LocationDto.

**Errors:** `409 location_name_taken`, `422 invalid_name|invalid_address|invalid_phone|invalid_timezone`.

### PUT /beauty/locations/{id}
**Access:** owner, admin

Update location.

**Body:** Same as POST (all fields optional except name).

**Response:** `200` LocationDto.

**Errors:** `404 location_not_found`, `409 has_future_appointments|timezone_locked`, `422 invalid_*`.

---

## Staff Management (`/beauty/specialists`, `/beauty/absences`, requires `beauty_booking` module)

### GET /beauty/specialists
**Access:** owner, admin, specialist (read; specialist sees all as reference without phone)

List specialists.

**Response:** `200`
```json
[
  {
    "id": "uuid",
    "name": "John Doe",
    "phone": "+380501234567 (admin/owner only)",
    "position": "Hairdresser",
    "photoUrl": "https://...",
    "isActive": true,
    "hasAccount": true,
    "services": [{ "id": "uuid", "name": "Haircut" }],
    "locations": [
      {
        "locationId": "uuid",
        "locationName": "Kyiv Main",
        "isActive": true,
        "workingHours": { "mon": [{"from":"09:00","to":"18:00"}], ... } | null
      }
    ]
  }
]
```

### POST /beauty/specialists
**Access:** owner, admin

Create specialist profile.

**Body:**
```json
{
  "name": "Jane Smith",
  "phone": "+380501234567",
  "position": "Colorist",
  "locationIds": ["loc-id1", "loc-id2"],
  "serviceIds": ["svc-id1"],
  "workingHours": { "mon": [{"from":"09:00","to":"18:00"}], ... }
}
```

**Response:** `201` SpecialistDto.

**Errors:** `409 location_already_assigned`, `422 invalid_name|invalid_phone|invalid_position|invalid_working_hours|location_required|location_not_found|service_not_found`.

### GET /beauty/specialists/{id}
**Access:** owner, admin, specialist

Get specialist details.

**Response:** `200` SpecialistDto.

### PUT /beauty/specialists/{id}
**Access:** owner, admin (not for other role's profiles)

Update specialist profile.

**Body:**
```json
{
  "name": "Jane Smith",
  "phone": "+380501234567",
  "position": "Colorist",
  "isActive": false
}
```

**Response:** `200` SpecialistDto.

**Errors:** `403 forbidden_role` (admin can't edit owner's profile), `404 specialist_not_found`, `422 invalid_*`.

**Notes:** `isActive=false` deactivates in one transaction (user disabled, tokens revoked, pending invites revoked).

### PUT /beauty/specialists/{id}/services
**Access:** owner, admin

Set services assigned to specialist (full replacement).

**Body:**
```json
{ "serviceIds": ["svc-id1", "svc-id2"] }
```

**Response:** `200` SpecialistDto.

### PUT /beauty/specialists/{id}/schedule
**Access:** owner, admin

Set working hours for location.

**Body:**
```json
{
  "locationId": "uuid",
  "workingHours": { "mon": [{"from":"09:00","to":"18:00"}], ... }
}
```

**Response:** `200` SpecialistDto.

**Errors:** `404 location_not_found|specialist_not_found`, `409 location_already_assigned`, `422 invalid_working_hours`.

### POST /beauty/specialists/{id}/locations
**Access:** owner, admin

Add location to specialist (with optional schedule).

**Body:**
```json
{
  "locationId": "uuid",
  "workingHours": { ... } (optional)
}
```

**Response:** `200` SpecialistDto.

**Errors:** `409 location_already_assigned`, `404 location_not_found|specialist_not_found`, `422 invalid_working_hours`.

### DELETE /beauty/specialists/{id}/locations/{locationId}
**Access:** owner, admin

Remove location from specialist (schedule preserved).

**Response:** `200` SpecialistDto.

**Errors:** `404 location_not_assigned|specialist_not_found`, `409 has_future_appointments`.

### POST /beauty/specialists/{id}/invite
**Access:** owner, admin

Send invitation to specialist (revokes previous pending).

**Body:**
```json
{ "email": "specialist@example.com" }
```

**Response:** `201`
```json
{
  "invite": { ... },
  "token": "one-time-token"
}
```

**Headers:** `Cache-Control: no-store`.

**Errors:** `409 specialist_inactive`, `422 invalid_email`.

---

### GET /beauty/absences
**Access:** owner, admin, specialist

List absences.

**Query:**
- `from` (optional, default: today UTC): ISO date
- `to` (optional, default: from + 31 days): ISO date
- `specialistId` (optional): UUID

**Response:** `200`
```json
[
  {
    "id": "uuid",
    "specialistId": "uuid",
    "type": "sick|vacation|day_off|other",
    "dateFrom": "2026-10-10",
    "dateTo": "2026-10-12",
    "status": "requested|approved|rejected|cancelled",
    "note": "Approved by admin (admin/owner and author only)",
    "conflicts": [
      { "appointmentId": "uuid", "startsAt": "2026-10-10T09:00:00Z", "serviceName": "Haircut" }
    ] (for admin on create/approve)
  }
]
```

**Errors:** `422 invalid_range`.

### POST /beauty/specialists/{id}/absences
**Access:** owner/admin (creates as `approved`); specialist (creates as `requested`, self only)

Create absence (may have conflicts with future appointments).

**Body:**
```json
{
  "type": "sick|vacation|day_off|other",
  "dateFrom": "2026-10-10",
  "dateTo": "2026-10-12",
  "note": "Dental appointment (optional, <= 500 chars)"
}
```

**Response:** `201` AbsenceDto (+ conflicts).

**Errors:** `403 forbidden_role|specialist_inactive`, `409 absence_overlap`, `422 too_many_requests|invalid_type|invalid_dates|invalid_note`, `429 rate_limited` (20/min per user).

**Notes:** Specialist can have max 10 active `requested` absences; owner/admin `approved` not counted.

### POST /beauty/absences/{id}/approve
**Access:** owner, admin

Approve pending absence (returns conflicts).

**Response:** `200` AbsenceDto (+ conflicts).

**Errors:** `404 absence_not_found`, `409 absence_not_pending`.

### POST /beauty/absences/{id}/reject
**Access:** owner, admin

Reject pending absence.

**Response:** `200` AbsenceDto.

**Errors:** `404 absence_not_found`, `409 absence_not_pending`.

### POST /beauty/absences/{id}/cancel
**Access:** owner, admin, or author if still `requested`

Cancel absence.

**Response:** `200` AbsenceDto.

**Errors:** `404 absence_not_found`, `409 absence_closed` (already cancelled), `403 forbidden` (specialist can't cancel approved/rejected).

---

## Booking (`/beauty/slots`, `/beauty/appointments`, requires `beauty_booking` module)

### GET /beauty/slots
**Access:** owner, admin, specialist

Get free slots for a date.

**Query:**
- `locationId` (required): UUID
- `serviceId` (required): UUID
- `date` (required): ISO date (local to location)
- `specialistId` (optional): UUID; if omitted, returns slots from all specialists

**Response:** `200`
```json
[
  {
    "specialistId": "uuid",
    "startsAt": "2026-10-10T09:00:00Z",
    "endsAt": "2026-10-10T09:30:00Z",
    "label": "John Doe, 09:00–09:30",
    "timezone": "Europe/Kyiv",
    "cancellation": {
      "windowHours": 12,
      "refundPercentInWindow": 50,
      "refundPercentOutside": 100,
      "deductFee": false,
      "feePercent": 0
    }
  }
]
```

**Errors:** `401`, `403`, `404`, `422`.

### GET /beauty/appointments
**Access:** owner, admin, specialist

List appointments (calendar view).

**Query:**
- `from` (optional, default: today UTC): ISO timestamp
- `to` (optional, default: from + 7 days): ISO timestamp
- `locationId` (optional): UUID
- `specialistId` (optional): UUID; specialist always filters to self
- `includeCancelled` (optional, default: false): bool; skip cancelled by default

**Response:** `200` `AppointmentDto[]`.

**Errors:** `422 invalid_range|range_too_large` (> 62 days).

### GET /beauty/appointments/{id}
**Access:** owner, admin, specialist (specialist: self only)

Get appointment details.

**Response:** `200` AppointmentDto.

**Errors:** `404 appointment_not_found`.

### POST /beauty/appointments
**Access:** owner, admin, specialist (can only create for self)

Create appointment.

**Body:**
```json
{
  "locationId": "uuid",
  "specialistId": "uuid",
  "serviceId": "uuid",
  "startsAt": "2026-10-10T09:00:00Z",
  "client": {
    "id": "uuid (optional)",
    "name": "John Client",
    "phone": "+380501234567",
    "email": "client@example.com (optional)"
  },
  "reminder": "none|1h|2h",
  "paymentMethod": "card|cash",
  "source": "admin|online (default: online)"
}
```

**Response:** `201`
```json
{
  "id": "uuid",
  "locationId": "uuid",
  "locationName": "Kyiv Main",
  "specialistId": "uuid",
  "specialistName": "John Doe",
  "serviceId": "uuid",
  "serviceName": "Haircut",
  "clientId": "uuid",
  "startsAt": "2026-10-10T09:00:00Z",
  "endsAt": "2026-10-10T09:30:00Z",
  "durationMinutes": 30,
  "timezone": "Europe/Kyiv",
  "status": "pending|confirmed",
  "source": "online",
  "priceOriginal": 500.00,
  "priceFinal": 450.00,
  "promotionId": "uuid|null",
  "reminderOption": "1h|none",
  "paymentMethod": "card|cash",
  "cancellation": { ... },
  "cancelledAt": null,
  "cancelledBy": null,
  "cancelReason": null
}
```

**Errors:**
- `409 slot_unavailable|specialist_unavailable` (overlap, day off, service not assigned)
- `422 validation_failed|specialist_not_at_location` (bad input, specialist not at location, price not set, inactive specialist)
- `402 payment_failed` (card charge failed)

### PATCH /beauty/appointments/{id}
**Access:** owner, admin, specialist (specialist: self only)

Move or change status.

**Body:**
```json
{
  "startsAt": "2026-10-10T10:00:00Z (optional)",
  "status": "confirmed|completed|no_show (optional)"
}
```

**Response:** `200` AppointmentDto.

**Errors:** `409 slot_unavailable|specialist_unavailable`, `422 validation_failed`, `404 appointment_not_found`.

### POST /beauty/appointments/{id}/cancel
**Access:** owner, admin, specialist (specialist: self only)

Cancel appointment. Refund amount calculated per cancellation policy (TASK-685).

**Body:** `{ "reason": "..." (optional, <= 300) }` (can be empty)

**Response:** `200`
```json
{
  "appointment": { AppointmentDto },
  "refundAmount": 225.00,
  "refundPercent": 50,
  "feePercent": 0
}
```

**Errors:** `404 appointment_not_found`, `409 already_cancelled`, `422 invalid_reason`.

**Notes:** Cancelled appointment has `cancelledBy: {type: "staff", name: "..."}` and `cancelReason`. Specialist doesn't see `name` or `reason`.

---

## Catalog (`/beauty/services`, `/beauty/promotions`, requires `beauty_catalog` module)

### GET /beauty/services
**Access:** owner, admin, specialist (read only)

List services.

**Query:**
- `includeInactive` (optional, default: false): bool

**Response:** `200`
```json
[
  { "id": "uuid", "name": "Haircut", "description", "category", "durationMinutes": 30, "isActive": true }
]
```

### POST /beauty/services
**Access:** owner, admin

Create service.

**Body:**
```json
{
  "name": "Color Correction",
  "description": "...",
  "category": "coloring",
  "durationMinutes": 90
}
```

**Response:** `201` ServiceDto.

### PUT /beauty/services/{id}
**Access:** owner, admin

Update service.

**Body:** Same as POST.

**Response:** `200` ServiceDto.

### GET /beauty/services/{id}/prices
**Access:** owner, admin, specialist (read only)

Get prices (network-wide + per-location overrides).

**Response:** `200`
```json
[
  {
    "serviceId": "uuid",
    "locationId": null,
    "price": 500.00,
    "label": "Network price"
  },
  {
    "serviceId": "uuid",
    "locationId": "loc-id",
    "price": 550.00,
    "label": "Override for Kyiv location"
  }
]
```

### PUT /beauty/services/{id}/prices
**Access:** owner, admin

Set prices.

**Body:**
```json
[
  { "locationId": null, "price": 500.00 },
  { "locationId": "uuid", "price": 550.00 }
]
```

**Response:** `200` (list).

### GET /beauty/promotions
**Access:** owner, admin, specialist (read only)

List promotions.

**Response:** `200`
```json
[
  {
    "id": "uuid",
    "name": "Summer Sale",
    "description": "...",
    "discountType": "percent|fixed",
    "discountValue": 20,
    "startsAt": "2026-10-01T00:00:00Z|null",
    "endsAt": "2026-12-31T23:59:59Z|null",
    "isActive": true,
    "locationIds": ["uuid1", "uuid2"] (empty = all),
    "serviceIds": ["uuid3"] (empty = all)
  }
]
```

### POST /beauty/promotions
**Access:** owner, admin

Create promotion.

**Body:** Same as GET response fields (without id, createdAt).

**Response:** `201` PromotionDto.

### PUT /beauty/promotions/{id}
**Access:** owner, admin

Update promotion.

**Body:** Same as POST.

**Response:** `200` PromotionDto.

### GET /beauty/promotions/preview
**Access:** owner, admin, specialist

Preview price with applicable promotion.

**Query:**
- `locationId` (required): UUID
- `promotionId` (optional): UUID
- `at` (optional, default: now): ISO timestamp

**Response:** `200`
```json
{
  "serviceId": "uuid",
  "originalPrice": 500.00,
  "discountPercent": 20,
  "finalPrice": 400.00,
  "appliedPromotionId": "uuid"
}
```

---

## Clients (`/beauty/clients`, requires `beauty_clients` module)

### GET /beauty/clients
**Access:** owner, admin

Search clients.

**Query:**
- `search` (optional): phone/email/name substring
- `page` (optional, default: 1): int
- `pageSize` (optional, default: 50): int

**Response:** `200`
```json
{
  "items": [
    {
      "id": "uuid",
      "fullName": "John Client",
      "phone": "+380501234567",
      "email": "john@example.com",
      "birthDate": "1990-01-15",
      "marketingConsent": true,
      "unsubscribed": false,
      "cancelledCount": 2,
      "cancelledByClientCount": 1,
      "notes": [ { "id": "uuid", "body": "...", "createdAt", "authorUserId" } ],
      "appointments": [
        {
          "id": "uuid",
          "startsAt": "2026-09-15T14:30:00Z",
          "status": "completed|cancelled",
          "serviceName": "Haircut",
          "cancelledAt": "2026-09-16T10:00:00Z|null",
          "cancelledBy": { "type": "client|staff|system", "name": "..." },
          "cancelReason": "..."
        }
      ],
      "visitCount": 5,
      "lastVisit": "2026-09-15T14:30:00Z"
    }
  ],
  "total": 100
}
```

### GET /beauty/clients/{id}
**Access:** owner, admin

Get client details.

**Response:** `200` ClientDto (same as above).

**Errors:** `404 client_not_found`.

### POST /beauty/clients/{id}/notes
**Access:** owner, admin

Add note to client.

**Body:**
```json
{ "body": "Prefer evening appointments" }
```

**Response:** `201`
```json
{ "id": "uuid", "body": "...", "createdAt": "2026-10-07T...", "authorUserId": "uuid" }
```

---

## Analytics (`/beauty/analytics`, requires `beauty_analytics` module)

### GET /beauty/analytics/network
**Access:** owner, admin

Network-wide metrics.

**Query:**
- `from` (optional, default: 30 days ago): ISO date
- `to` (optional, default: today): ISO date

**Response:** `200`
```json
{
  "appointmentCount": 150,
  "completedCount": 145,
  "cancelledCount": 3,
  "noShowCount": 2,
  "revenue": 75000.00,
  "averageRating": 4.8
}
```

### GET /beauty/analytics/locations
**Access:** owner, admin

Per-location breakdown.

**Query:** Same as /network.

**Response:** `200`
```json
[
  {
    "locationId": "uuid",
    "name": "Kyiv Main",
    "appointmentCount": 100,
    "revenue": 50000.00
  }
]
```

### GET /beauty/analytics/promotions
**Access:** owner, admin

Promotion effectiveness.

**Query:** Same as /network.

**Response:** `200`
```json
[
  {
    "promotionId": "uuid",
    "name": "Summer Sale",
    "usageCount": 45,
    "totalDiscount": 9000.00,
    "revenue": 18000.00
  }
]
```

---

## Overview (`/beauty/overview`, requires `beauty_booking` module)

### GET /beauty/overview
**Access:** owner, admin

Daily overview + KPI (appointments, revenue, new clients, free slots).

**Query:**
- `date` (optional, default: today at reference location's timezone): ISO date
- `locationId` (optional, default: all active): UUID

**Response:** `200`
```json
{
  "date": "2026-10-08",
  "timezone": "Europe/Kyiv",
  "appointments": [ AppointmentDto[] ],
  "kpi": {
    "appointmentsCount": 8,
    "revenue": 4500.00,
    "newClients": 2,
    "freeSlots": 12
  }
}
```

**Errors:** `404 location_not_found`.

---

## Channels (`/beauty/channels`, `/beauty/webhooks`, requires `beauty_channels` module)

### GET /beauty/channels
**Access:** owner, admin

List channels.

**Response:** `200`
```json
[
  {
    "id": "uuid",
    "type": "telegram|instagram",
    "name": "Telegram Support",
    "locationId": "uuid|null",
    "isActive": true,
    "maskedToken": "********abcd",
    "hasWebhookSecret": true,
    "hasAppSecret": true,
    "hasVerifyToken": true,
    "webhookPath": "/api/beauty/webhooks/telegram/uuid"
  }
]
```

### PUT /beauty/channels/{id}
**Access:** owner, admin

Update channel.

**Body:**
```json
{
  "type": "telegram|instagram",
  "name": "Bot Name",
  "locationId": "uuid|null",
  "isActive": true,
  "token": "123:ABC...",
  "webhookSecret": "secret-key",
  "appSecret": "app-secret (Instagram HMAC key)",
  "verifyToken": "verify-token (Instagram GET handshake)",
  "settingsJson": { "custom": "value" }
}
```

**Response:** `200` ChannelDto.

**Errors:** `404 channel_not_found`, `409 channel_id_conflict`.

### POST /beauty/webhooks/{channel}/{channelId}
**Access:** Public (signature verification required)

Inbound webhook from external channel (Telegram, Instagram).

**Headers:** `X-Telegram-Bot-Api-Secret-Token` or `X-Hub-Signature-256` (depending on channel).

**Body:** Channel-specific format (JSON).

**Response:** `200 OK` or `204 No Content`.

**Errors:** `401 signature_invalid`, `404 channel_not_found`, `400 parse_error`.

**GET /beauty/webhooks/instagram/{channelId}**
(Same path, GET for Meta hub.challenge handshake)

---

## AI Actions (`/beauty/ai/actions`, requires `beauty_ai` module)

### GET /beauty/ai/actions
**Access:** owner, admin

List AI actions (proposed, approved, rejected).

**Query:**
- `status` (optional): "proposed|approved|rejected|executed"
- `from` (optional): ISO timestamp

**Response:** `200`
```json
[
  {
    "id": "uuid",
    "conversationId": "uuid|null",
    "appointmentId": "uuid|null",
    "toolName": "create_appointment",
    "payload": { "locationId": "...", ... },
    "status": "proposed|approved|rejected|executed",
    "result": { "appointmentId": "..." } | null,
    "error": "...|null",
    "createdAt": "2026-10-07T...",
    "confirmedAt": "2026-10-07T...|null",
    "confirmedByUserId": "uuid|null"
  }
]
```

### POST /beauty/ai/actions/{id}/approve
**Access:** owner, admin

Approve & execute proposed action.

**Body:** `{}` (empty)

**Response:** `200` AiActionDto (updated, status=executed).

**Errors:** `404 action_not_found`, `409 action_not_pending`.

### POST /beauty/ai/actions/{id}/reject
**Access:** owner, admin

Reject proposed action.

**Body:** `{}` (empty)

**Response:** `200` AiActionDto (updated, status=rejected).

### POST /beauty/ai/actions/{id}/revert
**Access:** owner, admin

Revert executed action (undo appointment creation, etc.).

**Body:** `{}` (empty)

**Response:** `200` AiActionDto.

---

## Cancellation Settings (`/beauty/settings/cancellation`, requires `beauty_booking` module)

### GET /beauty/settings/cancellation
**Access:** owner, admin, specialist

Get tenant's cancellation policy.

**Response:** `200`
```json
{
  "windowHours": 12,
  "refundPercentInWindow": 50,
  "refundPercentOutside": 100,
  "deductFee": false,
  "feePercent": 0
}
```

### PUT /beauty/settings/cancellation
**Access:** owner only

Update cancellation policy (all 5 fields required).

**Body:** Same as GET response.

**Response:** `200` same.

**Errors:** `422 invalid_window_hours|invalid_refund_percent|invalid_fee_percent|settings_incomplete`, `403 forbidden_role`.

---

## Public Booking API (`/api/public/{tenantSlug}`, no JWT, §12)

**Tenant resolution:** Slug → tenant via separate connection, transaction-scoped `app.tenant_slug` setting, narrow RLS policy.

### GET /public/{tenantSlug}/locations
**Access:** Public

List active locations.

**Response:** `200`
```json
[
  {
    "id": "uuid",
    "name": "Kyiv Main",
    "address": "123 Main St",
    "phone": "+380501234567",
    "timezone": "Europe/Kyiv"
  }
]
```

### GET /public/{tenantSlug}/locations/{locationId}/specialists
**Access:** Public

List specialists at location (with optional service filter).

**Query:**
- `serviceId` (optional): UUID; show only specialists assigned to service

**Response:** `200`
```json
[
  {
    "id": "uuid",
    "name": "John Doe",
    "title": "Hairdresser",
    "photoUrl": "https://..."
  }
]
```

### GET /public/{tenantSlug}/locations/{locationId}/services
**Access:** Public

List services at location (with optional specialist filter).

**Query:**
- `specialistId` (optional): UUID; show only services assigned to specialist

**Response:** `200`
```json
[
  {
    "id": "uuid",
    "name": "Haircut",
    "description": "...",
    "category": "cutting",
    "durationMinutes": 30,
    "priceOriginal": 500.00,
    "priceFinal": 450.00,
    "promotionId": "uuid|null",
    "promotionName": "Summer Sale"
  }
]
```

### GET /public/{tenantSlug}/slots
**Access:** Public (rate limited: 120/min per IP)

Get free slots.

**Query:**
- `locationId` (required): UUID
- `serviceId` (required): UUID
- `date` (required): ISO date; must be between yesterday and 180 days ahead
- `specialistId` (optional): UUID

**Response:** `200`
```json
[
  {
    "specialistId": "uuid",
    "startsAt": "2026-10-10T09:00:00Z",
    "endsAt": "2026-10-10T09:30:00Z",
    "label": "John Doe, 09:00–09:30",
    "cancellation": { ... }
  }
]
```

**Errors:** `404`, `422 invalid_date`.

### POST /public/{tenantSlug}/appointments
**Access:** Public (rate limited: 10/min per IP)

Create appointment.

**Headers:**
- `Idempotency-Key` (required): 16-128 chars [A-Za-z0-9._:-]; same key + body = same result
- `X-Captcha-Token` (optional): CAPTCHA token (if enabled)

**Body:**
```json
{
  "locationId": "uuid",
  "specialistId": "uuid",
  "serviceId": "uuid",
  "startsAt": "2026-10-10T09:00:00Z",
  "client": {
    "name": "John Client",
    "phone": "+380501234567|0501234567|CC1234567890"
  },
  "reminder": "none|1h|2h (default: none)",
  "paymentMethod": "card|cash",
  "captchaToken": "... (alternate header location)"
}
```

**Response:** `201`
```json
{
  "publicToken": "base64url(HMAC-SHA256(...)), 43 chars, 256 bits",
  "appointment": {
    "locationId": "uuid",
    "locationName": "Kyiv Main",
    "specialistId": "uuid",
    "specialistName": "John Doe",
    "serviceId": "uuid",
    "serviceName": "Haircut",
    "startsAt": "2026-10-10T09:00:00Z",
    "endsAt": "2026-10-10T09:30:00Z",
    "durationMinutes": 30,
    "status": "pending|confirmed",
    "priceOriginal": 500.00,
    "priceFinal": 450.00,
    "promotionId": "uuid|null",
    "reminderOption": "1h|none",
    "paymentMethod": "card|cash",
    "paymentStatus": "pending|paid|failed",
    "cancellation": { ... }
  }
}
```

**Headers:** `Cache-Control: no-store`, `Referrer-Policy: no-referrer`.

**Response (replay):** `200 + Idempotent-Replayed: true` (same publicToken + appointment).

**Errors:**
- `422 idempotency_key_required|invalid_idempotency_key|idempotency_key_reused` (key validation)
- `422 invalid_name|invalid_phone|invalid_reminder|invalid_payment_method|invalid_request` (data validation)
- `422 invalid_start|slot_in_past|outside_working_hours` (slot validation)
- `422 booking_limit_reached` (>3 active per phone or >5 created/hour per phone)
- `422 captcha_failed` (if enabled)
- `409 slot_unavailable` (overlap)
- `402 payment_failed` (card charge)
- `404` (unknown slug, module disabled, tenant suspended)

**Notes:**
- Phone normalized to E.164 (`+380...`); accepts `+CC...`, `00CC...`, `0XXXXXXXXX` (→ +380), `CC...` (11-15 digits).
- Client deduplicated by normalized phone; existing name NOT overwritten.
- `marketingConsent` only explicit `true` for new clients; anonymous existing profile not changed.
- PII not logged; slug/token in path not logged by Kestrel.

### GET /public/{tenantSlug}/appointments/{publicToken}
**Access:** Public (rate limited: 30/min per IP)

Get appointment by public token.

**Response:** `200`
```json
{
  "locationId": "uuid",
  "locationName": "Kyiv Main",
  "specialistId": "uuid",
  "specialistName": "John Doe",
  "serviceId": "uuid",
  "serviceName": "Haircut",
  "startsAt": "2026-10-10T09:00:00Z",
  "endsAt": "2026-10-10T09:30:00Z",
  "durationMinutes": 30,
  "status": "pending|confirmed|completed|cancelled|no_show",
  "priceOriginal": 500.00,
  "priceFinal": 450.00,
  "promotionId": "uuid|null",
  "reminderOption": "1h|none",
  "paymentMethod": "card|cash",
  "paymentStatus": "pending|paid|failed",
  "cancellation": { ... }
}
```

**Headers:** `Cache-Control: no-store`, `Referrer-Policy: no-referrer`.

**Errors:** `404 not_found` (unknown/invalid token, same for all).

### POST /public/{tenantSlug}/appointments/{publicToken}/cancel
**Access:** Public (rate limited: 10/min per IP)

Cancel appointment (refund per policy).

**Body:** `{ "reason": "..." (optional, <= 300) }` (can be empty)

**Response:** `200`
```json
{
  "appointment": { PublicAppointmentDto },
  "refundAmount": 225.00,
  "refundPercent": 50,
  "feePercent": 0
}
```

**Headers:** `Cache-Control: no-store`, `Referrer-Policy: no-referrer`.

**Errors:** `404 not_found`, `409 already_cancelled`, `422 invalid_reason`.

**Notes:** No auth info disclosed; public API always shows `cancelled_by: null`.

---

## Error Codes Reference

| Code | HTTP | Meaning |
|------|------|---------|
| `invalid_credentials` | 401 | Email/password mismatch |
| `invalid_token` | 401 | Malformed/expired JWT |
| `tenant_required` | 401 | No tenant_id claim |
| `module_disabled` | 403 | Feature not enabled for tenant |
| `forbidden_role` | 403 | Insufficient permissions |
| `specialist_inactive` | 403 | Specialist profile deactivated |
| `account_locked` | 423 | Too many failed attempts (15 min) |
| `rate_limited` | 429 | Too many requests |
| `appointment_not_found` | 404 | Appointment not found |
| `client_not_found` | 404 | Client not found |
| `location_not_found` | 404 | Location not found |
| `specialist_not_found` | 404 | Specialist not found |
| `absence_not_found` | 404 | Absence not found |
| `specialist_not_at_location` | 422 | Specialist not assigned to location |
| `slot_unavailable` | 409 | Overlap or specialist fully booked |
| `specialist_unavailable` | 409 | Specialist has approved absence or service not assigned (staff API only) |
| `already_cancelled` | 409 | Appointment already cancelled |
| `absence_overlap` | 409 | Absence overlaps with existing |
| `absence_closed` | 409 | Absence already cancelled |
| `absence_not_pending` | 409 | Absence not in requested state |
| `invite_invalid` | 404 | Token expired or invalid |
| `user_exists` | 409 | Email already registered in tenant |
| `specialist_linked` | 409 | User already linked to another specialist |
| `specialist_inactive` | 409 | Specialist profile deactivated (can't invite) |
| `location_name_taken` | 409 | Location name already used (case-insensitive) |
| `location_already_assigned` | 409 | Specialist already assigned to location |
| `has_future_appointments` | 409 | Location/specialist has future pending/confirmed appointments |
| `timezone_locked` | 409 | Can't change timezone with future appointments |
| `tenant_slug_taken` | 409 | Slug already in use |
| `validation_failed` | 422 | Malformed request body |
| `payment_failed` | 402 | Card payment declined |
| `refund_failed` | 402 | Refund service error |
| `too_many_requests` | 422 | Specialist has 10+ active requested absences |
| `invalid_working_hours` | 422 | Malformed schedule JSON |
| `invalid_timezone` | 422 | Not a valid IANA timezone |

---

## Status Codes Summary

- `200 OK` — Success, body present.
- `201 Created` — Resource created.
- `204 No Content` — Success, no body.
- `400 Bad Request` — Malformed webhook.
- `401 Unauthorized` — Missing/invalid token.
- `402 Payment Required` — Payment failure.
- `403 Forbidden` — Role/module insufficient.
- `404 Not Found` — Resource not found.
- `409 Conflict` — Slot occupied, email exists, etc.
- `422 Unprocessable Entity` — Validation failed.
- `423 Locked` — Account locked.
- `429 Too Many Requests` — Rate limited.
- `500 Internal Server Error` — Infrastructure failure.
