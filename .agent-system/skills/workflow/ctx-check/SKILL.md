---
name: ctx-check
description: >-
  Use when a Claude Code session feels slow, expensive, or forgetful, or the context window is filling up, and you need to decide between /clear, /compact, delegating to a subagent, or doing nothing. Diagnoses where context and tokens are going from /context and /usage output.
---

# Skill: Context Check

## When
- The session is long, responses degrade, or the context window is near full.
- Usage is climbing faster than the work justifies.
- You need to pick one next step: `/clear`, `/compact`, a subagent, or no action.

## Steps
1. A skill cannot run built-in commands. Ask the user to run `/context` and `/usage`, or read their output if already pasted.
2. Classify the main driver:
   - Old, unrelated work fills the context -> recommend `/clear` after the handoff-session skill writes the handoff.
   - Same task, context is large -> recommend `/compact <what to keep>`, using the project's "Compact instructions".
   - Verbose tool output (tests, logs, search results) -> delegate that work to a subagent so raw output stays out of the main context.
   - MCP servers or skills bloat the context -> recommend `/skill-doctor` and `/mcp` to trim them.
   - Nothing dominant -> do nothing.
3. Give ONE concrete recommendation with the exact command and a one-line reason.
4. Never recommend `/compact` right before a long idle without noting that the prompt cache expires and the next message will re-read the full context at full cost.

Reference: `workflow/context-policy.md`, section "Session commands".

## Validation
- The recommendation names a single command (or "do nothing").
- The reason cites the evidence from `/context` or `/usage`.
- If `/compact` is suggested, the cache-expiry note is present when an idle gap is expected.

## Escalation
- If `/usage` shows the session is near the plan limit, say so first, before any context advice.
- If the output is missing or ambiguous, ask the user for a fresh `/context` run instead of guessing.
