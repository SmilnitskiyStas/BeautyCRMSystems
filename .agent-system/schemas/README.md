# schemas/

Vendor-neutral JSON Schemas (draft 2020-12) for the v2 shared layer. These are the
contract that `scripts/validate-config.mjs` enforces and that the adapters
(`adapters/claude`, `adapters/codex`) read from.

| Schema | Validates | Where the data lives |
|---|---|---|
| `agent.schema.json` | frontmatter of `agents/<category>/<id>.md` | this repo (source of truth) |
| `task.schema.json` | task blocks in the project task store | project runtime state |
| `handoff.schema.json` | handoff records | project runtime state |
| `artifact.schema.json` | artifact metadata headers | project `.ai/artifacts/` |
| `context-package.schema.json` | context-manager output | project runtime state |
| `review.schema.json` | independent review records | project runtime state |

Only `agent.schema.json` constrains files committed to this library. The rest describe
the shapes agents produce while working in a real project, and exist so templates,
policies, and the usage report stay consistent.

**Model tiers** are `cheap | standard | reasoning` everywhere. They never name a concrete
model — resolution happens only in `adapters/<runtime>/model-map.yaml`. See
`workflow/model-routing.md`.
