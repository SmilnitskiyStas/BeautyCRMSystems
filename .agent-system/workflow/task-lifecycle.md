# Task lifecycle

## States (spec §41)

```
draft -> needs_clarification -> researched -> planned -> ready -> in_progress -> review -> done
                                                                       \-> blocked
                                                                       \-> changes_requested -> in_progress
                                                              (any)  -> cancelled
```

Not every project uses every state. A trivial task can go `draft -> ready -> in_progress ->
done`. The extra states exist for work that needs clarification, research, or review.

## Definition of Ready (spec §42)

A task may enter `ready` only when all are true:

```yaml
goal_defined: true
acceptance_criteria_defined: true
blocking_questions: 0
required_dependencies_available: true
required_context_resolvable: true
assigned_agent_known: true
```

## Definition of Done (spec §43)

A task may enter `done` only when all that apply are true:

```yaml
implementation_completed: true
required_tests_pass: true
required_reviews_complete: true
artifacts_updated: true
task_log_written: true
docs_updated_if_needed: true
known_issue_recorded_if_applicable: true
human_gate_passed_if_required: true
```

## Complexity classifier (spec §44)

`trivial | low | medium | high | critical`, judged on: affected modules, architecture
impact, data migration, security, concurrency, financial impact, external integrations,
offline behavior, ambiguity, irreversible effects.

## Risk classifier (spec §45)

Boolean flags: `security, privacy, financial, data_loss, migration, external_integration,
production, architecture, cross_platform, concurrency, offline_sync`.

Complexity and risk together drive: which agent, which model tier
(`model-routing.md`), which reviews (`review-policy.md`), and whether a human gate
applies (`human-gates.md`).

## Proportionality

A one-line copy change is `trivial`, no risk flags, `cheap` tier, no research/architecture
pipeline. Routing must be proportional to the task (spec §77).
