# Beauty CRM — REST API Reference

**Current version:** 0.2 (TASK-675, 2026-10-07)

**Base path:** `/api`

**Error format:**
```json
{
  "code": "error_code",
  "message": "Human-readable message"
}
```

HTTP codes: `200`, `201`, `204`, `401`, `403`, `404`, `409`, `422`, `423`, `429`.

**Authentication:** JWT bearer token in `Authorization: Bearer <token>` header (except login/refresh/accept/webhooks, see below).

---

## Authentication (`/auth`)

### POST /auth/login
**Access:** Public (rate limited)

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

### POST /auth/refresh
**Access:** Public (rate limited)

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

**Errors:** `404 invite_invalid`, `409 user_exists`, `422 weak_password`.

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

**Errors:** `403 forbidden` (owner can't disable themselves; admin can't modify equal/higher roles).

### POST /invites
**Access:** owner, admin

Create invitation (one-time token, shown once).

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
  "invite": { "id": "uuid", "email", "role", "createdAt" },
  "token": "one-time-token (show to user out-of-band)"
}
```

**Errors:** `409 email_exists`, `422 validation_failed`.

### GET /invites
**Access:** owner, admin

List pending invitations.

**Response:** `200` `InviteDto[]`.

### DELETE /invites/{id}
**Access:** owner, admin

Revoke invitation.

**Response:** `204 No Content`.

---

## Platform API (Operator Only)

### POST /platform/tenants
**Access:** X-Platform-Key header only

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

## Booking (`/beauty/slots`, `/beauty/appointments`, requires `beauty_booking` module)

All endpoints require authentication & `[RequireModule("beauty_booking")]`.

**Specialist access control:** Specialist can only view/create/modify their own appointments; requesting another specialist's data returns `404 (existence not revealed)`.

### GET /beauty/slots
**Access:** owner, admin, specialist

Get free slots for a date.

**Query:**
- `locationId` (required): UUID
- `serviceId` (required): UUID
- `date` (required): ISO date (local to location)
- `specialistId` (optional): UUID; if omitted, returns slots from all specialists at location

**Response:** `200`
```json
[
  {
    "specialistId": "uuid",
    "startsAt": "2026-10-10T09:00:00Z",
    "endsAt": "2026-10-10T09:30:00Z",
    "label": "John Doe, 09:00–09:30",
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

**Errors:** `401`, `403`, `422`.

### GET /beauty/appointments
**Access:** owner, admin, specialist

List appointments (calendar view).

**Query:**
- `from` (optional, default: today UTC): ISO timestamp
- `to` (optional, default: from + 7 days): ISO timestamp
- `locationId` (optional): UUID
- `specialistId` (optional): UUID; specialist role always filters to own

**Response:** `200` `AppointmentDto[]`.

### GET /beauty/appointments/{id}
**Access:** owner, admin, specialist (specialist sees only own)

Get appointment details.

**Response:** `200` `AppointmentDto`.

**Errors:** `404` (existence not revealed for forbidden specialist).

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
    "id": "uuid (optional, new client if omitted)",
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
  "specialistId": "uuid",
  "serviceId": "uuid",
  "clientId": "uuid",
  "startsAt": "2026-10-10T09:00:00Z",
  "endsAt": "2026-10-10T09:30:00Z",
  "duration": 30,
  "status": "pending|confirmed",
  "source": "online",
  "priceOriginal": 500.00,
  "priceFinal": 450.00,
  "promotionId": "uuid|null",
  "reminder": "1h|none",
  "paymentMethod": "card|cash",
  "payment": { "status": "pending|paid|failed", "amount": 450.00 },
  "cancellation": { "windowHours": 12, ... }
}
```

**Errors:**
- `409 slot_unavailable` (overlap, cancelled too recently)
- `422 validation_failed` (bad input, specialist not at location, price not set)
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

**Response:** `200` `AppointmentDto`.

**Errors:** `409 slot_unavailable` (overlap), `422 validation_failed`.

### POST /beauty/appointments/{id}/cancel
**Access:** owner, admin, specialist (specialist: self only)

Cancel appointment. Refund amount calculated per cancellation policy (TASK-685).

**Body:** `{}` (empty)

**Response:** `200`
```json
{
  "appointment": { AppointmentDto },
  "refundAmount": 225.00,
  "refundPercent": 50,
  "feePercent": 0
}
```

---

## Catalog (`/beauty/services`, `/beauty/promotions`, requires `beauty_catalog` module)

### GET /beauty/services
**Access:** owner, admin, specialist (read only)

List active services.

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

List active promotions.

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
      "notes": [ { "id": "uuid", "body": "...", "createdAt", "authorUserId" } ],
      "appointments": [ { AppointmentDto... } ],
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
  "settingsJson": { "custom": "value" }
}
```

**Response:** `200` ChannelDto.

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
- `status` (optional): "proposed|approved|rejected"
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

**Errors:** `404`, `409 already_confirmed|already_rejected`.

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

Update cancellation policy.

**Body:** Same as GET response (all 5 fields required).

**Response:** `200` same.

**Errors:** `422 invalid_window_hours|invalid_refund_percent|invalid_fee_percent|settings_incomplete`.

---

## Error Codes Reference

| Code | HTTP | Meaning |
|------|------|---------|
| `invalid_credentials` | 401 | Email/password mismatch |
| `invalid_token` | 401 | Malformed/expired JWT |
| `tenant_required` | 401 | No tenant_id claim |
| `module_disabled` | 403 | Feature not enabled for tenant |
| `forbidden_role` | 403 | Insufficient permissions |
| `account_locked` | 423 | Too many failed attempts |
| `rate_limited` | 429 | Too many requests |
| `appointment_not_found` | 404 | Appointment not found |
| `client_not_found` | 404 | Client not found |
| `slot_unavailable` | 409 | Overlap or specialist fully booked |
| `already_cancelled` | 409 | Appointment status doesn't allow cancellation |
| `invite_invalid` | 404 | Token expired or invalid |
| `user_exists` | 409 | Email already registered in tenant |
| `tenant_slug_taken` | 409 | Slug already in use |
| `validation_failed` | 422 | Malformed request body |
| `payment_failed` | 402 | Card payment declined |
| `refund_failed` | 402 | Refund service error |

---

## Status Codes Summary

- `200 OK` — Success, body present.
- `201 Created` — Resource created, Location header optional.
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
