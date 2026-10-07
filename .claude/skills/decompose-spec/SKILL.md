---
name: decompose-spec
description: >-
  Break a project spec into a task DAG with dependencies and artifact links. Use when
  turning requirements into an executable backlog, or when re-planning after a scope change.
x-generated-from: skills/workflow/decompose-spec/SKILL.md
---

# Decompose Spec

## When

At project start, and whenever the spec gains a feature large enough to need more than one
task.

## Steps

1. Read the relevant spec sections (not the whole document unless this is the initial
   decomposition).
2. For each deliverable, write a task with `templates/task-template.md`:
   - narrow, single-agent scope;
   - `acceptance_criteria` that are observable and testable;
   - `complexity` and `risk` flags (`workflow/task-lifecycle.md`).
3. Wire dependencies (`workflow/task-dag.md`):
   - prefer `requires_artifacts` over `depends_on` when only an artifact is needed;
   - name the artifacts each task `provides`;
   - for cross-stack features, split as requirements -> contract -> parallel backend +
     frontend -> integration -> QA, not "all backend then all frontend".
4. Prefer vertical slices: a thin end-to-end path first, then expand.
5. Mark which tasks are parallel-ready (independent files, no shared decision pending).
6. Hand the graph to `project-manager`.

## Validation

- No task depends on itself transitively (`validate-config` also checks this once tasks
  are in a store).
- Every `requires_artifacts` id is `provided` by some task.
- No task is so broad it needs two agents — split it.

## Escalation

Ambiguity that blocks writing acceptance criteria goes to `requirements-analyst` or a
human gate before the task is created, not into the task as a guess.
