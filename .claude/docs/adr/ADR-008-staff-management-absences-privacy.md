# ADR-008: Staff Management, Specialist Absences & Note Privacy

**Date:** 2026-10-08

**Status:** Accepted (TASK-691, TASK-696)

**Context:**

Beauty CRM needs to manage specialist profiles, their availability, and absence periods. Requirements (user decisions):
1. Admin creates specialist profiles; invite is optional (specialist may be non-user)
2. Absences block appointment slots; existing appointments are NOT auto-cancelled
3. Specialists can request absences (pending approval); managers can declare them (immediate)
4. Absence notes (medical, personal) are sensitive; visibility restricted
5. Specialist deactivation (is_active=false) immediately closes access

---

## Decision

**Specialist profiles & services:**
- `beauty_specialists` table: `id`, `tenant_id`, `name`, `position` (new), `phone`, `is_active`, `created_at`
- `beauty_specialist_services` (new): junction table with PK `(tenant_id, specialist_id, service_id)`
- Specialist offers ONLY assigned services (via junction table)
- Deactivated specialist (`is_active=false`) does not appear in public API or available slots

**Absences:**
- New table `beauty_specialist_absences` (tenant_id + RLS FORCE):
  - `id`, `tenant_id`, `specialist_id`, `type` (sick|vacation|day_off|other)
  - `date_from`, `date_to` (full days, inclusive, in location's timezone)
  - `status` (requested|approved|rejected|cancelled)
  - `note` (≤500 chars, sensitive)
  - `requested_by_user_id` (who requested), `decided_by_user_id` (admin decision), `decided_at` (timestamp), `created_at`
  - `cancelled_by_user_id` (new in TASK-696), `cancelled_at` (new in TASK-696)
  - Constraints: CHECK type/status/dates; EXCLUDE gist `daterange` to prevent overlapping requested/approved

**API (§13, v0.6-updated):**
- `GET /specialists` — staff-visible list with services/locations
- `POST /specialists` — owner/admin create; specialists can't create own profiles
- `PUT /specialists/{id}` — name/phone/position/isActive (owner/admin; admin can't modify owner's profile)
- `PUT /specialists/{id}/services` — owner/admin assign services
- `PUT /specialists/{id}/schedule` — set working hours per location
- `POST /specialists/{id}/invite` — send one-time token (optional; profile pre-exists)
- `GET /absences?from&to` — calendar view; filters by role
- `POST /specialists/{id}/absences` — create request/declaration
- `POST /absences/{id}/approve|reject|cancel` — manager/author transitions

**Visibility of absence notes:**
- Owner/admin see full note, type, who decided
- Specialist sees only own absences + type/dates (no note unless authored)
- Specialist cannot see colleagues' notes (privacy)

**Permission hierarchy:**
- owner: creates/edits/invites anyone; cannot be modified by admin
- admin: creates/edits/invites specialist only; cannot manage owner/admin profile
- specialist: cannot create/edit profiles; can create own absence requests (awaiting approval)

---

## Race Condition Protection: Advisory Lock

**Problem:** Absence overlaps with appointment creation or rejection. Without locking:
- Thread A: Create absence `2026-10-10 to 2026-10-12`
- Thread B (concurrent): Create appointment on `2026-10-11`
- Result: Appointment in absence period (silently allowed)

**Solution:** `pg_advisory_xact_lock` within a transaction:

```csharp
// In CreateAbsenceAsync, ApproveAbsenceAsync, CreateAppointmentAsync:
using var txn = await db.BeginTransactionAsync(ct);

var lockKey = pg_advisory_xact_lock(
    hashtextextended(app.tenant_id || ':specialist:' || specialist_id, 0)
);

// Now under lock:
// 1. Query existing absences & appointments
var conflicts = await db.GetConflicts(specialist_id, dateFrom, dateTo, ct);
// 2. If absence: create record
// 3. If appointment: verify no approved absence intersects
// 4. Commit

await txn.CommitAsync(ct);
```

**Result:** 
- Absence creation sees all concurrent appointments (and prevents conflicts)
- Appointment creation re-checks absence/services under lock (prevents missed conflicts)
- Conflicts returned as part of response for manager review

---

## Deactivation (is_active=false)

**One atomic transaction:**
1. `beauty_specialists.is_active = false`
2. Linked user (if exists): `users.is_active = false` (except owner)
3. All refresh tokens revoked: `refresh_tokens.revoked_at = now`
4. Pending invitations revoked: `invites.status = 'revoked'`

**Consequence:**
- Specialist login → 401 (inactive user)
- `POST /api/beauty/*` with specialist access token → 403 `specialist_inactive` (filter in `ActiveSpecialistFilter`)
- New invitations: `409 specialist_inactive`
- Reactivation: User remains linked; must explicitly re-enable via `PATCH /api/users/{id}/status {isActive:true}` (separate action)

---

## Specialist Services Query Performance

`SlotCalculator` and public API queries:
```sql
SELECT DISTINCT specialist_id 
FROM beauty_specialist_services 
WHERE tenant_id = $1 
  AND service_id = $2 
  AND specialist_id IN (
    SELECT id FROM beauty_specialists 
    WHERE tenant_id = $1 AND is_active = true
  )
```

Result: Only active specialists who serve the requested service appear in slot results.

---

## Consequences

### Positive
1. **Data integrity:** Absence-appointment conflicts prevented by database lock
2. **Privacy:** Sensitive absence notes visible only to authorized roles
3. **Graceful deactivation:** No cascading deletes; soft-delete with access revocation
4. **Reusable profiles:** Specialist can be re-invited without re-creating profile

### Negative
1. **Lock contention:** Advisory lock blocks concurrent changes (acceptable for beauty salon scale)
2. **Complexity:** Multiple tables (specialists, services, absences, users) to manage consistency
3. **Invitation state:** If invite expires or user doesn't join, specialist profile orphaned (no cleanup needed, just inactive)

---

## Testing

- Unit: Absence overlap detection, permission matrix, visibility filters
- Integration: Create absence + concurrent appointment creation (both succeed or one fails cleanly); deactivation revokes tokens
- RLS: Multi-tenant isolation on new tables; chúng ta bạng (no cross-tenant leaks)
- Advisory lock: Parallel threads attempting to create overlapping absences (only one succeeds)

---

## References

- `backend/BeautyCrm.Application/Features/BeautyStaff/` — StaffService, AbsenceService
- `backend/BeautyCrm.Infrastructure/Data/EfBeautyStore.Staff.cs` — Storage layer
- `backend/BeautyCrm.Tests/Beauty/StaffHardeningTests.cs` — Test scenarios
- `.claude/docs/beauty-contracts.md` § 13 — Staff management contract
