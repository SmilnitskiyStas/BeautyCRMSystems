# hooks/

The **source of truth** for every hook. One file per hook: `hooks/<id>.md` — YAML
frontmatter (validated by `schemas/hook.schema.json`) plus a doc body — and
`hooks/scripts/<id>.mjs`, the zero-dependency Node implementation.

`adapters/claude/generate-hooks.mjs` copies `hooks/scripts/**` verbatim into
`.claude/hooks/**` and compiles the frontmatter into `.claude/settings.hooks.json`, a
mergeable fragment. `scripts/sync-agents.mjs` never writes `.claude/settings.json`
itself (it is project runtime state) — merge the fragment in explicitly with
`node scripts/apply-hook-settings.mjs --apply` (`workflow/hooks-policy.md`).

## Hooks

| id | event | category | applies_to |
|---|---|---|---|
| `task-log-sync` | `Stop` | lifecycle | both |
| `path-deny-list` | `PreToolUse` (`Write\|Edit\|MultiEdit`) | guardrail | both |
| `dependency-gate` | `PreToolUse` (`Bash`) | gate | consuming_project |
| `compact-snapshot` | `PreCompact` | lifecycle | both |
| `compact-restore` | `SessionStart` (`compact`) | lifecycle | both |
| `agent-run-log` | `SubagentStop` | lifecycle | both |
| `session-context` | `SessionStart` (`startup\|resume\|clear`) | lifecycle | both |

## Runtime config

Hooks read parameters from `.ai/hooks.config.yaml` — project-owned runtime state, never
generated (see `templates/hooks-config-template.yaml` and `.ai/README.md`), not
anything under `hooks/` itself. This keeps a hook definition reusable across every
consuming project without editing generated output.

## Scripts must stay self-contained

`hooks/scripts/**` is copied verbatim into a consuming project's `.claude/hooks/`,
which has no access to this repo's `scripts/lib/`. Never import anything from outside
`hooks/scripts/` in a hook implementation — `hooks/scripts/lib/hook-io.mjs` is the only
shared helper, and it ships alongside them.

## Codex CLI

Verified live on codex-cli 0.171.0 (2026-11-20). `sync-agents` writes `.codex/hooks/**` and an **inert**
`.codex/hooks.available.json` with the hooks marked `codex_status: supported`; nothing runs until you opt in:
`node scripts/apply-hook-settings.mjs --codex --apply --only session-context,dependency-gate` (writes
`.codex/hooks.json`). Codex also needs the project trusted and each hook approved once inside Codex.

| hook | Codex |
|---|---|
| `session-context` | supported — `SessionStart` (`startup\|resume`); injected active context reached the model |
| `dependency-gate` | supported — `PreToolUse` on shell commands; blocks via JSON `permissionDecision: deny` (see below) |
| `task-log-sync` | supported — `Stop` |
| `path-deny-list` | not supported — Codex edits through `apply_patch`, which `PreToolUse` does not see |
| `compact-snapshot`, `compact-restore`, `agent-run-log` | not supported — Codex has no `PreCompact` / `SubagentStop` |

Blocking differs: Claude Code honours stderr + exit 2; Codex ignores exit 2 for `PreToolUse` (the hook is just
marked "Failed" and the command still runs) and honours only a JSON `permissionDecision: "deny"`. `block()` in
`hooks/scripts/lib/hook-io.mjs` picks the right one — Codex hook input carries a `turn_id`, Claude's does not.
