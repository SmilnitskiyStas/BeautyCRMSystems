# Model routing

The shared layer never names a vendor model. It uses three tiers (spec §14):

```
cheap   standard   reasoning
```

Concrete model per runtime is resolved only in `adapters/<runtime>/model-map.yaml`.

## Head rule

> Use the cheapest model tier that can reliably complete the task.

## Tier guide (spec §19)

| Tier | Use for |
|---|---|
| `cheap` | routing, PM ops, status updates, file discovery, simple research, summarization, docs sync, mechanical edits, context filtering, basic classification |
| `standard` | ordinary coding: frontend, backend, DB changes, normal debugging, tests, normal review, UI implementation |
| `reasoning` | architecture, hard debugging, distributed systems, security-sensitive design, financial flows, destructive migrations, concurrency, offline sync, complex refactors, cross-system design, hard ambiguity |

## Resolution

1. Start from the assigned agent's `default_model_tier`.
2. Apply `risk_escalation` from the agent definition for any risk flag the task carries
   (e.g. `security -> reasoning`). Escalation can only raise the tier (spec §17).
3. Apply downgrade: if the task is genuinely `complexity: low` and `risk` all false, drop
   to the lowest of the agent's `allowed_model_tiers` that still fits (spec §18) — but
   only when the runtime and project policy allow it.
4. A manual instruction from the user overrides the heuristic, unless it breaks a safety
   or project rule (spec §50): "use reasoning tier", "do not use Opus", "keep it cheap".

## Decision record (spec §46)

Every task log records:

```yaml
model:
  tier: standard
  runtime: claude
  resolved_model: <as reported by the CLI, or null>
  reason: normal backend implementation
  escalations: []
```

If the runtime does not expose the exact model, write `null` — do not invent it.
