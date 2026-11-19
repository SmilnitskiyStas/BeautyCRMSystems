# MIGRATION.md

How the repository changed from v1 (Claude-only prose personas) to v2 (vendor-neutral
shared library + adapters), and what a consuming project must do.

Full rationale: `AI_AGENT_ONLY_V2_TECHNICAL_SPEC.md`. Phase-by-phase audit:
`MIGRATION_AUDIT.md`.

## Status by phase

| Phase | What | State |
|---|---|---|
| 0 | Audit | done — `MIGRATION_AUDIT.md` |
| 1 | Shared schema, workflow policies, templates | done — `schemas/`, `workflow/`, `templates/` |
| 2 | Migrate 16 agents to shared defs | done — `agents/**` |
| 3 | Claude adapter + generator + skills reorg | done — `scripts/`, `adapters/claude/`, `skills/**` |
| 4 | Control-plane agents + mobile-developer | done — 5 control agents + `mobile-developer` |
| 5 | Artifacts + DAG + Interaction Gate wiring | policies + schemas in Phase 1; `.ai/` conventions pending |
| 6 | Codex adapter | done — `adapters/codex/`, `.codex/agents/*.toml` |
| 7 | Metrics + template consolidation | done — task-log metrics, `report-agent-usage.mjs`, `templates/` |

## What moved

| v1 | v2 |
|---|---|
| `.claude/agents/<role>.md` (prose, no frontmatter) | **source:** `agents/<category>/<id>.md` (frontmatter + body) → **generated:** `.claude/agents/<id>.md` |
| `.claude/skills/<domain>/<skill>.md` (flat, no frontmatter) | **source:** `skills/<domain>/<skill>/SKILL.md` (frontmatter) → **generated:** `.claude/skills/<slug>/SKILL.md` |
| `.claude/skills/personal-engineering-standards/` | `skills/shared/engineering-standards/` |
| `.claude/skills/nextjs-marketing-architect/` | `skills/stacks/nextjs-marketing/` |
| `.claude/skills/backend/add-validation.md` + `.claude/skills/fullstack/add-validation.md` | merged → `skills/shared/add-validation/` |
| `.claude/commands/{16 persona}.md` | **removed** — native auto-delegation + `@agent` + manual override replace them |
| `.claude/commands/{7 generic}.md` | kept (hand-maintained) |
| process rules inside `CLAUDE.template.md` | `workflow/*.md` (referenced, not restated) |
| `.claude/templates/*` | `templates/` (consolidated in Phase 7; `decision-template` → `adr-template`) |

## Removed commands

`/accessibility /architect /backend /copywriting /cro /database /devops /docs /frontend
/fullstack /pm /product-design /qa /security /seo /ui-ux-design`

Replacement: describe the task and let Claude Code delegate, or name the agent
(`@backend-developer` / "use the backend-developer subagent"). Manual tier/agent override
still works (`workflow/model-routing.md`).

## New: build tooling

- `node scripts/sync-agents.mjs` — regenerate `.claude/` from `agents/` + `skills/`.
- `node scripts/sync-agents.mjs --check` — CI drift check (non-zero on drift).
- `node scripts/validate-config.mjs` — schema + cross-reference validation.
- `node scripts/doctor.mjs` — environment + config health.

Zero dependencies, Node ≥ 18.

## For a consuming project

1. Run `node scripts/sync-agents.mjs` in this repo.
2. **Claude Code:** copy `.claude/agents/`, `.claude/skills/`, `.claude/commands/`;
   copy `CLAUDE.template.md` → `CLAUDE.md` and fill the project sections.
   **Codex CLI:** copy `.codex/agents/`; copy `AGENTS.template.md` → `AGENTS.md`
   (keep the `<!-- roster:start/end -->` markers so the roster stays synced); add
   `[agents]\nenabled = true` to `config.toml`.
3. Copy `workflow/`, `schemas/`, `templates/` alongside (agents reference them by path).
4. Pick a stack pack (`skills/stacks/`) and the backend-shape variant (fullstack-developer
   OR backend-developer + database-engineer); delete the unused agent files.
5. Create runtime state dirs: `.claude/docs/`, `.claude/tasks/`, `.claude/logs/`, and
   `.ai/artifacts/`, `.ai/memory/`.
6. Add `.claude/settings.local.json` (and Codex `~/.codex/rules/`) with the project's real
   commands.

## Backward compatibility

- The v1 `.claude/agents/*` prose files are replaced by generated equivalents with the
  same ids and preserved responsibilities/guardrails.
- The 7 generic commands still work; internal skill-path references were updated to skill
  names.
- No project data format changed. Task logs, handoffs, known-issues keep their shape
  (now also covered by `schemas/`).

## Updating a project that already vendors the library

Keep the vendored copy pristine and put everything project-specific outside it, so an update
never overwrites your work.

```
<project>/
  .agent-system/             vendored library — NEVER edit by hand
  .ai/agents/<category>/<id>.md    project agents (same format as agents/); same id as a library agent = override
  .ai/skills/<path>/SKILL.md       project skills (stack packs, domain skills)
  .ai/agent-overlay.yaml           per-agent additions to library agents
  .ai/agent-profile                roster profile name (written by --profile)
```

`.ai/agent-overlay.yaml`:

```yaml
agents:
  frontend-developer:
    skills_add: [stacks/my-stack/create-react-component]   # discoverable on demand
    preload_add: []                                         # injected in full — keep 0-2
    runtime_hints: { max_turns: 120 }
    # any other agent.schema.json key may be overridden, e.g. default_model_tier
```

An overlay naming an unknown agent, or producing a schema-invalid agent, fails the sync.
Project agents and skills are never removed by a roster `--profile`.

Update flow — from a checkout of the library (`git pull` there first):

```bash
node <library>/scripts/vendor-into.mjs <project>      # replaces <project>/.agent-system/ (first time too)
cd <project>
node .agent-system/scripts/sync-agents.mjs --project . [--profile backend-service]
node .agent-system/scripts/validate-config.mjs --project .
```

`vendor-into` copies only what the generator needs (no generated `.claude/`, no tests/docs) and
removes files the library dropped. Do not use `git subtree` for this: it would also import the
library's own `.claude/`, which Claude Code can discover as duplicate skills and agents. And never
`git pull` the library repo into a project: the histories are unrelated and its directories would be
merged into the project root.

Overlay extras: `exclude_agents: [id, ...]` drops library roles the project does not use (an unknown
id fails the sync).
