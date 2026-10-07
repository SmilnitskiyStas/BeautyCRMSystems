# templates/

Fill-in-the-blank skeletons. A template is a skeleton, not documentation — keep them
terse. The reasoning behind a field belongs in the matching `workflow/*.md` or
`schemas/*.json`.

## Process templates

| Template | For | Schema |
|---|---|---|
| `agent-template.md` | a new shared agent definition | `schemas/agent.schema.json` |
| `task-template.md` | a task in the DAG | `schemas/task.schema.json` |
| `task-log-template.md` | a completed task's log (+ model routing + metrics) | — |
| `handoff-template.md` | a handoff record | `schemas/handoff.schema.json` |
| `context-package-template.md` | context-manager output | `schemas/context-package.schema.json` |
| `artifact-template.md` | artifact metadata header | `schemas/artifact.schema.json` |
| `research-report-template.md` | project-researcher output | artifact `RESEARCH_REPORT` |
| `requirements-template.md` | requirements-analyst output | artifact `REQUIREMENTS` |
| `adr-template.md` | architecture decision record | artifact `ARCHITECTURE_DECISION` |
| `review-template.md` | independent review record | `schemas/review.schema.json` |
| `bug-report-template.md` | a bug report | — |
| `known-issue-template.md` | one known-issues.md entry | — |
| `known-issues-doc-template.md` | the whole known-issues.md skeleton | — |

## Content templates

`case-study-template.md`, `service-page-template.md`, `industry-page-template.md`,
`blog-post-template.md` — marketing-site content structures. Pair with
`skills/stacks/nextjs-marketing/`.

## Notes

- `adr-template.md` supersedes the old `decision-template.md` (removed in Phase 7).
- Templates live only here now (consolidated from `.claude/templates/` in Phase 7).
  A consuming project copies this directory; `.claude/` and `.codex/` do not get a
  generated copy.
