# Rules hierarchy

When two instructions conflict, higher wins (spec §39):

1. **Runtime / system safety rules** — the CLI's own guardrails, sandbox, permission mode.
2. **Project root instructions** — `CLAUDE.md` / `AGENTS.md`.
3. **Accepted ADRs and architecture rules** — `.ai/memory/architecture-decisions/` or the
   project's decisions doc.
4. **Project spec / requirements** — the project's master spec document.
5. **Current task** — its goal and acceptance criteria.
6. **Agent responsibilities** — the assigned role's `responsibilities` / `must_not`.
7. **Skill procedure** — the steps in the invoked skill.
8. **Historical patterns / memory** — known-issues, implementation-patterns, glossary.

## On conflict

- Do not silently pick a side.
- Name the source of truth for the disputed point (see `source-of-truth.md`).
- If it stays ambiguous, escalate (`escalation-policy.md`) — do not guess.
- A lower level never overrides a higher one "because it is more specific". A skill that
  contradicts an ADR is a bug in the skill.
