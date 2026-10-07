---
name: root-cause-analysis
description: >-
  Structured root-cause analysis before proposing a fix — reproduce, isolate, explain.
  Use when debugging a non-trivial failure, a flaky test, or "works here but not there".
x-generated-from: skills/workflow/root-cause-analysis/SKILL.md
---

# Root Cause Analysis

## When

Any failure that is not an obvious typo. Especially: intermittent failures, environment
differences, regressions after a deploy.

## Steps

1. **Reproduce** — a minimal, reliable repro. If it is intermittent, find what makes it
   fire.
2. **Isolate** — bisect: last known good, first bad. Narrow to a file / commit / input.
3. **Explain the mechanism** — not "we forgot X" but "X returns Y whenever Z, and the
   code assumed W". State it precisely enough to predict other places it breaks.
4. **Confirm** — show that changing the identified cause changes the symptom.
5. **Fix** — the smallest change that addresses the mechanism, not the symptom.
6. **Prevent** — a concrete rule: a test that now covers it, a lint rule, a checklist
   item in the relevant skill. Record via `skills/workflow/resolve-known-issue`.

## Validation

- The explanation predicts the observed behavior exactly, including when it does *not*
  fail.
- The fix is minimal and production-safe; unrelated code is untouched.
- `Root cause` + `Prevention` land in `known-issues.md` before the task closes.

## Guardrail (spec §69)

One failure does not justify rewriting an agent's rules. A prevention rule graduates into
a skill/agent file only after the pattern repeats and a human approves it.
