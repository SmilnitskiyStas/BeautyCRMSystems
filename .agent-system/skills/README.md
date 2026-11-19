# skills/

Skills are the **how** — one concrete competence each. Three kinds (spec §36):

## A. Shared methodology — `skills/workflow/`, `skills/shared/`

Runtime-independent procedures. Referenced by `workflow/*.md` policies and by agent
`skills:` fields.

| Skill | Purpose | Source |
|---|---|---|
| `workflow/context-loading` | assemble a minimum-sufficient context package | `.claude/skills/workflow/context-loader.md` |
| `workflow/create-handoff` | write a handoff record + run the Interaction Gate | `.claude/skills/workflow/create-handoff.md` |
| `workflow/create-task-log` | write a task log | `.claude/skills/workflow/create-task-log.md` |
| `workflow/update-task-status` | move a task through the lifecycle | `.claude/skills/workflow/update-task-status.md` |
| `workflow/resolve-known-issue` | record root cause + prevention | `.claude/skills/workflow/resolve-known-issue.md` |
| `workflow/code-review` | independent review procedure | `.claude/skills/workflow/review-completed-task.md` |
| `workflow/create-adr` | write an ADR | *new (Phase 3)* |
| `workflow/decompose-spec` | spec -> task DAG | *new (Phase 3)* |
| `workflow/create-contract` | produce / version an API_CONTRACT artifact | *new (Phase 3)* |
| `workflow/root-cause-analysis` | structured RCA before a fix | *new (Phase 3)* |
| `shared/engineering-standards` | stack-agnostic engineering standards | `.claude/skills/personal-engineering-standards/SKILL.md` (minus stack bits) |
| `shared/add-validation` | validate-at-the-boundary pattern | merge of `.claude/skills/backend/add-validation.md` + `.claude/skills/fullstack/add-validation.md` |

## B. Domain skills — `skills/<domain>/`

Move as-is from `.claude/skills/<domain>/` (Phase 3):
`accessibility`, `backend`, `copywriting`, `cro`, `database`, `frontend`, `qa`,
`security`, `seo`, `ui-ux-design`, plus new `mobile/` (Phase 4).

Stack-neutral where possible; the framework specifics come from a stack pack.

## C. Stack packs — `skills/stacks/<stack>/`

Everything tied to one framework / platform. `skills/stacks/nextjs-marketing/` collects:

- `.claude/skills/nextjs-marketing-architect/SKILL.md`;
- the Next.js-specific parts of `frontend/` and `fullstack/` skills;
- the marketing-site-specific parts of `product-design/`, `seo/`, `cro/`, `copywriting/`.

A project selects one stack pack. The role definitions in `agents/` stay stack-neutral
and reference domain skills; the stack pack supplies the framework detail.

## Format

Each skill is a directory with `SKILL.md` (YAML frontmatter `name` + `description`, then
the procedure). This is the common denominator both Claude Code and Codex discover. The
adapters expose `skills/` into `.claude/skills/` and `.codex/skills/`.

## Physical move

Phases 0-2 leave `.claude/skills/` untouched so the current system keeps working. Phase 3
performs the `git mv` into this tree as part of the adapter cutover, and this README's
"source" columns become history.
