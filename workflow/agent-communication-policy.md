# Agent communication policy — the Interaction Gate

No uncontrolled agent-to-agent chat mesh (spec §25). Every cross-agent request passes the
Interaction Gate first.

## The check (spec §26)

Before agent A asks agent B anything:

```yaml
request:
  from: A
  to: B
  question: "..."

checks:
  answer_exists_in_artifact:   # is it already in an accepted artifact?
  answer_exists_in_docs:       # is it in living docs / spec / ADR?
  request_is_blocking:         # does A actually need this to proceed now?
  target_agent_is_correct:     # is B the right owner of this answer?
```

## Decision

```yaml
decision: ALLOW | REJECT | REDIRECT
reason: "..."
reference: "<artifact id / doc anchor / correct agent>"
```

- `REJECT` when the answer already exists — return the reference.
- `REDIRECT` when B is the wrong owner — name the right agent.
- `ALLOW` only for genuinely blocking, not-yet-answered questions to the correct owner.

A cheap classifier is enough for almost all of these.

## Routing

- Non-blocking questions become backlog items or artifact requests, not live pings.
- Blockers go through `project-manager` (`handoff-policy.md`), not directly to B.
- Frontend does not edit backend because an API is inconvenient — it files a contract
  change request (spec §24).
