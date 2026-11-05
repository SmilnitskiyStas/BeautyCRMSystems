<!-- One task log per completed task. Save to the project task-log store.
     `metrics` feeds scripts/report-agent-usage.mjs (spec §46-48). -->

# TASK-XXX: [Title]

**Date:** YYYY-MM-DD
**Agent:** [agent-id]
**Status:** done | changes_requested | blocked
**Duration:** [e.g. 2h, or —]

## What was done
[Short description]

## Files changed
- path/to/file — what changed

## Decisions made
[Any decisions during implementation; link an ADR if one was created]

## Artifacts
- [artifact id] — created | updated | consumed

## Tests
- Written: yes/no — [what]
- Build / typecheck: pass/fail
- Suite before: [N failing] → after: [N failing]

## Model routing decision (spec §46)

```yaml
agent: [agent-id]
model:
  tier: cheap | standard | reasoning
  runtime: claude | codex
  resolved_model: [as reported by the CLI, or null]
  reason: [one line]
  escalations: []            # e.g. ["risk:migration -> reasoning"]
```

## Metrics (spec §47 — fill what is observable)

```yaml
retry_count: 0
human_corrections: 0
review_findings: 0
tests_failed_before: 0
tests_failed_after: 0
context_compaction_count: 0
```

## Notes for next agent
[Important context; or —]
