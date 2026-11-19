# Handoff policy

A handoff transfers a task between agents. Record it with the fields in
`schemas/handoff.schema.json` (`templates/handoff-template.md`):

```yaml
task_id:
from:
to:
status:            # completed | blocked | partial | needs_review | needs_clarification
reason:
completed:         # what is actually done
artifacts:         # artifact ids produced/updated
open_questions:
blockers:
next_expected_action:   # one sentence for the receiver
```

## Blocker rule (spec §27)

> A blocker goes through `project-manager`, not straight to an arbitrary agent.

When you hit something you cannot resolve, hand off with `to: project-manager` and
`status: blocked`. The PM re-routes. This keeps a task from being lost when the right
next agent is not known in advance.

## Before handing off

- Run the Interaction Gate check (`agent-communication-policy.md`) — the answer may
  already exist in an artifact or in docs.
- Make sure any artifact you produced is written and its status set, so the receiver can
  consume it without asking you.
- State the `next_expected_action` concretely. "Review the auth changes in
  `src/auth/session.ts`" beats "please review".

## Do not

- Edit another agent's completed work during a handoff — only task status and metadata.
- Chain handoffs to dodge the PM on a real blocker.
