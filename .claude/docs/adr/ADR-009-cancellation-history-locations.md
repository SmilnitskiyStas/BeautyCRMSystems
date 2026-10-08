# ADR-009: Cancellation History & Location Management

**Date:** 2026-10-08

**Status:** Accepted (TASK-697, TASK-698)

**Context:**

Beauty CRM needs to track who cancelled appointments and why (audit trail). Additionally, locations are soft-deletable (no hard deletes) and can have different timezones. Requirements (user decisions):
1. Cancelled appointments stay in database; show in client history with metadata
2. Cancellation author (client, staff, system) tracked separately
3. Locations can be deactivated (soft-delete); active/inactive visible to admin only
4. Timezone changes locked during active bookings (prevents confusion)
5. Client/staff views show cancelled appointments differently (staff see reason, client see only type)

---

## Decision

**Cancellation metadata** (added to `beauty_appointments`, TASK-697):
- `cancelled_by_type` (VARCHAR, CHECK: client|staff|system)
- `cancelled_by_user_id` (FK to users, nullable; only set for staff cancellation)
- `cancel_reason` (VARCHAR ≤300; optional, visible only to staff)
- `cancelled_at` (TIMESTAMPTZ; existing field)

Constraints:
- `cancelled_by_type` required only when `status = 'cancelled'`
- `cancelled_by_user_id` required only when `cancelled_by_type = 'staff'`
- Reason ≤300 characters, trim on save

**Locations** (new API, TASK-697):
- New table `beauty_locations` (tenant_id + RLS FORCE):
  - `id`, `tenant_id`, `name` (unique per tenant, case-insensitive), `address`, `phone`, `timezone` (IANA zone, e.g., "Europe/Kyiv")
  - `is_active` (BOOLEAN; default true)
  - `created_at`, `updated_at`

**API:**
- `GET /locations` — staff-visible list; `includeInactive=true` only for owner/admin
- `POST /locations` — owner/admin; validates timezone via `TimeZoneInfo`, requires name/timezone, optional address/phone
- `PUT /locations/{id}` — modify name/address/phone/timezone/isActive
  - 409 `has_future_appointments` if deactivating with pending/confirmed appointments
  - 409 `timezone_locked` if changing timezone with active bookings
- Deactivated location: no new slots, no appointment creation, not shown in public API

**Cancellation visibility** (API responses):
- `AppointmentDto` includes (if cancelled):
  - `cancelledAt` (timestamp)
  - `cancelledBy { type, name? }` (name only for staff, only for viewer with permission)
  - `cancelReason` (only for owner/admin/specialist sees type but not reason)
- `ClientDto.cancelledCount`, `ClientDto.cancelledByClientCount` (new fields)
- `GET /appointments?includeCancelled=false` (default: hide cancelled from calendar view)
- `GET /clients/{id}` (card) includes full history with cancellations & metadata

**Cancellation originator:**
- Public API cancel (`/public/{slug}/appointments/{token}/cancel`) → `type = client`
- Staff cancel (`POST /beauty/appointments/{id}/cancel`) → `type = staff`, user_id set
- AI revert (`POST /beauty/ai/actions/{id}/revert`) → `type = system`
- Payment charge failure on booking → `type = system`

---

## Timezone Handling

**Per-location timezone:**
- All `beauty_specialist_locations.working_hours` keyed by location's timezone
- Absence `date_from`/`date_to` interpreted in location's timezone (local midnight to midnight)
- Appointment `starts_at` stored UTC; local time shown via location timezone

**Timezone lock:**
```csharp
public async Task<Result> UpdateLocationAsync(Guid locationId, UpdateLocationRequest req, CancellationToken ct)
{
    var location = await db.Locations.FindAsync(locationId, ct);
    
    // If changing timezone:
    if (req.Timezone != location.Timezone)
    {
        // Check for future active appointments
        var hasFutureAppointments = await db.Appointments
            .AnyAsync(a => a.LocationId == locationId 
                && a.StartsAt > DateTimeOffset.UtcNow
                && a.Status IN ("pending", "confirmed"), ct);
        
        if (hasFutureAppointments)
            return Error.Conflict("timezone_locked", "Cannot change timezone with active bookings.");
    }
    
    location.Timezone = req.Timezone ?? location.Timezone;
    await db.SaveChangesAsync(ct);
    return Result.Ok();
}
```

