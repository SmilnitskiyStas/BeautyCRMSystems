<!-- One entry for known-issues.md (spec §32-33). The whole-file skeleton is
     known-issues-doc-template.md; this is a single record to append.
     An entry without Root cause + Prevention does not do its job — do not close without them. -->

### KI-NNN: <Title>

**Severity:** critical | high | medium | low
**Status:** open | resolved
**Discovered in:** TASK-XXX
**Resolved in:** TASK-YYY
**Area:** frontend | backend | database | security | infra | mobile | ...

**Description:** <symptom — what was observed, under what conditions it reproduces>

**Root cause:** <resolved only — the real technical reason. Not "we forgot to check X"
but "X returns Y whenever Z, and the code assumed W">

**Resolution:** <what was changed to fix it>

**Prevention:** <resolved only — a concrete, actionable rule: a lint rule, a checklist
item added to a skill file, a test that now covers it. Not "be more careful". If the rule
generalizes, also copy it into the relevant skills/<domain>/*.md or agents/**/<role>.md —
see skills/workflow/resolve-known-issue (spec §69: memory -> repeated pattern ->
human-approved rule update, never auto-rewrite from one failure).>

**Affected patterns:** <other places that share this shape and should be checked>
