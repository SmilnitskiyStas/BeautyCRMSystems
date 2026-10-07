# Task DAG

The backlog is a dependency graph, not a flat list (spec §20). Each task (see
`schemas/task.schema.json`):

```yaml
id: TASK-204
title: Implement partial refunds
agent: backend-developer
depends_on: [TASK-201]
requires_artifacts: [API-CONTRACT-REFUND-V1]
provides: [ARTIFACT-REFUND-SERVICE-V1]
blocks: [TASK-205]
status: planned
```

## Depend on artifacts, not whole tasks (spec §21, §24)

Frontend does not wait for backend to finish — it waits for the **API contract artifact**.
For a cross-stack feature:

```
requirements -> (architecture if needed) -> contract
                                              /      \
                                        backend    frontend      (parallel)
                                              \      /
                                            integration
                                                 |
                                                QA
```

Prefer `requires_artifacts` over `depends_on` whenever only an artifact is actually
needed.

## Vertical slices (spec §52)

For feature work, prefer:

```
requirements -> contract -> minimal backend + frontend -> integration test -> expand
```

over "all backend, then all frontend", when the horizontal split creates dependencies
that the vertical one avoids.

## Parallelism check (spec §51)

Before running tasks in parallel, confirm:

- they do not edit the same files;
- there is no contract dependency between them;
- neither needs a decision the other produces;
- no merge-conflict risk.

Otherwise sequence them.

## Validation

`scripts/validate-config.mjs` rejects a cyclic hard dependency (`depends_on`) and a
`requires_artifacts` reference no task `provides`.
