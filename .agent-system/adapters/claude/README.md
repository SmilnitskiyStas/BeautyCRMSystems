# adapters/claude

Generates native Claude Code config from the shared source of truth.

## What it generates

| From | To | By |
|---|---|---|
| `agents/**/*.md` | `.claude/agents/<id>.md` | `adapters/claude/generate.mjs` |
| `skills/**/SKILL.md` | `.claude/skills/<slug>/SKILL.md` | `adapters/claude/generate-skills.mjs` |
| `hooks/*.md` + `hooks/scripts/**` | `.claude/hooks/**` + `.claude/settings.hooks.json` | `adapters/claude/generate-hooks.mjs` |

Run `node scripts/sync-agents.mjs` (or `--check` in CI). Generated files carry a
`GENERATED — DO NOT EDIT` header; edit the source and re-sync.

## Field mapping

| Shared | Claude frontmatter | Notes |
|---|---|---|
| `id` | `name` | |
| `summary` (+ `when_to_use`) | `description` | drives auto-delegation |
| `default_model_tier` | `model` | via `model-map.yaml` `tiers` |
| `default_model_tier` | `effort` | via `model-map.yaml` `effort`; omitted if unset |
| `tools` (capabilities) | `tools` | `read→Read`, `search→Grep,Glob`, `edit→Write,Edit`, `shell/run-tests→Bash`, `web→WebFetch,WebSearch`, `spawn-subagent→Agent`; omitted ⇒ inherit all |
| `read_only: true` | `disallowedTools: Write, Edit` | |
| `skills` (ids) | `skills` | last path segment (slug); slug uniqueness enforced by `validate-config` |
| body | body | verbatim, after a generated tier note |

`handoff_targets`, `produces`, `required_context`, `risk_escalation`, `must_not`,
`allowed_model_tiers` are shared-layer metadata used by `workflow/*` policies and
`validate-config`; they are not Claude frontmatter fields.

## model-map.yaml

The only place a Claude model name appears. Verified valid for `claude` 2.1.197.
See `../COMPATIBILITY.md` for the tier table.

## Hooks

`.claude/settings.hooks.json` is a mergeable fragment, **not** `.claude/settings.json`
itself — the generator never writes that file (see "Not generated" below). Wiring the
generated hooks in is an explicit, separate step:
`node scripts/apply-hook-settings.mjs --apply` (`../../workflow/hooks-policy.md`).

## Not generated

`.claude/commands/*` (7 hand-maintained workflow commands), `.claude/docs`,
`.claude/tasks`, `.claude/logs`, `.claude/settings*.json` — project state, never touched
by the generator.
