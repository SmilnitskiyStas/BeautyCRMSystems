---
name: create-contract
description: >-
  Produce or version an API_CONTRACT artifact so consumers can build in parallel. Use
  when defining a new API surface or changing an existing one.
x-generated-from: skills/workflow/create-contract/SKILL.md
---

# Create Contract

## When

Before frontend and backend work on the same feature start in parallel, and whenever an
endpoint's request or response shape changes.

## Steps

1. Define the surface: paths / operations, request shape, response shape, error shapes,
   auth requirement, pagination, idempotency.
2. Write it in the project's contract format (OpenAPI, GraphQL SDL, typed schema — per
   ADR). Put it under `.ai/artifacts/contracts/`.
3. Add the artifact header (`templates/artifact-template.md`): `type: API_CONTRACT`,
   `id: API-CONTRACT-<FEATURE>-V<n>`, `status: draft`, list the `consumers`.
4. Circulate for review; on agreement set `status: accepted`. Only an `accepted` contract
   may be depended on for parallel work (`workflow/artifact-policy.md`).
5. A breaking change is a **new version** (`-V2`) with `supersedes` set; the old one goes
   `superseded`; notify consumers through the PM.

## Validation

- Every field has a type and a required/optional marker.
- Error responses are specified, not just the happy path.
- The task that implements the server sets `provides: [<contract id>]`; consumer tasks set
  `requires_artifacts: [<contract id>]`.

## Rule

The consumer does not edit the producer's code because the contract is inconvenient — it
files a contract-change request (`workflow/agent-communication-policy.md`).
