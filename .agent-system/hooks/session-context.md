---
id: session-context
category: lifecycle
version: 1
summary: At session start shows the active agent-system version to the user and injects the memory-bank active context into the session.
event: SessionStart
matcher: startup|resume|clear
script: hooks/scripts/session-context.mjs
applies_to: both
config_keys:
  - session_context
  - update_check
  - update_repo
codex_status: supported
codex_matcher: startup|resume
---

# session-context

## What it does

Runs on `SessionStart` (new session, resume, `/clear`; not after compaction — `compact-restore`
covers that).

1. **Version banner** — `systemMessage: agent-system v2.1.0`, read from `.claude/agent-system.json`
   (written by `sync-agents`) or, in a plugin, from the plugin manifest. The banner is shown to
   the user only; it adds no tokens to the model context.
2. **Update notice** — if a newer `v<semver>` release tag exists, the banner adds
   `update 2.2.1 available → /update-agents`. The hook only reads `.ai/state/update-check.json`;
   when that cache is older than 24 h it starts a detached background refresh
   (`git ls-remote --tags`, 8 s timeout), so session start is never delayed and a new release is
   noticed at most one session late. Offline is silent. Plugins are skipped (they update through
   their marketplace).
3. **Active context** — if `.ai/memory/activeContext.md` exists and has been filled in, it is
   returned as `additionalContext` (capped at 2000 characters). An empty template is ignored.

Nothing is output when there is neither a version stamp nor an active context.

## Config (`.ai/hooks.config.yaml`)

- `session_context: off` — disable the banner, the update notice and the injection.
- `update_check: off` — disable only the update check (env `AGENT_SYSTEM_UPDATE_CHECK=off` does the same).
- `update_repo: <git url>` — where to look for releases (default: the AI_agent_only_v2 GitHub repo; env `AGENT_SYSTEM_UPDATE_URL`).

## Failure mode

Missing files or unreadable JSON — outputs nothing, exits 0. Never blocks a session.
