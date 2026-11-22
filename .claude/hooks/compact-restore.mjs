#!/usr/bin/env node
// SessionStart hook (matcher: compact). See ../compact-restore.md.
import { existsSync, readFileSync, statSync } from "node:fs";
import { join } from "node:path";
import { readHookInput, allow } from "./lib/hook-io.mjs";
import { readActiveContext } from "./lib/memory-bank.mjs";

const MAX_AGE_MS = 6 * 60 * 60 * 1000;
const MAX_CHARS = 3000;
const ACTIVE_CHARS = 1500;

const input = readHookInput();
const cwd = input.cwd || process.cwd();
const file = join(cwd, ".ai", "state", "session-snapshot.md");
try {
  const parts = [];
  if (existsSync(file)) {
    const age = Date.now() - statSync(file).mtimeMs;
    if (age <= MAX_AGE_MS) {
      let text = readFileSync(file, "utf8").trim();
      if (text.length > MAX_CHARS) text = text.slice(0, MAX_CHARS) + "\n…(truncated)";
      const mins = Math.max(1, Math.round(age / 60000));
      parts.push(`Working state saved ${mins} min ago, before the context was compacted:\n\n${text}`);
    }
  }
  const active = readActiveContext(cwd, ACTIVE_CHARS);
  if (active) parts.push(`Project memory bank — active context (.ai/memory/activeContext.md):\n\n${active}`);
  if (!parts.length) allow();
  process.stdout.write(
    JSON.stringify({
      hookSpecificOutput: {
        hookEventName: "SessionStart",
        additionalContext: parts.join("\n\n---\n\n"),
      },
    })
  );
} catch {
  // restoring is best-effort
}
allow();
