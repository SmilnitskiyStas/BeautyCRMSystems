# adapters/codex

Generates Codex CLI config from the shared source of truth.

## What it generates

| From | To | By |
|---|---|---|
| `agents/**/*.md` | `.codex/agents/<id>.toml` | `adapters/codex/generate.mjs` |
| `agents/**` (roster) | `AGENTS.md` block between `<!-- roster:start -->` / `<!-- roster:end -->` (only if `AGENTS.md` exists) | `generateRoster` |

Run `node scripts/sync-agents.mjs` (or `--target codex`, or `--check`).

Verified against `codex-cli 0.144.1`: custom agents live in `.codex/agents/*.toml`
(project) and `~/.codex/agents/*.toml` (personal); feature `multi_agent` is stable.

## Field mapping

| Shared | Codex TOML | Notes |
|---|---|---|
| `id` | `name` | identity Codex matches when spawning |
| `summary` (+ `when_to_use`) | `description` | agent selection guidance |
| `default_model_tier` | `model` | via `model-map.yaml` `tiers` |
| `default_model_tier` | `model_reasoning_effort` | via `model-map.yaml` `effort` (`low\|medium\|high`) |
| `read_only` / `tools` | `sandbox_mode` | `read_only` ⇒ `read-only`; has `edit`/`shell` ⇒ `workspace-write`; else `read-only`. Never `danger-full-access`. |
| body + `must_not` + tier + `skills` + `handoff_targets` | `developer_instructions` | Codex has no separate guardrail/skill fields, so they are appended as `## Never` / `## Skills` / `## Handoff` sections. Codex does not preload skills — they are listed by name for the agent to invoke. |

## model-map.yaml

The only place a Codex model name appears. Codex model ids change roughly per release
(`notice.model_migrations` in `~/.codex/config.toml`); re-verify on upgrade —
`node scripts/doctor.mjs` flags a missing tier.

## config.toml

Not generated. Add to the project or `~/.codex/config.toml` manually:

```toml
[agents]
enabled = true
```

## Not generated

`.codex/config.toml`, `~/.codex/*`, `AGENTS.md` outside the roster markers.
