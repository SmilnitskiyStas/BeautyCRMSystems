---
id: agent-run-log
category: lifecycle
version: 1
summary: Appends one JSON line per finished subagent (agent, model, requests, tokens, tool calls, duration) to .ai/logs/agent-runs.jsonl for usage analytics.
event: SubagentStop
script: hooks/scripts/agent-run-log.mjs
applies_to: both
codex_status: unverified
config_keys: [agent_run_log]
---

# agent-run-log

## What it does

On every `SubagentStop`, reads the subagent's own transcript (`agent_transcript_path`) and
appends a record to `.ai/logs/agent-runs.jsonl`:

```json
{"ts":"…","session_id":"…","agent_id":"…","agent":"backend-developer",
 "requests":7,"input":14,"output":9210,"thinking":4100,"cache_read":182000,"cache_write":21000,
 "tool_calls":12,"models":["claude-sonnet-5-5"],"started":"…","ended":"…","duration_s":94,
 "final_message_chars":820}
```

`requests` = model calls. A call is written to the transcript as several entries sharing
one `message.id`, with partial early usage snapshots — the parser keeps the last entry per
id, otherwise output tokens are under-counted by orders of magnitude.

Read it with `node scripts/report-agent-usage.mjs --runs`.

## Config (`.ai/hooks.config.yaml`)

`agent_run_log: off` disables logging. Default on.

## Scope and limits

- Subagents only. The main session's own tokens are visible in `/usage` and `/context`;
  this hook does not duplicate them.
- Token counts are what the API reported, not a price. Cost depends on plan and model rates
  (`/usage` shows the session estimate); the report deliberately prints no dollar figure.
- Local data: it never leaves the machine and contains no prompt text. Add
  `.ai/logs/agent-runs.jsonl` to `.gitignore` unless the team wants to share it.

## Failure mode

Missing/unreadable transcript, unwritable directory — writes nothing, exits 0.
