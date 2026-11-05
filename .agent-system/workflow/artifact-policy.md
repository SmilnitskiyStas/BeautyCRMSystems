# Artifact policy

Agents exchange stable, versioned deliverables as **artifacts** instead of re-deriving
each other's work or copying it around (spec §22-23).

## Location

Project runtime state — created in the project, never committed to this library:

```
.ai/artifacts/
  contracts/
  schemas/
  decisions/
  ui-specs/
  test-plans/
  research/
  requirements/
```

## Types (spec §23)

```
API_CONTRACT  DATA_SCHEMA  UI_SPEC  RESEARCH_REPORT  REQUIREMENTS
ARCHITECTURE_DECISION  TEST_PLAN  SECURITY_FINDINGS  CONTEXT_PACKAGE
```

## Metadata header

Every artifact starts with a header validated by `schemas/artifact.schema.json`
(`templates/artifact-template.md`):

```yaml
id:            # API-CONTRACT-REFUND-V1
type:          # API_CONTRACT
version:       # 1
created_by:    # backend-developer
task_id:       # TASK-204
status:        # draft | review | accepted | superseded | deprecated
consumers:     # [frontend-developer, qa-engineer]
source_files:  # [src/payments/RefundController.cs]
```

## Lifecycle

- `draft` -> `review` -> `accepted`. Only `accepted` artifacts may be depended on for
  parallel work.
- A breaking change means a **new version** (`...-V2`) with `supersedes` pointing at the
  old id; the old one goes `superseded`. Consumers are notified via the PM.
- An artifact that contradicts the code it describes is stale — same rule as docs
  (`source-of-truth.md`).

## Task wiring

A task consumes artifacts via `requires_artifacts` and emits them via `provides`
(`task-dag.md`). `validate-config.mjs` flags a `requires_artifacts` id that nothing
`provides`.
