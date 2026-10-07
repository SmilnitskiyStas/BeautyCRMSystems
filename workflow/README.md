# workflow/

Vendor-neutral operating rules for the agent system. One concern per file. The adapters
inline the relevant parts into `CLAUDE.md` / `AGENTS.md`; agents reference them by name.

These files are the **source** for process rules. Do not restate them in agent bodies —
link to them.

| File | Covers |
|---|---|
| `rules-hierarchy.md` | precedence when instructions conflict |
| `source-of-truth.md` | which store is authoritative for each kind of fact |
| `model-routing.md` | tier selection, escalation, downgrade, manual override, decision record |
| `task-lifecycle.md` | task states, Definition of Ready, Definition of Done, complexity & risk classifiers |
| `task-dag.md` | dependency graph, artifact dependencies, parallelism, vertical slices |
| `context-policy.md` | minimum-sufficient context, context levels, compaction ladder |
| `handoff-policy.md` | handoff record, blocker-through-PM rule |
| `artifact-policy.md` | artifact bus convention, types, lifecycle |
| `agent-communication-policy.md` | the Interaction Gate |
| `review-policy.md` | review pipeline, orchestration, anti-ping-pong |
| `escalation-policy.md` | when to stop and ask a human or raise the tier |
| `human-gates.md` | actions that always require explicit human approval |

## Non-goals

This is not an orchestration engine. The main CLI session (Claude Code or Codex)
orchestrates. Artifacts, the DAG, and the Interaction Gate are **conventions + schemas +
these policies + `validate-config` checks**, nothing more (spec §77, §89).
