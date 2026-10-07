<!-- One task in the DAG (spec §20, §41-45). Validated by schemas/task.schema.json.
     Lives in the project task store — runtime state, not this library. -->

```yaml
id: TASK-XXX
title: <imperative — "Implement partial refunds">
agent: <agent-id>            # may be empty until Definition of Ready is met
status: draft                # draft | needs_clarification | researched | planned | ready | in_progress | blocked | review | changes_requested | done | cancelled

complexity: trivial | low | medium | high | critical
risk:
  security: false
  data_loss: false
  migration: false
  financial: false
  # ... only list flags that are true or relevant

depends_on: []               # hard task deps — prefer requires_artifacts
requires_artifacts: []       # artifact ids consumed
provides: []                 # artifact ids produced
blocks: []

acceptance_criteria:
  - <observable, testable outcome>

definition_of_ready:
  goal_defined: false
  acceptance_criteria_defined: false
  blocking_questions: 0
  required_dependencies_available: false
  required_context_resolvable: false
  assigned_agent_known: false

# filled by the router / working agent:
model:
  tier: standard
  runtime: <claude|codex>
  resolved_model: null
  reason: <one line>
  escalations: []
```
