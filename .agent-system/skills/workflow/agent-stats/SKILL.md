---
name: agent-stats
description: >-
  Show subagent usage analytics — which agents ran, how many times, tokens spent, cache share
  and duration. Run it yourself as /agent-stats [--since 7d] [--agent <id>].
disable-model-invocation: true
# No model/effort override on purpose: a different model has its own prompt cache, so a turn
# switched to Haiku re-sends and re-caches the whole conversation (measured: 694k cache-write
# tokens in a long session). Staying on the session model reads the warm cache instead.
argument-hint: "[--since 7d|24h] [--agent <id>]"
allowed-tools: Bash(node .claude/hooks/lib/report-agent-runs.mjs *)
---

# Agent stats

Report from `.ai/logs/agent-runs.jsonl` (written by the `agent-run-log` hook):

```!
node .claude/hooks/lib/report-agent-runs.mjs $ARGUMENTS 2>&1 || true
```

Reply with the report above exactly as it is, in a code block, and then at most three short
lines:

1. where the tokens actually went — compare the subagent total with the "Main session" section
   (the main session usually dominates);
2. one concrete saving to try: a large "Context carried" means handoff-session + `/clear`;
   several models in one session means stop switching models; otherwise an agent with high
   Avg/run or low Cache%;
3. plan limits and dollar cost are in `/usage`, not here.

If the report says there is no run log, say how to enable it:
`node scripts/apply-hook-settings.mjs --apply --local --only agent-run-log`.

Do not call any tools and do not read other files.
