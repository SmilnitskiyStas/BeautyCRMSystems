#!/usr/bin/env node
// Stop hook (no matcher). See ../task-log-sync.md.
import { existsSync, readFileSync, readdirSync, statSync } from "node:fs";
import { join } from "node:path";
import { readHookInput, loadHooksConfig, block, allow } from "./lib/hook-io.mjs";

function safe(fn, fallback) {
  try {
    return fn();
  } catch {
    return fallback;
  }
}

const input = readHookInput();
if (input.stop_hook_active) allow(); // already retried once this stop — never loop

const transcriptPath = input.transcript_path;
if (!transcriptPath || !existsSync(transcriptPath)) allow();

const lines = safe(() => readFileSync(transcriptPath, "utf8").trim().split("\n").filter(Boolean), []);
if (!lines.length) allow();

let sessionStart = 0;
let touchedFiles = false;
for (const line of lines) {
  const entry = safe(() => JSON.parse(line), null);
  if (!entry) continue;
  const ts = entry.timestamp ? Date.parse(entry.timestamp) : NaN;
  if (!sessionStart && Number.isFinite(ts)) sessionStart = ts;
  const blocks = entry.message?.content;
  if (
    Array.isArray(blocks) &&
    blocks.some((b) => b?.type === "tool_use" && ["Write", "Edit", "MultiEdit"].includes(b.name))
  ) {
    touchedFiles = true;
  }
}
if (!touchedFiles) allow(); // nothing implemented this turn — no log required

const config = loadHooksConfig(input.cwd || process.cwd());
const logDirName = config.task_log_dir || ".ai/logs/tasks";
const cwd = input.cwd || process.cwd();
const logDir = join(cwd, logDirName);

const hasRecentLog = safe(() => {
  if (!existsSync(logDir)) return false;
  return readdirSync(logDir, { recursive: true }).some((f) =>
    safe(() => {
      const full = join(logDir, String(f));
      const st = statSync(full);
      return st.isFile() && st.mtimeMs >= sessionStart;
    }, false)
  );
}, false);

if (!hasRecentLog) {
  block(
    `task-log-sync: this turn edited files but no task log under ${logDirName} was created or updated.\n` +
      `Run create-task-log / update-task-status before finishing (workflow/task-lifecycle.md — task_log_written).`
  );
}
allow();
