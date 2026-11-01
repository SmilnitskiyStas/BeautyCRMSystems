---
id: compact-restore
category: lifecycle
version: 1
summary: After compaction, re-injects the snapshot saved by compact-snapshot as additional context so the working state survives summarization.
event: SessionStart
matcher: compact
script: hooks/scripts/compact-restore.mjs
applies_to: both
codex_status: unverified
---

# compact-restore

## What it does

Runs on `SessionStart` with matcher `compact` (the event Claude Code fires after a
compaction). Reads `.ai/state/session-snapshot.md` and returns it as
`hookSpecificOutput.additionalContext`, prefixed with how old it is. Capped at 3000
characters.

## Rules

- Ignores a snapshot older than 6 hours (stale state is worse than none).
- Read-only: never edits the snapshot or any other file.

## Failure mode

No snapshot, unreadable file — outputs nothing, exits 0.
