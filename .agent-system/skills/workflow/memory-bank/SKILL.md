---
name: memory-bank
description: >-
  Project working memory in .ai/memory/ — active context, inbox, progress log. Use when the user says
  "memory bank", "init memory bank", "status", "where are we", "what's the context", "remember/log this",
  "reflect", or "archive/close this phase", or when a milestone is reached and the active context is stale.
allowed-tools: Bash(node .claude/hooks/lib/memory-bank.mjs *)
---

# Skill: Memory bank

Working memory that survives `/clear`, compaction and new sessions. It extends `.ai/memory/`; it is
not a second memory system. Policy: `workflow/memory-bank.md`.

| File | Holds | Rule |
|---|---|---|
| `.ai/memory/activeContext.md` | current focus, next action, blockers, key files | overwrite, ≤ 40 lines; injected at session start |
| `.ai/memory/inbox.md` | raw notes and ideas | triage, then delete |
| `.ai/memory/progress.md` | milestones, one line each | append only |
| `.ai/memory/architecture-decisions/` | decisions | via `create-adr`, never here |

Only confirmed information goes in (code, a human decision, an accepted ADR, finished work). Never
invent goals, context or decisions. Keep entries as pointers (task id, path, commit), not pasted content.

## Commands (infer from the user's words)

- **init** — run `node .claude/hooks/lib/memory-bank.mjs init`. It creates only missing files. If
  `activeContext.md` is still the empty template, ask the user for the current goal in one question, then
  fill it in and **delete the `unset` marker line**. Do not interview the user beyond that.
- **status** ("where are we", "what now") — run `node .claude/hooks/lib/memory-bank.mjs status`, answer in
  at most five lines: focus, next action, blockers, inbox size. Name the files you relied on.
- **focus / update** — rewrite `activeContext.md` (overwrite, do not append). Update `## Updated` with the date.
- **log** ("remember this", "note") — append one line to `inbox.md`. A *decision* goes to `create-adr`; a
  *fixed defect* goes to `resolve-known-issue`; a *milestone* goes to `progress.md`.
- **reflect** — after meaningful work: one line per lesson. A reusable rule goes to
  `.ai/memory/implementation-patterns.md`, a dead end to `.ai/memory/rejected-approaches.md` (create if missing).
- **archive** ("close this phase") — summarize the outcome in `progress.md`, then run
  `node .claude/hooks/lib/memory-bank.mjs archive <slug>`; it moves the active context to
  `.ai/memory/archive/` and resets the file.

## When to update without being asked

At a milestone (task done, decision made, plan changed) and before `/clear` — see `handoff-session`.
Do not log every turn. Re-read the target file first if another session may have edited it, and merge narrowly.

## If the script is missing

Create the three files by hand with the headings above; behaviour is the same.
