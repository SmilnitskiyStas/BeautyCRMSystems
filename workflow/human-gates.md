# Human gates

A human stays in the loop. The AI does not make irreversible or high-impact decisions on
its own (spec §28).

## Always require explicit human approval

- Large product-requirement ambiguity — a decision the spec does not answer.
- Destructive database migration.
- Production data deletion.
- Architecture replacement.
- Auth / security boundary change.
- Secrets / credentials handling.
- Payment / financial critical flow.
- Legal / compliance assumptions.
- Final merge / release, if project policy requires it.

## How a gate works

1. The agent prepares everything up to the gated action and stops.
2. It presents: what will happen, why, what is irreversible about it, and the options.
3. A human approves in the session. Approval is per-action and per-session — it does not
   carry to the next action or a later session.
4. Only then does the agent proceed.

## Relationship to other policies

- `escalation-policy.md` — a gate is one reason to stop and ask.
- `review-policy.md` — critical security findings route here.
- `model-routing.md` — gated tasks are almost always `reasoning` tier, but the gate is
  independent of tier.
