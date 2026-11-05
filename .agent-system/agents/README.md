# agents/

The **source of truth** for every role. One file per role:
`agents/<category>/<id>.md` — YAML frontmatter (validated by `schemas/agent.schema.json`)
plus a project-agnostic instruction body.

Adapters generate the runtime form from these:
`adapters/claude` -> `.claude/agents/<id>.md`, `adapters/codex` -> `.codex/agents/<id>.toml`.
Never edit the generated files.

## Categories

| Category | Roles |
|---|---|
| `control/` | task-intake, requirements-analyst, project-researcher, context-manager, project-architect, project-manager, review-orchestrator, integration-steward |
| `engineering/` | frontend-developer, backend-developer, fullstack-developer, database-engineer, mobile-developer, devops-engineer, qa-engineer, security-reviewer |
| `product/` | product-designer, ui-ux-designer, accessibility-specialist, seo-specialist, cro-specialist, copywriter |
| `documentation/` | documentation-writer |

*(control-plane agents task-intake / requirements-analyst / project-researcher /
context-manager / review-orchestrator and engineering/mobile-developer are added in
Phase 4.)*

## What belongs in a role definition

- **Durable role outcomes** — what this role owns in any project, any stack.
- **Guardrails** (`must_not`) — spelled out so they survive paraphrasing.
- **Model tier** and per-risk escalation.
- **Handoff targets** and produced artifacts.

## What does NOT belong here

- Stack or framework specifics (Next.js, ASP.NET, Expo, Postgres, ...) — those live in
  `skills/stacks/<stack>/` and are selected per project.
- Project names, spec file names, brand variables — those live in `CLAUDE.template.md` /
  `AGENTS.template.md`.
- Process rules — those live in `workflow/*.md`; reference them, do not restate them.

## Overlapping roles

`fullstack-developer` is an **alternative** to `backend-developer` + `database-engineer`,
not an addition. A project picks one shape:

- light backend colocated with the frontend (server actions / route handlers, small data
  model) -> `fullstack-developer`;
- separate backend service and/or large data model -> `backend-developer` +
  `database-engineer`.

`when_to_use` in each definition states this.
