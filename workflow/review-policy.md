# Review policy

## Pipeline (spec §29)

```
implementation agent
      -> self checks
      -> independent review agent
      -> static analysis / tests
      -> human review
```

**The reviewer is never the same agent session that did the implementation.** Spawn a
fresh reviewer.

## Orchestration (spec §30)

`review-orchestrator` decides which reviews a change actually needs:

```yaml
code_review:
security_review:
qa:
accessibility:
seo:
database_review:
```

Do not run every review agent on every change. A copy tweak needs none of the specialist
reviews; a payment flow needs security + QA + code review.

## Findings

Recorded per `schemas/review.schema.json` (`templates/review-template.md`). Every finding
is evidence-based: a `file:line`, a failing input, a spec clause — not a hunch (spec §31).
Order most-severe first.

## Anti-ping-pong (spec §31)

- Cap autonomous review<->fix cycles (default: **2**). Track `cycles` on the review record.
- After a second substantive disagreement, stop and escalate to human review — do not
  loop.
- "Substantive" = correctness, security, data-integrity, contract. Style nits do not
  trigger another full cycle; batch them.

## Security reviewer boundary (spec §53)

Review-only by default. Does not fix production code without a separate assignment. Finds
evidence, classifies severity, does not invent findings. Critical security changes go to a
human gate (`human-gates.md`).
