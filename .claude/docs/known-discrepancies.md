# Known Discrepancies: Documentation vs. Implementation

**Date:** 2026-10-08 (TASK-700)

**Status codes:**
- ✓ RESOLVED — ADR or contract updated (TASK-700)
- ⚠ PENDING — Code correct; documentation needs update by documentation-writer
- ✗ BLOCKER — Code does NOT match spec; needs backend/frontend fix

This document tracks cases where documentation (contracts, ADRs, API spec) does NOT match the actual code. Only meaningful discrepancies listed (minor implementation detail enhancements not listed here).

---

### 1. AI Tool: `create_appointment` source

**Contract (§6):** Tool is available to AI; creates appointments with `source` configurable.

**Code (TASK-677):** AI `create_appointment` tool defaults to `source="ai"` (not mentioned in contract). When called by AI, appointments are marked with this source for audit trail.

**Status:** ✅ Not a bug; `source` field allows tracking AI-created bookings. Will be documented in schema.

---

### 2. Message Status Enum

**Contract (§9, beauty_messaging_and_consent):** Status values not enumerated in detail.

**Code (TASK-675):** Message status includes `draft` (created but not queued), `pending` (queued, awaiting send), `sent`, `failed`. Inbound messages use `received`.

**Status:** ✅ Implementation is richer than contract; no contradiction.

---

### 3. Retry Strategy: "×3" Interpretation

**Contract (§3, §8, §9):** "Retries ×3" specified without clarity (3 total? 1 initial + 3 retries?).

**Code (TASK-678):** Clarified as **3 total attempts** (not 1 + 3 retries). If all fail, status = `failed`.

**Status:** ✅ Confirmed with agent; implementation is clearer than contract.

---

### 4. IPaymentService Mock

**Contract (§5):** Interface defined; no implementation specified.

**Code (TASK-675):** `MockPaymentService` always succeeds; real implementation deferred.

**Status:** ✅ Mock is appropriate for MVP. Real provider (Stripe, etc.) plugged in later.

---

### 5. Tenant-Slug Lookup During Webhook

**Contract (§9):** Webhook tenant resolution not detailed.

**Code (TASK-676):** Uses special RLS policy `channel_webhook_lookup` to resolve tenant by channel ID (not slug).

**Status:** ✅ Secure implementation; tenant = beauty_channels.tenant_id after channel lookup.

---

### 6. AI Tool: `build_audience`

**Contract (§6):** Tool listed; no details on segmentation criteria.

**Code (TASK-677):** Tool signature defined (locationIds[], serviceIds[], filters); execution not implemented (awaits worker integration).

**Status:** ⚠️ Tool defined but not fully executed; implementation deferred to TASK-679+. No contradition; tracked as task dependency.

---

### 7. Promotion Preview: Multiple Active Promotions

**Contract (§2):** "Из нескольких акций діє одна з найбільшою знижкою" (one promotion with highest discount wins).

**Code (TASK-675):** `PromotionPricing.Quote()` selects max discount; multiple promotions don't stack.

**Status:** ✅ Matches contract.

---

### 8. Appointment Overlap: Cancelled & No-Show

**Contract (§9, exclusion constraint):** "Cancelled / no_show слоты не блокують" (cancelled/no_show don't block future slots).

**Code (TASK-674):** EXCLUDE constraint WHERE (status IN ('pending', 'confirmed', 'completed')) — cancelled & no_show excluded.

**Status:** ✅ Matches contract.

---

### 9. Working Hours: JSONB Format

**Contract (§9, SpecialistLocation.working_hours):** Format: `{"mon":[{"from":"09:00","to":"18:00"}],...}` specified.

**Code (TASK-674):** Exactly as specified; validated in SlotCalculator (backend/BeautyCrm.Application/Features/BeautyBooking/SlotCalculator.cs).

**Status:** ✅ Matches.

---

### 10. Platform Operator: X-Platform-Key Header

**Contract (§10):** "заголовок X-Platform-Key" specified.

**Code (TASK-675, AuthController):** Header checked; content validated. Endpoint returns `401` if missing or incorrect.

**Status:** ✅ Matches.

---

## Potential Future Inconsistencies

These are areas where contracts may drift from code as development continues. Monitor:

1. **Public Appointment API (TASK-688):** Contract mentions `GET /appointments` from public booking form; code not yet implemented. Mark as "in development" in docs.

2. **Outbox Implementation:** Contract mentions "×3 retries"; worker implements correctly. Ensure integration tests validate.

3. **Instagram 24-hour Window:** Contract notes "повідомлення відхиляється, менеджер бачить"; implementation rejects outside window (no HANDOFF yet). Confirm UX with product.

4. **AI Limits (20% discount, 09:00–20:00):** Hard-coded in `AiSettings`; if policy becomes tenant-configurable, update docs.

---

## How to Use This Document

- **Before implementing a feature:** Check for related items in this doc.
- **After code review:** Update this doc with any new discrepancies found.
- **Before merge:** Ensure no items marked ⚠️ are left unresolved.

---

## Resolution Template

If you find a discrepancy:

```
### N. [Title]

**Contract:** [Quote & section]
**Code:** [What's actually implemented]
**Status:** 
  ✅ Matches (explain any clarifications)
  ⚠️ Diverges (explain why, next action)
  ❌ Contradiction (fix required)
```

Add it to this file immediately; don't let misalignment accumulate.

---

**Last reviewed:** 2026-10-07 (TASK-683, documentation-writer)
**Next review:** After TASK-684–690 (auth, UI, public API)
