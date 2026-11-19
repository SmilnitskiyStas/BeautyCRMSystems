# Context policy

Minimum sufficient context. Never load the whole repository, the whole spec, all docs,
all task logs, or the previous agent's entire history (spec §12).

The `context-manager` agent assembles a **Context Package** (see
`schemas/context-package.schema.json`, `templates/context-package-template.md`) per task.
Everything below is what it applies.

## What every task needs ("Always")

- The project root rules (`CLAUDE.md` / `AGENTS.md`).
- What is currently in progress (the task store's current view).
- Relevant resolved entries from `known-issues.md` — retrieved by area/keyword, **not** the
  whole file read top to bottom (spec §32). As the file grows, this is retrieval, not a
  linear read.

## Domain context

- The **relevant section(s)** of the project master spec — never the whole document each
  time.
- The assigned agent's `required_context` slices, resolved against `source-of-truth.md`.

## Architecture & living docs (only when the task touches them)

- Architecture / domain-model / content-model docs.
- Accepted ADRs — always before an architectural decision.
- Task-specific docs: API contracts for Server-Action/endpoint work, DB schema for data
  work, SEO map for content work, and so on.

## Recent history (thin)

- The last 2-3 handoffs.
- The current task's own log, if it exists.

## Context levels (spec §12)

```yaml
cheap_task:        { target: small }
standard_task:     { target: medium }
architecture_task: { target: larger_but_curated }
```

Do not hardcode token limits without checking the current model's real window.

## Prefer references over copies (spec §13)

A compact package points at the source:

```yaml
sources:
  - path: src/orders/OrderService.cs
    symbol: CreateReturn
    purpose: current return flow
  - path: docs/decisions.md
    anchor: ADR-017
    purpose: payment idempotency rule
```

The agent reads the source on demand.

## When context grows (spec §12)

Apply in order:

1. summarize;
2. replace inlined content with source references;
3. extract stable results into artifacts;
4. evict old, now-irrelevant context;
5. start a fresh subagent / session;
6. escalate to a human if growth is abnormal.

## Anti-patterns

- Starting work without the root rules.
- Re-reading the full master spec every task.
- Making an architectural decision without checking accepted ADRs.
- Stepping on a documented known issue because the file was not checked.
- Pasting a whole file when a symbol reference would do.

## Session commands (Claude Code) — which one to reach for

| Situation | Command | Why |
|---|---|---|
| Unrelated new task | `/clear` (`/rename` first) | Free — stale context is paid on every later message |
| Same task, context heavy | `/compact <what to keep>` | It reads the whole conversation once (a large request itself) — do it before a long idle, not after |
| Quick side question | `/btw <question>` | Answered without growing the conversation |
| Verbose side work (tests, logs, big search) | subagent or `/subtask <task>` | Only the summary returns |
| Wrong direction | `Esc`, then `/rewind` | Cheaper than arguing it back |
| Try an alternative | `/branch` or `/fork` | Original stays intact |
| Where did tokens go? | `/context`, `/usage` (press `f` to apply fixes), `/skill-doctor` | Measure before tuning |

Limits worth knowing: `/compact`, `/clear` and `/model` are built-ins — a skill, agent or
hook **cannot run them**. Hooks can only block compaction (`PreCompact`, exit 2) or
re-inject context after it (`SessionStart` with matcher `compact`). Tune the threshold with
`/autocompact <n>` or `autoCompactWindow`; put what must survive in the project's
`# Compact instructions` section (see `CLAUDE.template.md`). Set
`CLAUDE_CODE_SUBAGENT_EFFORT` rather than leaving subagents at the session's effort.

## Do not switch the model for a cheap command in a long session

Every model has its own prompt cache. A skill/command with a `model:` override (or `/model`
mid-session) makes that turn re-send and re-cache the **entire** conversation on the other
model. Measured on a ~700k-token session: one `model: haiku` command wrote 694k tokens to
Haiku's cache; switching Sonnet 5 -> 5.5 mid-session cost another 2.3M cache-write tokens.
Per-agent `model:` is fine — subagents start with a fresh, small context. For commands that
run inside the main conversation, stay on the session model.
