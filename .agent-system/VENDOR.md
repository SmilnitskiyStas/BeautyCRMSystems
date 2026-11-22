# Vendored agent library

Version: **2.6.1**
Source: github.com/SmilnitskiyStas/AI_agent_only_v2 @ `2bfe3e05ec73844a5e39ea64cff3c68c11c80f2a`
Vendored: 2026-11-22

Do not edit anything in this directory — it is replaced on every update.
Project agents, skills and overrides live in `.ai/agents/`, `.ai/skills/`, `.ai/agent-overlay.yaml`.

Update:

```
node <path-to-library>/scripts/vendor-into.mjs .
node .agent-system/scripts/sync-agents.mjs --project .
node .agent-system/scripts/validate-config.mjs --project .
```
