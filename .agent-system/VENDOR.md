# Vendored agent library

Source: github.com/SmilnitskiyStas/AI_agent_only_v2 @ `c11256e3847006e1206e4bcd6b238c3cad7d8bfd`
Vendored: 2026-11-01

Do not edit anything in this directory — it is replaced on every update.
Project agents, skills and overrides live in `.ai/agents/`, `.ai/skills/`, `.ai/agent-overlay.yaml`.

Update:

```
node <path-to-library>/scripts/vendor-into.mjs .
node .agent-system/scripts/sync-agents.mjs --project .
node .agent-system/scripts/validate-config.mjs --project .
```
