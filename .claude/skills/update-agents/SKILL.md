---
name: update-agents
description: >-
  Update the vendored agent library (.agent-system/) in this project to a newer release. Run it yourself as
  /update-agents (preview + changelog) and /update-agents --apply (update).
disable-model-invocation: true
# No model/effort override: see agent-stats — a different model re-caches the whole conversation.
argument-hint: "[--apply] [--to <version>]"
allowed-tools: Bash(node .agent-system/scripts/update-agent-system.mjs *)
x-generated-from: .claude/skills/update-agents/SKILL.md
---

# Update agents

```!
node .agent-system/scripts/update-agent-system.mjs --project . $ARGUMENTS 2>&1 || true
```

Reply with the output above exactly as it is, in a code block, then at most two short lines:

1. without `--apply` and with a newer version: run `/update-agents --apply` when no feature work is
   in flight (the update replaces agent definitions); with `--apply`: it commits nothing — review
   `git diff --stat`, commit, and start a new session;
2. if the output lists new hooks, repeat the enabling command it printed.

Do not call any tools and do not read other files.
