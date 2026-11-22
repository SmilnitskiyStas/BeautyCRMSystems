# workflow/rules/

Short rules that are **injected into the generated body** of the agents that list them in
`include_rules:` (agent frontmatter, or per project via `.ai/agent-overlay.yaml`). Unlike the policies
in `workflow/*.md` — which an agent has to be told to read — a rule costs no file read, but its text is
part of every spawn of that agent, so keep each one short. `include_rules: []` in an overlay turns a rule off.

| rule | for | what it does |
|---|---|---|
| `minimal-change` | implementers (backend, frontend, database, mobile, fullstack) | least new code, reuse first, no pasted code in replies |

`minimal-change` follows the principle of the MIT-licensed [Ponytail](https://github.com/DietrichGebert/ponytail)
("the best code is the code you never wrote"), rewritten for this workflow: it never overrides contracts, ADRs,
required tests or human gates. Measure its effect with `/agent-stats` (`Out/call`, `Output`) using `--since <date>`
before and after.

- `model-budget` — cost ladder for model/effort choice (Codex orchestrator). Delivered through the `AGENTS.md` `model-budget` marker block, not `include_rules`.
