# Changelog

Versioning: semver. `VERSION` is the single source; every release gets a `## <version>` heading
here (checked by `doctor`) and a git tag `v<version>`.

- **major** — breaking change to the shared agent/skill/hook format or to project extension files
  (`.ai/agents`, `.ai/skills`, `.ai/agent-overlay.yaml`).
- **minor** — new agents, skills, hooks, scripts or policies; generated output changes.
- **patch** — fixes that do not change what a consuming project must do.

## 2.6.1

- Fix: sync-agents failed (`SKILL.md has no frontmatter`) and `--check` reported false drift on Windows checkouts with `core.autocrlf` (CRLF files). Sources and generated files are compared LF-normalised.

## 2.6.0

- Codex `frontier` tier now maps to `gpt-6.2-astra` **medium** (was xhigh, which exhausted plan limits fast).
- Rule `model-budget`: cost ladder luna low → sol medium → sol high → astra medium → astra high; xhigh/max/ultra only on explicit user request. Synced into a project's `AGENTS.md` between `<!-- model-budget:start/end -->` markers (opt-in, like the roster block).

## 2.5.1

- Fix `codex-usage.mjs`: a subagent rollout embeds its parent's `session_meta`; the first one is now the identity (custom agents like `frontend-developer` were being counted as `main`).

## 2.5.0

- `codex-usage.mjs` (in `.codex/hooks/lib/`): token usage of a project's Codex sessions from Codex's own
  rollout logs — by agent role, model/effort, day or session (`--since`, `--json`). Codex has no
  SubagentStop hook, so this is the Codex counterpart of `/agent-stats`.

## 2.4.1

- `runtime_hints.effort` overrides the tier's reasoning effort per agent (Claude `effort`, Codex
  `model_reasoning_effort`). `project-architect` runs at `medium` instead of `high` to cut thinking
  output; raise it per task for hard architecture work. `security-reviewer` stays `high`.

## 2.4.0

- Rules: `workflow/rules/<id>.md` text can be injected into an agent via `include_rules` in its
  frontmatter (Claude `.md` body and Codex `developer_instructions`); overlay `include_rules: []`
  turns it off per project; `validate-config` fails on unknown rule ids.
- Rule `minimal-change` (reuse-first, least new code, with explicit "never cut" safety list) enabled
  for backend/frontend/database/mobile/fullstack developers. Measure with `/agent-stats --since`.

## 2.3.0

- Codex adapter, verified live on codex-cli 0.171.0: model map moved to `gpt-6.2-sol` / `gpt-6.2-astra` /
  `gpt-6-luna` (0.154.0 rejects them — `doctor` warns on old CLIs); hooks → `.codex/hooks/**` +
  inert `.codex/hooks.available.json` (opt in: `apply-hook-settings.mjs --codex --apply --only <ids>`);
  skills → `.agents/skills/`; hook `block()` emits a JSON deny for Codex (exit 2 is ignored there);
  `codex_status` per hook and `codex_matcher` in the hook schema.
- `/agent-stats`: new columns Think% (thinking share of output) and Out/call, to measure output/effort changes.

## 2.2.0

- Update flow: `session-context` shows `update <version> available → /update-agents` when a newer
  `v<semver>` tag exists (24 h cache in `.ai/state/update-check.json`, refreshed by a detached background
  process — session start is never delayed; off: `update_check: off`).
- `/update-agents` (user-only) previews the new release and its changelog; `/update-agents --apply` runs
  vendor → sync → validate and prints new hooks to enable. It touches only generated paths, refuses to
  overwrite local edits there and commits nothing. Script: `.agent-system/scripts/update-agent-system.mjs`.
- `scripts/release.mjs <patch|minor|major>`: promotes `## Unreleased`, bumps `VERSION`, runs all checks,
  commits, tags and pushes.
- Plugin builds exclude `update-agents`.

## 2.1.0

- Versioning: `VERSION`, this changelog, `node scripts/version.mjs [--latest]`, and a stamp
  (`.claude/agent-system.json`, `.codex/agent-system.json`) in generated output so a project can tell
  which library version produced it. `vendor-into` records version + commit in
  `.agent-system/.vendor.json`. `doctor` checks version and changelog.
- Memory bank as an extension of `.ai/memory/`: `activeContext.md`, `inbox.md`, `progress.md`;
  skill `memory-bank` (init / status / log / reflect / archive); `workflow/memory-bank.md`.
- Hook `session-context` (SessionStart `startup|resume|clear`): shows `agent-system <version>` to the
  user and injects the active context (capped) into the session. `compact-restore` also re-injects it.
- `context-manager` and `handoff-session` read/write the active context.
- Plugin builds use the library version instead of `0.0.0-test.*`.

## 2.0.0

- Vendor-neutral shared library (agents, skills, workflow, schemas) with Claude and Codex adapters,
  model tiers (incl. `frontier`), control agents, hooks, profiles, project extensions, vendoring,
  worktree integration, run analytics (`/agent-stats`).
