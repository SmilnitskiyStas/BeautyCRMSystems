<!-- project-researcher output (spec §10). Save as an artifact: type RESEARCH_REPORT.
     Read-only pass — no production code, no architecture change, no new patterns. -->

# Research report: <task title>

**Task:** TASK-XXX
**Author:** project-researcher
**Date:** YYYY-MM-DD

## Source of truth for this area
- <path / doc / contract that is authoritative — see workflow/source-of-truth.md>

## Relevant files
| Path | Why it matters |
|---|---|
| `src/...` | <current implementation of X> |

## Existing patterns
- <pattern already used for this kind of thing, with an example location>

## Contracts / schemas touched
- <API contract / DB schema / event schema id or path>

## Architecture decisions that apply
- ADR-XXX — <one line>

## Tests that cover this area
- `<path>` — <what it asserts>

## Known issues in scope
- KI-XXX — <symptom + prevention rule>

## Risks
- <what could go wrong given the above>

## Retrieval method used
<repository tree / ripgrep / symbol search / AST / git history / docs — in that order.
Vector RAG is out of scope (spec §10).>
