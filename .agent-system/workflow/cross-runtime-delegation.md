# Cross-runtime delegation

How a Claude Code session hands a task to Codex CLI as a background worker, and how
that work comes back under review — the mechanism behind "distribute work across
runtimes" (`scripts/dispatch-codex.mjs`).

## Why Codex, specifically, as a delegate

A different vendor/model running in an isolated git worktree is a stronger
independent reviewer than a second Claude session (`review-policy.md`'s "never the
same agent session" is trivially satisfied — it isn't even the same model family).
Delegation is not about Codex being "the other agent that also codes"; it's about
getting a second, differently-biased implementation or a second opinion at low
coordination cost.

## When to propose delegation

Good candidates (from `task-dag.md`'s parallel-ready flags): an independent, narrow,
well-specified task that doesn't share files with what the current session is doing —
a vertical slice, a mechanical migration, a second-opinion implementation to diff
against. Do not delegate an ambiguous task (send it through `requirements-analyst`
first) or one with a pending human gate.

## The gate: propose, then confirm (spec-equivalent: this doc)

Delegation defaults to **semi-automatic**: Claude may notice a good candidate and
draft the dispatch, but never runs `dispatch-codex.mjs start` without the human
confirming in the session first. Present, before dispatch:

- the exact task text Codex will receive,
- the worktree/branch it will run in (`.worktrees/<slug>` / `codex/<slug>`),
- the sandbox mode (default `workspace-write` — Codex can edit files and run commands
  inside the worktree, nothing outside it),
- what "done" will be checked against.

This is a human gate in the sense of `human-gates.md` even though it isn't listed
there as an unconditional one — it's conditional on choosing to use delegation at all,
not on every task.

## Mechanics

1. `node scripts/dispatch-codex.mjs start --slug <name> --task-file <path> [--base <branch>] [--sandbox workspace-write]`
   creates `.worktrees/<slug>` on branch `codex/<slug>`, then runs `codex exec` inside
   it to completion, streaming JSONL events to
   `.ai/logs/delegations/<slug>/events.jsonl` and the final message to
   `.ai/logs/delegations/<slug>/last-message.txt`. Invoke this via a **backgrounded**
   shell call (Claude Code's Bash tool with `run_in_background: true`) — the script
   itself blocks until Codex finishes.
2. While it runs, check progress with
   `node scripts/dispatch-codex.mjs status --slug <name>` (reads the record +
   tails `run.log`), or tail the log files directly.
3. Once `status` is `done`, run `node scripts/dispatch-codex.mjs collect --slug <name>`
   — prints Codex's last message and a diff stat against `base`. Read the diff
   yourself; this **is** the independent review (`review-policy.md`), not a
   rubber stamp.
4. Merging `codex/<slug>` back into the working branch is **always** an explicit human
   gate (`human-gates.md`) — never automatic, regardless of how clean the diff looks.
5. After merge (or if the work is discarded), run
   `node scripts/dispatch-codex.mjs cleanup --slug <name>` to remove the worktree.
   Cleanup refuses to run on an unmerged branch unless `--force` is passed.

## Failure handling

`codex exec` exiting non-zero marks the record `failed`, not `running` forever —
`collect` still works on a failed run so you can read what Codex attempted before it
gave up. A worktree is never silently deleted; `cleanup` always requires either a
merged branch or an explicit `--force`.

## Scope

Claude-only for now: this depends on `codex exec`'s non-interactive mode (verified
present in `codex-cli 0.144.1` — `codex exec --help`), not on anything from
`adapters/codex/`. It does not use Codex's own internal `multi_agent` sub-agent
feature (`adapters/COMPATIBILITY.md`) — that's Codex spawning its own sub-agents
inside one session; this is Claude Code spawning a whole separate `codex exec`
process as a peer worker.
