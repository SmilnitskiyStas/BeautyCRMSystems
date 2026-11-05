---
name: integrate-worktrees
description: >-
  Fold branches produced by parallel worktree agents into one integrate/* branch in
  dependency order, testing after each merge. Use after parallel agents finish, before
  anything is merged into main.
x-generated-from: .claude/skills/integrate-worktrees/SKILL.md
---

# Integrate Worktrees

## When

Two or more agents ran in isolated worktrees (`isolation: worktree`, `/batch`, or
`dispatch-codex`) and each left a branch. Not for a single branch — merge that directly
through the normal review path.

## Steps

1. **Collect** the branch names and the intended base (usually the current branch) from
   the agent hand-backs. Order them by the task DAG (`.claude/workflow/task-dag.md`): contracts and
   schemas first, consumers after.
2. **Plan:** `node scripts/integrate-worktrees.mjs plan --base <base> --branches a,b,c`.
   - `STALE_BASE` — the agent started from the wrong tree. Do not merge; re-run that agent
     with `worktree.baseRef: head`.
   - `UNCOMMITTED` — the agent had no commit rights. Commit in its worktree first
     (`git -C <path> add -A && git -C <path> commit`), after reading what you are committing.
   - `overlap` — two branches touch the same file; keep their DAG order and expect friction.
3. **Merge:** `node scripts/integrate-worktrees.mjs merge --base <base> --into integrate/<feature> --branches a,b,c --test "<project test command>"`.
   It creates a fresh branch, merges one by one, and rolls back a merge whose tests fail.
4. **On a conflict or failing test** (exit 3): stop. Report the branch, files and output to
   `project-manager` for the owning agent. Resolve only purely mechanical conflicts
   (adjacent lines, import order); never a behavioral one.
5. **Hand off:** give `integrate/<feature>` to `review-orchestrator`; tell the human the diff
   is ready. Merging it into the base is their gate.
6. **Cleanup** only after that merge: `node scripts/integrate-worktrees.mjs cleanup --branches a,b,c`
   (it keeps anything not merged).

## Validation

- `plan` printed ✓ for every branch before merging.
- The base branch commit is unchanged after the run.
- Tests pass on the integrate branch as a whole, not only per merge.

## Escalation

Behavioral conflict, failing tests that no single branch explains, a migration in more than
one branch, or any need to touch the base branch → stop and ask a human
(`.claude/workflow/human-gates.md`).
