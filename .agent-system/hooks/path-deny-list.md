---
id: path-deny-list
category: guardrail
version: 1
summary: Blocks Write/Edit/MultiEdit on file paths matching a configured deny-list, enforcing "generated — do not edit" boundaries.
event: PreToolUse
matcher: Write|Edit|MultiEdit
script: hooks/scripts/path-deny-list.mjs
applies_to: both
codex_status: unverified
config_keys: [deny_paths]
---

# path-deny-list

## What it does

Runs on `PreToolUse` for `Write`/`Edit`/`MultiEdit`. Compares the target file path
against the glob patterns in `.ai/hooks.config.yaml` -> `deny_paths`; a match blocks
the call (exit 2) with a message naming the pattern that matched.

## Dogfooding on this repo

AI_agent_only's own `.ai/hooks.config.yaml` sets:

```yaml
deny_paths:
  - .claude/**
  - .codex/**
```

turning the "GENERATED — DO NOT EDIT DIRECTLY" header already on every generated file
into an enforced rule instead of a comment an agent can ignore.

## For a consuming project

Set `deny_paths` to whatever that project should never let an agent touch directly —
generated output, secrets directories, vendored code, infra config, etc.

## Failure mode

No `.ai/hooks.config.yaml`, no `deny_paths` key, or a path that matches nothing —
allows the call. This hook only ever blocks on an explicit, configured match.

## Codex CLI

No verified `PreToolUse`-equivalent event for this repo yet — see `hooks/README.md`.
Claude-only until confirmed; fallback is the project's own permission rules
(`~/.codex/rules/*.rules`).
