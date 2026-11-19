---
id: dependency-gate
category: gate
version: 1
summary: Blocks Bash package install/upgrade commands for packages not on an approved list, routing unapproved changes to a human gate.
event: PreToolUse
matcher: Bash
script: hooks/scripts/dependency-gate.mjs
applies_to: consuming_project
codex_status: unverified
config_keys: [approved_packages]
---

# dependency-gate

## What it does

Runs on `PreToolUse` for `Bash`. Matches install/upgrade commands (`npm install|i|add|update|upgrade`,
`pnpm add|update|up`, `yarn add|upgrade`, `pip install`) and checks each package name
against `.ai/hooks.config.yaml` -> `approved_packages`. An unapproved package blocks the
command (exit 2) and points at `workflow/human-gates.md` for sign-off.

## Why `applies_to: consuming_project`

AI_agent_only itself ships no application dependencies — this hook is only meaningful
once generated into a project that has a package manifest. `applies_to` documents that
so `doctor`/docs don't suggest dogfooding it here the way `path-deny-list` is.

## Config (`.ai/hooks.config.yaml`)

```yaml
approved_packages:
  - react
  - zod
```

## Failure mode

No `.ai/hooks.config.yaml`, no `approved_packages` key, or a command that isn't a
recognized install/upgrade form — allows the call. This hook only blocks a recognized
install of a package that is explicitly not on the list.

## Codex CLI

No verified `PreToolUse`-equivalent event for this repo yet — see `hooks/README.md`.
Claude-only until confirmed; fallback is manual review before merge
(`workflow/human-gates.md`).
