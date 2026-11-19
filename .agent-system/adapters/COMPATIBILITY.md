# Runtime compatibility matrix

What each runtime supports for the v2 agent system, from **verified** behavior — not
assumptions (spec §8). Re-verify on a runtime upgrade.

**Verified:** 2026-08-28 · Claude Code `2.1.197` · Codex CLI `codex-cli 0.144.1` · Node 22.16.

| Capability | Claude Code | Codex CLI | Fallback when absent |
|---|---|---|---|
| Native named subagents (files) | yes — `.claude/agents/<id>.md` (YAML frontmatter) | yes — `.codex/agents/<id>.toml` + `~/.codex/agents/`; feature `multi_agent` = stable | instruction routing in the root instructions file |
| Automatic delegation from a description | yes | yes (instruction-based / explicit; proactive on higher tiers) | the orchestrator picks per the Agent→Task table |
| Per-agent model | yes — `model:` (`sonnet\|opus\|haiku\|fable\|inherit\|<id>`) | yes — `model` + `model_reasoning_effort` (`low\|medium\|high`) | model-tier recommendation in the body |
| Per-agent effort / reasoning | yes — `effort:` (`low\|medium\|high\|max`) | yes — `model_reasoning_effort` | omit; use the tier default |
| Agent-local tool restriction | yes — `tools:` / `disallowedTools:` | yes — `sandbox_mode` (`read-only\|workspace-write\|danger-full-access`) | project permission settings |
| Read-only agents | yes — `disallowedTools: Write, Edit` | yes — `sandbox_mode: read-only` | body guardrail only |
| Nested / parallel subagents | yes — depth 3 (`CLAUDE_CODE_MAX_SUBAGENT_SPAWN_DEPTH`) | yes — `[agents] max_concurrent_threads_per_session` (default ~6) | sequential DAG execution |
| Skills — preload | yes — `skills:` frontmatter | via `developer_instructions` pointing at skill files; `~/.codex/skills/` discovery | inline the procedure in the body |
| Skills — runtime discovery | yes — `Skill` tool | yes — skill discovery + `skill_mcp_dependency_install` (stable) | reference by path |
| Built-in read-only explore agent | yes — `Explore` (and `Plan`) | yes — built-in `explorer` (plus `default`, `worker`) | `project-researcher` runs the retrieval manually |
| Hooks | yes — `hooks/*.md` compiles to `.claude/hooks/**` + `.claude/settings.hooks.json` (`scripts/apply-hook-settings.mjs --apply` to wire in) | feature `hooks` = stable per Codex docs, **not yet verified** against the binary — `adapters/codex/generate-hooks.mjs` deferred | `hooks/README.md`'s per-hook fallback (self-check / manual review / permission rules) |
| Root project instructions | `CLAUDE.md` (+ nested) | `AGENTS.md` (+ nested + `~/.codex/AGENTS.md`), keep < 150 lines | required — always present |
| Rules / command allowlist | `settings.json` `permissions` | `~/.codex/rules/*.rules` (`prefix_rule(...)`), `/permissions` | manual approval |
| Native artifact bus | no (v2 uses a `.ai/artifacts/` convention) | experimental — feature `artifact` = under development, **not used** | the `.ai/artifacts/` convention + `schemas/artifact.schema.json` |
| MCP servers per agent | yes — `mcpServers:` | yes — `mcp_servers` table | project-level MCP config |

## Model tiers → concrete models

Resolved only in `adapters/<runtime>/model-map.yaml`. Current values:

| Tier | Claude (`claude` 2.1.197) | Codex (`codex` 0.144.1) |
|---|---|---|
| `cheap` | `haiku`, effort `low` | `gpt-5.6-luna`, reasoning `low` |
| `standard` | `sonnet`, effort `medium` | `gpt-5.6-terra`, reasoning `medium` |
| `reasoning` | `opus`, effort `high` | `gpt-5.6-sol`, reasoning `high` |

Codex model ids from `~/.codex/models_cache.json`: `gpt-5.6-sol`, `gpt-5.6-terra`,
`gpt-5.6-luna`, `gpt-5.5`, `gpt-5.4`, `gpt-5.4-mini`. Codex model names change roughly per
release (see `notice.model_migrations` in `~/.codex/config.toml`) — re-verify at Phase 6
and on upgrades.

## Known gaps / degradations

- **Codex hooks unverified**: the row above reflects Codex's own claim of a stable
  `hooks` feature, not a binary-verified event/config shape (unlike `model-map.yaml`,
  which is re-checked per runtime upgrade). Every hook in `hooks/` is Claude-only until
  someone confirms the real Codex hook surface and `adapters/codex/generate-hooks.mjs`
  is written against it.
- **Codex frontmatter drift**: `multi_agent_v2` is under development. The `.codex/agents/`
  TOML schema (`name`, `description`, `developer_instructions`, `model`,
  `model_reasoning_effort`, `sandbox_mode`, `mcp_servers`) is confirmed against the
  0.144.1 binary but may gain fields.
- **Claude `effort:` field**: present in the 2.1.197 agent config schema; unknown
  frontmatter keys are silently dropped, so emitting it is safe on older builds.
