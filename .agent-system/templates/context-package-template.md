<!-- context-manager output (spec §11-13). Validated by schemas/context-package.schema.json.
     Give the assigned agent the minimum sufficient context — references, not whole files. -->

```yaml
task_id: TASK-XXX
goal: <one sentence>
context_level: small | medium | larger_curated   # cheap / standard / architecture task

include:
  rules:
    - CLAUDE.md#<section>
  requirements:
    - <spec doc>#<section>
  architecture:
    - docs/architecture.md#<section>
  decisions:
    - ADR-XXX
  code:
    - src/<path>
  contracts:
    - .ai/artifacts/contracts/<id>.json
  known_issues:
    - KI-XXX

sources:                       # read on demand instead of inlining
  - path: src/<path>
    symbol: <FunctionOrClass>
    purpose: <why this matters>
  - path: docs/decisions.md
    anchor: ADR-XXX
    purpose: <rule it establishes>

excluded:
  - <module explicitly out of scope for this task>

open_questions: []
```
