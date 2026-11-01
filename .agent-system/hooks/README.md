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

`adapters/COMPATIBILITY.md` lists Codex's `hooks` feature as "stable," but no event
name or config shape here has been verified against the binary the way
`model-map.yaml` is (spec §8: verified behavior, not assumptions). Every hook above is
Claude-only for now; `adapters/codex/generate-hooks.mjs` is deferred until that
verification happens.
