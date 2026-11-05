<!-- vendored from the agent library — regenerate with sync-agents -->
# Parallel work with worktrees

How several agents work at the same time without trampling each other, and how their
results come back. Mechanism: git worktrees (`isolation: worktree`, `claude --worktree`,
`/batch`, `scripts/dispatch-codex.mjs`). Integration: the `integration-steward` agent and
`scripts/integrate-worktrees.mjs`.

## Parallelism is for time, not for tokens

Each agent has its own context, so parallel work costs about the same tokens as sequential
work, plus the integration overhead. Parallelize only when wall-clock time matters and the
tasks are genuinely independent (`task-dag.md`: parallel-ready, no shared files, no pending
shared decision). A measured trial (two small skills, two worktrees) cost ~23k + ~26k
tokens for ~120 lines of output — fine for independent work, wasteful for tiny edits.

## Preconditions (all must hold)

1. **Base is right.** Claude Code branches worktrees from the repository's *default branch
   on the remote* unless `worktree.baseRef` is `"head"`. A repo whose `origin` is stale or
   points elsewhere (this library once had `origin` = the old v1 repo) gives agents the wrong
   tree. `apply-hook-settings.mjs` sets `worktree.baseRef: "head"`; `plan` detects a stale
   base anyway.
2. **One owner per file.** Split by file/module, not by feature slice, when a slice would
   touch shared files. Shared files (route tables, DI registration, locale JSON, lockfiles)
   go to a single task or are merged last.
3. **Contract first.** Cross-stack work starts from an accepted `API_CONTRACT`
   (`artifact-policy.md`), not from a promise.
4. **Implementers can commit.** An agent without shell access leaves its work uncommitted;
   the steward then commits on its behalf after reading it. Prefer agents with `git-write`.
5. **Small fan-out.** At most 3–4 concurrent worktrees per integration; more adds
   coordination cost faster than it saves time. Each needs its own dependency install —
   copy env files with `.worktreeinclude`.

## Flow

```
project-architect  -> task DAG, parallel-ready flags
project-manager    -> assigns each independent task to an agent with isolation: worktree
implementers       -> work in worktrees, commit, hand back branch + summary
integration-steward-> plan -> merge into integrate/<feature> in DAG order, tests between merges
review-orchestrator-> independent review of the integrated branch
HUMAN GATE         -> merge integrate/<feature> into the base branch
integration-steward-> cleanup worktrees and merged branches
```

## What the steward may and may not do

- May: create `integrate/*`, merge into it, roll it back, resolve purely mechanical
  conflicts, delete *merged* branches and their worktrees.
- May not: touch the base branch, force-push, resolve behavioral conflicts, merge a branch
  that failed `plan`. The script refuses protected targets.

## Conflicts

A textual conflict between adjacent lines is mechanical. Two tasks changing the same
behavior is a planning error: return both to `project-manager`, and consider serializing
them. Repeated conflicts between the same two roles mean the DAG split is wrong.

## Related

`human-gates.md` (merging the integrated branch), `review-policy.md` (independent review),
`cross-runtime-delegation.md` (Codex branches use the same integration path).
