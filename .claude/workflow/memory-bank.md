<!-- vendored from the agent library — regenerate with sync-agents -->
# Memory bank

Working memory that survives `/clear`, compaction and a new session, kept in `.ai/memory/` next to
the long-term project memory (`.ai/README.md`). It is an extension, not a second system: there is no
separate `memory-bank/` directory.

## Files

| File | Holds | Lifetime |
|---|---|---|
| `.ai/memory/activeContext.md` | current focus, next action, blockers, key files | overwritten; ≤ 40 lines |
| `.ai/memory/inbox.md` | raw notes and ideas | until triaged |
| `.ai/memory/progress.md` | milestones, one line each | append only |
| `.ai/memory/archive/` | closed phases (`activeContext` snapshots) | permanent |

Already covered elsewhere — do not duplicate: decisions → `architecture-decisions/` (ADR, `create-adr`),
research → `.ai/artifacts/research/`, tasks → `.claude/tasks/` and the task DAG, defects →
`known-issues.md`, session handoff → `.ai/state/handoff.md` (`handoff-session`).

## Lifecycle → what we already have

| Phase | Do this | Memory touched |
|---|---|---|
| start | `task-intake`; read the active context (injected by `session-context`) | — |
| plan | `decompose-spec`, `requirements-analyst` | `activeContext` (focus, next action) |
| think | `project-architect`, `create-adr` | ADR |
| do | implementer agents; task log | — |
| log | `memory-bank` → log | `inbox` / `progress` |
| reflect | `memory-bank` → reflect, `resolve-known-issue` | patterns / rejected approaches |
| archive | `memory-bank` → archive | `archive/`, `progress` |

## Rules

- Only confirmed information: backed by code, a human decision, an accepted ADR or finished work.
  The agent never invents goals, context or decisions; if unsure, ask one question.
- Pointers, not copies (task id, path, commit).
- The active context is *read by the session start hook*; keep it short, it costs tokens every session.
  Turn the injection off with `session_context: off` in `.ai/hooks.config.yaml`.
- Never rename, relocate or bulk-rewrite existing memory files unless the user asks. Preserve unknown
  files and sections.
- Re-read a file before writing if another session may have changed it; merge narrowly.
- The memory bank is project-owned state: it is never generated and never committed back to the
  agent library.
