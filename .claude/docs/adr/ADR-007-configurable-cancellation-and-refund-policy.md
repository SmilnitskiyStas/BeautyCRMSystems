# ADR-007: Configurable Cancellation & Refund Policy

**Date:** 2026-10-07

**Status:** Accepted (TASK-685)

**Context:**

Beauty CRM supports online bookings with payments. Cancellation policies vary:
- Some salons: full refund up to 24h before, 50% refund within 24h
- Others: no refunds after 48h
- Some charge a flat fee for processing

**Requirements:**
- **Tenant-configured:** Owner sets policy, not hardcoded
- **UI-aware:** Client sees refund % before cancelling
- **Transparent:** No surprise refunds or hidden fees

**Initial MVP:** Default policy = 50% refund if cancelled ≤12h before, 100% if >12h.

---

## Decision

We store cancellation policy in a **single-row table** per tenant, with sensible defaults.

### Policy Table

```sql
CREATE TABLE beauty_cancellation_settings (
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL UNIQUE,
    window_hours INT NOT NULL (0..720), -- Hours before appointment when refund changes
    refund_percent_in_window INT NOT NULL (0..100), -- Refund % if cancelled within window
    refund_percent_outside INT NOT NULL (0..100), -- Refund % if cancelled outside window
    deduct_fee BOOLEAN DEFAULT false, -- Whether to deduct platform fee from refund
    fee_percent INT NOT NULL (0..100), -- Platform fee % (only applied if deduct_fee = true)
    created_at TIMESTAMPTZ,
    updated_at TIMESTAMPTZ,
    
    CHECK (window_hours BETWEEN 0 AND 720),
    CHECK (refund_percent_in_window BETWEEN 0 AND 100),
    CHECK (refund_percent_outside BETWEEN 0 AND 100),
    CHECK (fee_percent BETWEEN 0 AND 100)
);
```

### Default Values

If no row exists for tenant:
- `window_hours = 12`
- `refund_percent_in_window = 50`
- `refund_percent_outside = 100`
- `deduct_fee = false`
- `fee_percent = 0`

**Meaning:** Refund 50% if cancelled ≤12h before, 100% if >12h, no fees.

---

## API

### GET /api/beauty/settings/cancellation

**Access:** owner, admin, specialist (read-only)

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

If no custom policy, returns defaults.

### PUT /api/beauty/settings/cancellation

**Access:** owner only (no admin/specialist access; 403 otherwise)

**Request:**
```json
{
  "windowHours": 24,
  "refundPercentInWindow": 25,
  "refundPercentOutside": 100,
  "deductFee": true,
  "feePercent": 2
}
```

**Response:** `200` (updated policy)

**Errors:**
- `403 forbidden_role` — Not owner
- `422 invalid_window_hours` — Outside 0–720 range
- `422 invalid_refund_percent` — Outside 0–100 range
- `422 invalid_fee_percent` — Outside 0–100 range
- `422 settings_incomplete` — Required field missing (no default substitution)

---

## Implementation

### Service

```csharp
public class CancellationSettingsService(BeautyDbContext db, ITenantContext tenantContext)
{
    public async Task<CancellationSettingsDto> GetTermsAsync(CancellationToken ct)
    {
        var settings = await db.CancellationSettings
            .FirstOrDefaultAsync(s => s.TenantId == tenantContext.TenantId, ct);
        
        // Return stored policy or defaults
        return (settings ?? new CancellationSettingsRow()).ToDto();
    }
    
    public async Task<Result<CancellationSettingsDto>> UpdateTermsAsync(
        CancellationSettingsRequest req, CancellationToken ct)
    {
        // Validation
        if (req.WindowHours < 0 || req.WindowHours > 720)
            return Error.Validation("invalid_window_hours", "WindowHours must be 0–720.");
        if (req.RefundPercentInWindow < 0 || req.RefundPercentInWindow > 100)
            return Error.Validation("invalid_refund_percent", "RefundPercent must be 0–100.");
        if (req.RefundPercentOutside < 0 || req.RefundPercentOutside > 100)
            return Error.Validation("invalid_refund_percent", "RefundPercent must be 0–100.");
        if (req.FeePercent < 0 || req.FeePercent > 100)
            return Error.Validation("invalid_fee_percent", "FeePercent must be 0–100.");
        
        var tenantId = tenantContext.TenantId!.Value;
        var settings = await db.CancellationSettings.FirstOrDefaultAsync(
            s => s.TenantId == tenantId, ct);
        
        if (settings is null)
        {
            settings = new CancellationSettingsRow
            {
                TenantId = tenantId,
                WindowHours = req.WindowHours,
                RefundPercentInWindow = req.RefundPercentInWindow,
                RefundPercentOutside = req.RefundPercentOutside,
                DeductFee = req.DeductFee,
                FeePercent = req.FeePercent
            };
            db.CancellationSettings.Add(settings);
        }
        else
        {
            settings.WindowHours = req.WindowHours;
            settings.RefundPercentInWindow = req.RefundPercentInWindow;
            settings.RefundPercentOutside = req.RefundPercentOutside;
            settings.DeductFee = req.DeductFee;
            settings.FeePercent = req.FeePercent;
        }
        
        await db.SaveChangesAsync(ct);
        
        return Result.Ok(settings.ToDto());
    }
}
```

### Cancellation Service