**Rationale:** Changing timezone mid-bookings confuses UI (9:00 local time becomes 10:00), breaks reminders.

---

## Cancellation Atomicity

**Flow (§14.2 M1):**
```csharp
public async Task<Result> CancelAsync(Guid appointmentId, string? reason, CancellationToken ct)
{
    // 1. Fetch appointment
    var appt = await db.Appointments.FindAsync(appointmentId, ct);
    if (appt?.Status NOT IN ("pending", "confirmed"))
        return Error.Conflict("appointment_closed", "Cannot cancel completed appointments.");
    
    // 2. Calculate refund (policy from settings)
    var policy = await settings.GetAsync(appt.TenantId, ct);
    var refundAmount = CalculateRefund(appt, policy);
    
    // 3. ATOMIC: Claim cancellation
    var updated = await db.Appointments
        .Where(a => a.Id == appointmentId 
            && a.Status IN ("pending", "confirmed"))
        .ExecuteUpdateAsync(s => s
            .Set(a => a.Status, "cancelled")
            .Set(a => a.CancelledAt, DateTimeOffset.UtcNow)
            .Set(a => a.CancelledByType, "staff")
            .Set(a => a.CancelledByUserId, currentUser.Id)
            .Set(a => a.CancelReason, reason?.Trim())
        , ct);
    
    if (updated == 0)
        return Error.Conflict("already_cancelled", "Appointment already cancelled or no longer pending.");
    
    // 4. Process refund (if applicable)
    if (refundAmount > 0 && appt.PaymentMethod == "card")
    {
        var refundResult = await payments.RefundAsync(appt.PaymentId, refundAmount, ct);
        if (!refundResult.Success)
        {
            // Refund failed: roll back cancellation
            await db.Appointments
                .Where(a => a.Id == appointmentId)
                .ExecuteUpdateAsync(s => s
                    .Set(a => a.Status, appt.Status)
                    .Set(a => a.CancelledAt, null)
                    .Set(a => a.CancelledByType, null)
                    .Set(a => a.CancelledByUserId, null)
                    .Set(a => a.CancelReason, null)
                , ct);
            return Error.PaymentFailed("refund_failed", refundResult.Error);
        }
    }
    
    // 5. Trigger reminder cleanup, notifications
    await notifier.NotifyCancelAsync(appt, refundAmount, ct);
    
    return Result.Ok(new { appointment = appt, refundAmount, refundPercent, feePercent });
}
```

**Idempotency key:** Payment refund uses `paymentId` as key (provider ensures idempotency on their end).

---

## Migration Strategy (backfill)

At `beauty_locations_and_cancellation_history` migration time:
1. Create new columns with defaults
2. Backfill cancelled appointments: `UPDATE beauty_appointments SET cancelled_by_type = 'system' WHERE status = 'cancelled' AND cancelled_by_type IS NULL`
3. Create locations from existing specialist_locations.locationId (dedup, set timezone)
4. Verify RLS is FORCE on both tables (migration throws if not)

---

## Consequences

### Positive
1. **Audit trail:** Full cancellation history for compliance/disputes
2. **Privacy control:** Client doesn't see cancellation reason (keeps details internal)
3. **Timezone safety:** Lock prevents configuration mistakes
4. **Soft deletes:** Locations & appointments permanently archived, never lost

### Negative
1. **UI complexity:** Must handle `includeCancelled` flag, show/hide reason by role
2. **Migration backfill:** Existing cancelled appointments get default `system` author
3. **Timezone lock latency:** Owner must cancel/reschedule before changing zone

---

## Testing

- Unit: Refund calculation with/without fee, rounding (down to kopiyky), cancellation logic
- Integration: Cancel appointment → verify status/metadata/refund; deactivate location → 404 for new bookings
- RLS: Multi-tenant isolation on locations; chúng ta bạng; cancellation metadata visible only to tenant
- Timezone: Change timezone with no active bookings (succeeds); with active bookings (409)
- Atomicity: Concurrent cancel requests (only one succeeds, idempotent refund)

---

## References

- `backend/BeautyCrm.Application/Features/BeautyBooking/CancellationService.cs`
- `backend/BeautyCrm.Application/Features/BeautyLocations/LocationService.cs`
- `backend/BeautyCrm.Tests/Beauty/LocationsAndCancellationHistoryTests.cs`
- `.claude/docs/beauty-contracts.md` § 16 — Locations & history contract
