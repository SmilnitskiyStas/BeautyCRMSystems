<!--
  Shared agent definition template. Copy to agents/<category>/<id>.md.
  Frontmatter is validated by schemas/agent.schema.json. The body below the frontmatter
  is the human-readable instruction that adapters carry into .claude/agents/*.md and
  .codex/agents/*.toml verbatim (minus the generated header).
  Delete this comment.
-->
---
id: <kebab-case-id>            # must equal the file name
category: <control|engineering|product|documentation>
version: 1
summary: <one sentence — what this role does; feeds auto-delegation>

responsibilities:
  - <narrow outcome this role owns>
  - <...>

must_not:
  - <hard guardrail — never done even if asked>
  - <...>

default_model_tier: <cheap|standard|reasoning>
allowed_model_tiers: [cheap, standard, reasoning]

risk_escalation:               # optional — only list risks that change the tier
  security: reasoning
  destructive_data_change: reasoning

required_context:              # named slices, not file paths (see workflow/context-policy.md)
  - project-rules
  - current-task
  - relevant-requirements

produces:                      # code | tests | task-log | <ARTIFACT_TYPE from artifact.schema.json>
  - code
  - tests
  - task-log

handoff_targets:               # agent ids this role may hand to directly
  - project-manager

tools: [read, search, edit, shell, run-tests]   # optional — omit to inherit runtime default
skills: []                     # skill ids under skills/
read_only: false
# when_to_use: <disambiguation vs an overlapping role>
---

# <Role Name>

## Role

<2-4 sentences: what this role is responsible for, and the boundary with adjacent roles.>

## How this role works

- Follows the standard agent procedure (see `workflow/` — context-policy, then implement
  only the assigned responsibility, then task-log, then handoff if needed).
- <role-specific method notes>

## Guardrails

<expand each `must_not` with the reasoning, so the constraint survives paraphrasing.>

## Skills to use

- `skills/<domain>/<skill>` — <when>
