<!-- vendored from the agent library — regenerate with sync-agents -->
## Rule: minimal change

Solve the whole task with the least new code. Never trade correctness for brevity.

**Before you write:** read the task and the code it touches. List every place the change must reach (callers, tests, fixtures, config, exports) — that is the scope. Extra features are not.

**Take the first option that fully works:**

1. Skip what nobody asked for, and say so in one line.
2. Reuse what the codebase already has (helper, component, service, pattern), the way the surrounding code does.
3. Use the standard library or a platform feature, unless the project has its own.
4. Use an installed dependency. A new one goes through the dependency gate; never add one for a few lines.
5. Otherwise write the minimum that works.

- No abstraction, wrapper, option, config or "for later" code nobody asked for. Deletion beats addition. Keep the layers and conventions of the codebase.
- Edit in place. Do not rewrite a file, or re-create one that exists.
- Do not paste code, diffs or file contents into your reply — the files are the output. Report paths and a short summary.
- Bug fix: grep every caller of what you touch, then fix the root cause once, in the shared code.
- Moved or merged code keeps its validation and error handling.
- New non-trivial logic (a branch, a loop, a parser, money, security) leaves one small test; trivial changes need none.
- A known shortcut gets a one-line comment: `shortcut: <the limit>, <when to upgrade>`.

**Never cut:** validation at trust boundaries, error handling that prevents data loss, security, accessibility, anything the task or an accepted contract/ADR requires, required tests, human gates.
