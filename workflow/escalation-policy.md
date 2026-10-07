# Escalation policy

Two kinds of escalation: **raise the model tier** (stay autonomous) and **stop and ask a
human** (hand control back).

## Raise the tier (spec §17)

The router or the working agent may raise the tier mid-task when reality turns out harder
than the classification: unexpected architecture impact, a concurrency or migration
concern surfaces, ambiguity is deeper than it looked. Record it in the task log's
`model.escalations`. Tier only goes up this way, never down.

## Stop and ask a human

Escalate to a human when:

- a blocking question needs a product / UX / priority / brand decision the AI is not
  allowed to invent (spec §9.2, §28);
- instructions conflict and `source-of-truth.md` does not resolve it
  (`rules-hierarchy.md`);
- context growth is abnormal and compaction is not helping (`context-policy.md` step 6);
- a review disagreement is unresolved after the cycle cap (`review-policy.md`);
- the task hits a `human-gates.md` action.

When you stop: return what you have, state exactly what decision is needed and the
options, and do not proceed on a guess.

## Do not escalate for

- Minor missing detail that a documented assumption or placeholder covers — note the
  assumption and continue (spec, "AI Workflow").
- Something already answered in an artifact or in docs — read it
  (`agent-communication-policy.md`).
