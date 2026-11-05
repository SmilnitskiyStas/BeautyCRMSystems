---
id: compact-snapshot
category: lifecycle
version: 1
summary: Saves a small working-state snapshot (branch, changed files, current task) to .ai/state/ right before context compaction, so it can be restored afterwards.
event: PreCompact
script: hooks/scripts/compact-snapshot.mjs
applies_to: both
codex_status: unverified
---

# compact-snapshot

## What it does

Runs on `PreCompact` (manual `/compact` and automatic). Writes
`.ai/state/session-snapshot.md` with: timestamp, git branch, `git status --short`
(capped at 40 lines), and the first lines of the current-task file
(`.claude/tasks/current.md` or `.ai/tasks/current.md`, whichever exists).

It **never blocks** compaction and never writes outside `.ai/state/`.

## Why

Compaction summarizes the conversation; exact file lists and the active task id are the
details a summary most often blurs. The paired `compact-restore` hook re-injects this
snapshot afterwards (a few hundred tokens) instead of the agent re-discovering state
with extra tool calls.

## Failure mode

Not a git repo, no task file, unwritable directory — the hook silently does nothing and
exits 0.

## Hygiene

Add `.ai/state/` to the project's `.gitignore` — it is session state, not project memory.
