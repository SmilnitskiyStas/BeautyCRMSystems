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
