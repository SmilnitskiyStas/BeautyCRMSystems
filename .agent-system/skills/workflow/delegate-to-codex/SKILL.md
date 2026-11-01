---
name: delegate-to-codex
description: >-
  Dispatch a narrow, independent task to Codex CLI in an isolated git worktree, then
  review and merge what comes back. Use when a task is a good candidate for a second,
  differently-biased implementation and the human confirms delegation.
---

# Delegate to Codex

## When

An independent, well-specified, parallel-ready task (`task-dag.md`) that doesn't touch
files the current session is also editing — not an ambiguous task (send that through
`requirements-analyst` first), not one already blocked on a human gate.

## Steps

1. Draft the task text Codex will receive: goal, acceptance criteria, files it's
   expected to touch, anything it must not do. Save it (e.g. under a scratch path or
   `.ai/logs/delegations/<slug>-task.md`).
2. Propose to the human, per `workflow/cross-runtime-delegation.md`: the task text, the
   worktree/branch name, the sandbox mode. **Do not dispatch without confirmation** —
   this is the semi-automatic gate.
3. On confirmation, run in the background (Bash tool, `run_in_background: true`):
   ```
   node scripts/dispatch-codex.mjs start --slug <name> --task-file <path> --sandbox workspace-write
   ```
4. Continue other work. Check in with
   `node scripts/dispatch-codex.mjs status --slug <name>` rather than blocking on it.
5. When status is `done` (or `failed`), run
   `node scripts/dispatch-codex.mjs collect --slug <name> --full` and actually read the
   diff — this is the independent review (`review-policy.md`); do not treat "codex
   exited 0" as success on its own.
6. Report to the human: what Codex did, your own review findings, and whether you
   recommend merging. Merging `codex/<slug>` is always a separate human confirmation
   (`human-gates.md`) — never do it unprompted, even for a clean diff.
7. After merge or discard, run
   `node scripts/dispatch-codex.mjs cleanup --slug <name>` (add `--force` only to
   discard unmerged work, and say so explicitly first).

## Validation

- The task text was specific enough that Codex didn't have to guess a product/business
  decision — if it did, that's a sign this should have gone through
  `requirements-analyst` instead of straight to delegation.
- The diff was actually read, not just summarized by exit code.
- Merge happened only after explicit confirmation.

## Escalation

If Codex's diff touches something outside what was asked, or the task turns out to
need a decision only a human can make, stop and surface it — do not silently fix it up
yourself or re-dispatch with a patched prompt without telling the human what happened.
