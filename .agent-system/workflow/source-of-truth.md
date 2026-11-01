# Source of truth

Each kind of fact has exactly one authoritative store (spec §40). When a document
disagrees with its authoritative store, the document is **stale** — flag it and fix it in
a separate task; do not act on it.

| Fact | Authoritative store |
|---|---|
| Business requirements | project master spec document |
| Architecture | accepted ADRs + architecture docs |
| API shape | current API contract artifact / the code that serves it |
| Database shape | migrations + schema + code |
| Task status | the task store |
| Known issue / past lesson | `known-issues.md` (Resolved section) |
| Runtime behavior | tests + code |
| Domain vocabulary | `glossary.md` |
| What was tried and rejected | `rejected-approaches.md` |

## Rules

- Code that ships is truth about behavior. Docs describe intent.
- If the spec and the code disagree on **requirements**, the spec wins and the code is a
  bug. If they disagree on **behavior**, the code is what actually happens — reconcile
  with the spec owner (human gate for product ambiguity, see `human-gates.md`).
- Never treat a task log, a handoff, or a chat message as a source of truth. They are
  history.