```csharp
public class CancellationService(
    IBookingStore store,
    CancellationSettingsService settings,
    TimeProvider clock)
{
    public async Task<Result<CancelResult>> CancelAsync(
        Guid appointmentId, CancellationToken ct)
    {
        var appt = await store.GetAppointmentAsync(appointmentId, ct);
        if (appt is null || appt.Status is "cancelled" or "no_show")
            return Error.Conflict("already_cancelled", "Appointment already cancelled or no-show.");
        
        if (appt.Status is not ("pending" or "confirmed"))
            return Error.Conflict("appointment_closed", "Cannot cancel completed appointments.");
        
        // Get policy
        var policy = await settings.GetTermsAsync(ct);
        var now = clock.GetUtcNow();
        
        // Calculate refund
        var timeTillAppointment = appt.StartsAt - now;
        var inWindow = timeTillAppointment.TotalHours <= policy.WindowHours;
        var refundPercent = inWindow
            ? policy.RefundPercentInWindow
            : policy.RefundPercentOutside;
        
        // Apply fee
        var fee = policy.DeductFee ? policy.FeePercent : 0;
        var refundAmount = appt.PriceFinal * (refundPercent / 100m) * ((100m - fee) / 100m);
        
        // Round DOWN (client doesn't get more than calculated)
        refundAmount = Math.Floor(refundAmount * 100) / 100;
        
        // Mark cancelled
        appt.Status = "cancelled";
        appt.CancelledAt = now;
        
        await store.UpdateAppointmentAsync(appt, ct);
        
        // Process refund
        if (refundAmount > 0 && appt.PaymentMethod == "card")
        {
            var payment = await store.GetPaymentAsync(appt.Id, ct);
            if (payment?.Status == "paid")
            {
                var result = await store.RefundPaymentAsync(payment.Id, refundAmount, ct);
                if (!result.Success)
                    return Error.PaymentFailed("refund_failed", result.Error);
            }
        }
        
        return Result.Ok(new CancelResult(
            new AppointmentDto(appt),
            refundAmount,
            refundPercent,
            fee
        ));
    }
}
```

---

## Slot & Appointment DTOs (include policy)

**FreeSlot (from GET /slots):**
```json
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
```

**AppointmentDto (responses from POST/GET /appointments):**
```json
{
  "id": "uuid",
  "startsAt": "2026-10-10T09:00:00Z",
  "status": "confirmed",
  "priceFinal": 450.00,
  "cancellation": { ... policy ... }
}
```

**Usage:** Frontend shows refund % before confirming cancellation:
```javascript
// On cancel button click:
const refundAmount = appt.priceFinal * (appt.cancellation.refundPercentInWindow / 100);
alert(`You will receive a refund of ${refundAmount.toFixed(2)} UAH`);
```

---

## Calculation Details

### Formula

```
refundPercent = (now -> startsAt) <= windowHours
  ? refundPercentInWindow
  : refundPercentOutside

fee = deductFee ? feePercent : 0

refundAmount = priceFinal × (refundPercent / 100) × ((100 - fee) / 100)

refundAmount = floor(refundAmount * 100) / 100  // Round DOWN to kopiyky
```

### Examples

**Scenario 1: Default policy (12h window, 50% in, 100% out, no fee)**
- Appointment: 2026-10-10 09:00 UTC, price 1000
- Cancel at: 2026-10-10 08:00 UTC (1h before) → **in window**
  - Refund: 1000 × 50% = 500 UAH
- Cancel at: 2026-10-09 09:00 UTC (24h before) → **outside window**
  - Refund: 1000 × 100% = 1000 UAH

**Scenario 2: Strict policy (48h window, 25% in, 100% out, 2% fee)**
- Appointment: 2026-10-10 09:00, price 1000
- Cancel at: 2026-10-09 10:00 (23h before) → **in window**
  - Refund: 1000 × 25% × 98% = 245 UAH (25% less 2% fee)
- Cancel at: 2026-10-08 09:00 (48h before) → **outside window**
  - Refund: 1000 × 100% × 98% = 980 UAH

**Scenario 3: No refund policy (window 0, 0% in, 50% out)**
- Any cancellation → **always in window** (0 hours)
  - Refund: 1000 × 0% = 0 UAH (no refund)

---

## UI Representation

### GET /slots response includes cancellation policy

Client can show on booking form:
```
✓ Refund 50% if cancelled within 12 hours
✓ Refund 100% if cancelled more than 12 hours before
✓ No platform fees
```

### GET /appointments/{id} before cancel

```
Current price: 450.00 UAH
If cancelled now:
  • Within window (9 hours to appointment): 50% refund = 225.00 UAH
  • Outside window (48+ hours): 100% refund = 450.00 UAH
```

---

## Edge Cases

### Appointment in past

Cannot cancel completed/no-show appointments; only pending/confirmed can be cancelled.

### Zero refund

```csharp
if (refundAmount == 0)
{
    // Don't call payment service; mark as cancelled only
    appt.Status = "cancelled";
    await store.UpdateAppointmentAsync(appt, ct);
}
```

### Timezone edge cases

All times stored in UTC; policy comparison uses UTC:
```csharp
var timeTillAppointment = appt.StartsAt - now;  // Both UTC
var inWindow = timeTillAppointment.TotalHours <= policy.WindowHours;
```

---

## Consequences

### Positive

1. **Flexible:** Owner can tailor policy to business model
2. **Transparent:** Clients see refund % before confirming cancel
3. **Auditable:** Policy stored in database, can be retrieved for any appointment
4. **Simple:** Single table, no complex rules engine

### Negative

1. **Owner complexity:** Multiple % fields, validation burden
   - **Mitigation:** UI with sensible presets (strict, moderate, generous)

---

## Testing

- Unit: Refund calculation with various policies
- Integration: Cancel appointment → verify refund amount & status
- Edge: Window boundary (exactly at window_hours), zero refund, fee rounding

---

## References

- `.claude/docs/database.md` — `beauty_cancellation_settings` table
- `.claude/docs/api.md` — `/settings/cancellation` endpoints
- `backend/BeautyCrm.Application/Features/BeautyBooking/CancellationSettingsService.cs`
