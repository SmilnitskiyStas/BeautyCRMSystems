---
id: task-log-sync
category: lifecycle
version: 1
summary: Blocks a session from ending after code edits until a task log has been written or updated, enforcing task_log_written from the Definition of Done.
event: Stop
script: hooks/scripts/task-log-sync.mjs
applies_to: both
codex_status: unverified
config_keys: [task_log_dir]
---

# task-log-sync

## What it does

Runs on Claude Code's `Stop` event. Reads the session transcript; if the turn included
a `Write`/`Edit`/`MultiEdit` tool call but no file under the configured task-log
directory (`task_log_dir`, default `.ai/logs/tasks`) was created or modified since the
turn began, it blocks the stop (exit 2) and points the agent at
`create-task-log`/`update-task-status` — operationalizing `task_log_written: true` from
`workflow/task-lifecycle.md` (Definition of Done).

## Heuristic, not a guarantee

Detecting "this turn" from transcript timestamps and directory mtimes is a best-effort
check, not a precise one. It fails open (allows the stop) on any parse error, missing
transcript, empty transcript, or when Claude's `stop_hook_active` flag is already set —
this hook must never be the reason a session cannot end.

## Config (`.ai/hooks.config.yaml`)

```yaml
task_log_dir: .ai/logs/tasks   # optional — default shown
```

## Codex CLI

No verified `Stop`-equivalent event for this repo yet — see `hooks/README.md`.
Claude-only until confirmed; fallback is the agent self-checking the Definition of Done
before ending a turn.
