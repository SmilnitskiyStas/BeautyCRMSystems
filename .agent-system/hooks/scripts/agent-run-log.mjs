#!/usr/bin/env node
// SubagentStop hook. See ../agent-run-log.md. Never blocks, never throws.
import { appendFileSync, mkdirSync } from "node:fs";
import { join } from "node:path";
import { readHookInput, loadHooksConfig, allow } from "./lib/hook-io.mjs";
import { summarizeTranscript } from "./lib/transcript-usage.mjs";

const input = readHookInput();
try {
  const cwd = input.cwd || process.cwd();
  const cfg = loadHooksConfig(cwd);
  if (String(cfg.agent_run_log || "on").toLowerCase() === "off") allow();

  // nothing to attribute (malformed/foreign event) — do not write an empty record
  if (!input.agent_type && !input.agent_transcript_path) allow();
  const usage = summarizeTranscript(input.agent_transcript_path);
  // a stop with no model calls (internal/foreign subagent, missing transcript) would only skew averages
  if (!usage.requests) allow();
  const record = {
    ts: new Date().toISOString(),
    session_id: input.session_id || null,
    agent_id: input.agent_id || null,
    agent: input.agent_type || "unknown",
    ...usage,
    final_message_chars: (input.last_assistant_message || "").length,
  };
  const dir = join(cwd, ".ai", "logs");
  mkdirSync(dir, { recursive: true });
  appendFileSync(join(dir, "agent-runs.jsonl"), JSON.stringify(record) + "\n");
} catch {
  // analytics must never get in the way of the work
}
allow();
