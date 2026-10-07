---
name: engineering-standards
description: >-
  Stack-agnostic engineering standards for this workspace. Use for software engineering, architecture, refactoring, debugging, testing, documentation, and code review.
x-generated-from: skills/shared/engineering-standards/SKILL.md
---

# Personal Engineering Standards

Apply these standards for software engineering, architecture, refactoring, debugging, testing, documentation, and code review tasks.

---

## Core Principles

Write for the next developer, not the next deadline. Production-ready means readable, testable,
and maintainable — not just functional.

- Solve the actual problem. Not a hypothetical future one.
- Clarity beats cleverness. If the code needs a comment to explain *what* it does, rewrite it.
- Three similar lines beats a premature abstraction.
- When in doubt, delete rather than add.

---

## TypeScript First

Default to TypeScript for all new code.

- Prefer explicit types; use `any` only as a last resort, `unknown` when the type is genuinely open
- Use `type` for object shapes and unions; `interface` for extensible contracts
- Enable `strict` mode — never disable compiler checks to silence errors
- Validate at system boundaries (API input, env vars) with `zod` or equivalent; trust internal types
- Avoid type gymnastics; if a type definition exceeds 3–4 lines, question the abstraction

---

## Architecture

- **One file, one responsibility.** Aim for ~300 lines; split when a file is doing too many things.
- **Flat over deep.** Prefer shallow module hierarchies; 3 levels is a reasonable guide.
- **Co-locate related code.** Components, hooks, types, and tests live near each other.
- **Separate layers cleanly.** Presentation / business logic / data access must not bleed into each other.
- Prefer composition over inheritance.
- Fix circular dependencies at the module boundary — don't work around them.

---

## Functions & Structure

- Functions do one thing and are named after what they return or do.
- Aim for functions under ~40 lines. Extract when logic is hard to follow at a glance.
- Use early returns and guard clauses — avoid deeply nested conditionals.
- Avoid boolean flag parameters that alter behavior; prefer explicit variants or separate functions.
- Beyond 3–4 positional arguments, use an options object.

---

## Naming

| Thing | Convention | Example |
|-------|------------|---------|
| Variables | what they contain | `userById`, `activeOrders` |
| Functions | what they do | `fetchUserById`, `validateEmail` |
| Booleans | `is` / `has` / `can` / `should` prefix | `isLoading`, `hasPermission` |
| Event handlers | `on` prefix | `onSubmit`, `onClose` |
| True constants | `SCREAMING_SNAKE_CASE` | `MAX_RETRY_COUNT` |

---

## Comments

Default: **none**. Write a comment only when the *why* is non-obvious — a hidden constraint,
a subtle invariant, a workaround for a specific bug. Never explain what the code does.

---

## Error Handling

- Handle errors at system boundaries; let them propagate naturally through clean internal layers.
- Never swallow errors with empty `catch` blocks.
- Return typed error results rather than throwing where possible.
- Log errors with context: what was attempted, relevant IDs, upstream cause.
- External-facing errors: `{ error: string, code?: string }` — never raw strings or internal stack traces.

---

## State & Data Flow

- Keep state as close to where it's used as possible.
- Prefer immutable patterns; mutate only at explicit, named boundaries.
- Don't store derived values — compute them.
- Normalize data at ingestion; denormalize only for display.

---

## API Design

- RESTful by default; consistent naming, correct HTTP semantics.
- Validate all input at the boundary with a schema before touching business logic.
- Version external APIs from day one.
- Return consistent error shapes across all endpoints.

---

## Testing

- Test behavior, not implementation — tests should survive a refactor.
- Unit test pure functions; integration test API routes and data access.
- Prefer integration tests for database behavior. Use mocks only for external services or when they make tests simpler without hiding real behavior.
- Name tests as sentences: `"returns 404 when user is not found"`.
- Don't write tests that only cover the happy path.

---

## Dependencies

- Add a package only when the alternative is >~50 lines of your own code.
- Prefer well-maintained packages with a small footprint.
- Every dependency is a surface area — be deliberate.

---

## What to Avoid

| Anti-pattern | Why |
|--------------|-----|
| Premature optimization | Measure first; optimize the bottleneck, not the assumption |
| God files | Split by responsibility |
| Magic numbers / literals | Name them |
| `any` types | They erase TypeScript's value and propagate silently |
| Unnecessary abstractions | A function when you need a function; a framework when you need a framework |
| Duplicated logic | Extract on second occurrence; remove the unused copy |
| Deep prop drilling (>2 levels) | Use context or a store |
| Deeply nested structures | Flatten with early returns and well-named helpers |
| Overcomplicated architecture | Match complexity to the actual problem |

---

## AI-Assisted Development

- Review every AI output before committing — plausible is not the same as correct.
- Feed existing codebase patterns to AI for consistency.
- Use AI for boilerplate and scaffolding; apply your own judgment for architecture.
- Reject AI-generated tests that only cover happy paths.
- Prefer smaller, focused prompts over single massive generation requests.

---

## Output Style

When writing code under these standards:

- **Concise**: no filler, no placeholder comments, no `// TODO: implement later`
- **Production-oriented**: edge cases handled, typed properly, errors surfaced
- **Modular**: each piece can be tested and replaced independently
- **Readable**: a developer unfamiliar with this code can follow it in one pass
