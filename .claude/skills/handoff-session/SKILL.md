---
name: handoff-session
description: >-
  End a work session cleanly before /clear or a long break by writing a compact handoff so the next session can resume without re-reading everything. Use when a session is ending, the context is getting full, or the user is stepping away from an in-progress task.
x-generated-from: .claude/skills/handoff-session/SKILL.md
---

# Skill: Handoff Session

## When
- Before running `/clear` or `/compact`, or before a break after which the current context is not worth keeping.
- The task is in progress, blocked, or waiting on a human decision.

## Steps
1. Summarize state in 15 lines or fewer: task ID and status; decisions made, each with its why; files changed; open questions and blockers; the exact next action.
2. Overwrite `.ai/state/handoff.md`. Follow the fields of `.claude/templates/handoff-template.md` where they apply (Task, What was completed, What to do next, Important context, Risks / Blockers, Files to review, Definition of done).
3. Tell the user they can now run `/clear`. This skill cannot run `/clear` or `/compact` itself; they are built-in commands.
4. In the next session, read `.ai/state/handoff.md` first instead of whole task logs.

## Validation
- A stranger could resume from the file alone.
- No pasted file contents; reference paths only.

## Escalation
- If a human-gate decision is pending, put it first in the handoff, above the summary, and tell the user it needs an answer before work continues.
