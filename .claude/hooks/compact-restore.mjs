#!/usr/bin/env node
// SessionStart hook (matcher: compact). See ../compact-restore.md.
import { existsSync, readFileSync, statSync } from "node:fs";
import { join } from "node:path";
import { readHookInput, allow } from "./lib/hook-io.mjs";

const MAX_AGE_MS = 6 * 60 * 60 * 1000;
const MAX_CHARS = 3000;

const input = readHookInput();
const file = join(input.cwd || process.cwd(), ".ai", "state", "session-snapshot.md");
try {
  if (!existsSync(file)) allow();
  const age = Date.now() - statSync(file).mtimeMs;
  if (age > MAX_AGE_MS) allow();
  let text = readFileSync(file, "utf8").trim();
  if (text.length > MAX_CHARS) text = text.slice(0, MAX_CHARS) + "\n…(truncated)";
  const mins = Math.max(1, Math.round(age / 60000));
  process.stdout.write(
    JSON.stringify({
      hookSpecificOutput: {
        hookEventName: "SessionStart",
        additionalContext: `Working state saved ${mins} min ago, before the context was compacted:\n\n${text}`,
      },
    })
  );
} catch {
  // restoring is best-effort
}
allow();
