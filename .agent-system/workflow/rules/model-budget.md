## Rule: model budget

Usage limits are one shared, finite budget. Use the cheapest model + effort that can do the task, and climb ONE step at a time:

1. `luna` low — mechanical edits, lookups, docs sync
2. `sol` medium — ordinary coding (default)
3. `sol` high — hard debugging, concurrency, money flows, migrations
4. `astra` medium — architecture, cross-system design
5. `astra` high — only after step 4 failed on this same task
6. xhigh / max / ultra — never self-chosen; only when the user asks for it by name

- Climb only after a concrete failure (failing test, wrong result), never on a hunch. Say the step and why in one line before using step 4 or higher.
- When spawning a subagent, prefer a named roster agent (`.codex/agents`) over the generic `worker`; its model and effort are already set. Do not pass `model` / `effort` unless climbing a step, and never xhigh / max / ultra.
- At most one `astra` agent at a time; do not fan out parallel frontier agents.
- A subagent that needs more than its configured model stops and reports; it does not escalate itself.
