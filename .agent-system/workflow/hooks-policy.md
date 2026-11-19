# Hooks policy

Hooks turn a written rule into an enforced one. They exist to catch the mechanical
cases a human reviewer shouldn't have to — not to replace `human-gates.md` or
`review-policy.md`.

## What a hook may do

- Block a mechanical, unambiguous violation: an edit under a generated/denied path, an
  unapproved dependency install, a turn ending without a required task log.
- Point at the policy it enforces and what to do instead, in its block message.

## What a hook must never do

- Make a judgment call a human or a review agent should make — a hook is a fast,
  narrow, deterministic check, not a reviewer (`review-policy.md` still runs).
- Fail closed on its own bug. Every hook in `hooks/` fails open (allows the action) on
  a parse error or unexpected input shape — see each hook's "failure mode" notes.
- Silently change behavior in a consuming project. Parameters (`deny_paths`,
  `approved_packages`, ...) always come from that project's own
  `.ai/hooks.config.yaml`, never from a default meant for this repo.

## Definitions live in `hooks/`, not in `.claude/settings.json`

`hooks/<id>.md` (schema: `schemas/hook.schema.json`) is the source of truth.
`adapters/claude/generate-hooks.mjs` compiles it to `.claude/hooks/**` +
`.claude/settings.hooks.json`. Because `.claude/settings.json` is project runtime state
(`scripts/sync-agents.mjs` never overwrites it), wiring a hook up is an explicit,
separate step: `node scripts/apply-hook-settings.mjs --apply`.

## Relationship to other policies

- `human-gates.md` — `dependency-gate` routes an unapproved package here.
- `task-lifecycle.md` — `task-log-sync` operationalizes `task_log_written` from the
  Definition of Done.
- `review-policy.md` — hooks run before and independently of the review pipeline; they
  never substitute for it.
