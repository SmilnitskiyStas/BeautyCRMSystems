---
name: create-adr
description: >-
  Write an Architecture Decision Record — context, decision, consequences, alternatives.
  Use when making any significant architecture or tech-stack choice, and record it before
  implementation depends on it.
x-generated-from: skills/workflow/create-adr/SKILL.md
---

# Create ADR

## When

Any decision that is expensive to reverse: hosting, data layer, provider choice, module
boundaries, a framework, an auth model, a sync strategy. Not: naming, formatting, a local
refactor.

## Steps

1. Confirm this is architect scope and that the decision is not already fixed by an
   existing ADR (`workflow/source-of-truth.md`).
2. Draft using `templates/adr-template.md`:
   - **Context** — the forces: requirement, constraint, what prompted this. Link the spec
     section / task.
   - **Decision** — stated so an implementer can act on it without re-reading the context.
   - **Consequences** — positives and accepted trade-offs.
   - **Alternatives considered** — each with why it lost. Durable rejections also go to
     `rejected-approaches.md`.
   - **Scope of truth** — what this ADR is now authoritative for.
3. Number it `ADR-NNN` (next free number). Status `proposed`.
4. If the decision hits a human gate (`workflow/human-gates.md`) — architecture
   replacement, auth/security boundary, data-migration strategy — present it and wait for
   approval before setting `accepted`.
5. On acceptance: set `Status: accepted`, add the deciders, and emit it as an
   `ARCHITECTURE_DECISION` artifact. Hand to `documentation-writer` to reflect in the
   architecture docs.

## Validation

- The Decision section is actionable on its own.
- Every alternative has a stated reason for rejection.
- A superseding ADR sets the old one's status to `superseded by ADR-NNN` — never edits
  the old decision in place.

## Escalation

If the decision cannot be made without a product or business call the spec does not
cover, stop and escalate (`workflow/escalation-policy.md`).
