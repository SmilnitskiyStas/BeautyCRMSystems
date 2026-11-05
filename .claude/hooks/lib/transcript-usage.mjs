// Summarize token usage from a Claude Code transcript (.jsonl). Self-contained — shipped
// verbatim into .claude/hooks/ and plugin hooks/scripts/ (see hooks/README.md).
//
// Format facts (verified on real subagent transcripts, Claude Code 2.1.316):
//  - one model call is written as SEVERAL assistant entries sharing message.id (one per
//    content block), each carrying a usage snapshot; early snapshots are partial
//    (output_tokens in the tens vs. thousands in the last). So: keep the LAST entry per id.
//  - usage: input_tokens, output_tokens, cache_read_input_tokens,
//    cache_creation_input_tokens, output_tokens_details.thinking_tokens.
import { readFileSync, existsSync, readdirSync, statSync } from "node:fs";
import { join, resolve } from "node:path";
import { homedir } from "node:os";

export function summarizeTranscript(path) {
  const empty = { requests: 0, input: 0, output: 0, thinking: 0, cache_read: 0, cache_write: 0, tool_calls: 0, models: [], started: null, ended: null, duration_s: null };
  if (!path || !existsSync(path)) return empty;
  let lines;
  try {
    lines = readFileSync(path, "utf8").split("\n").filter(Boolean);
  } catch {
    return empty;
  }
  const lastById = new Map();
  const stamps = [];
  let tools = 0;
  for (const raw of lines) {
    let e;
    try { e = JSON.parse(raw); } catch { continue; }
    const t = e.timestamp ? Date.parse(e.timestamp) : NaN;
    if (Number.isFinite(t)) stamps.push(t);
    if (e.type !== "assistant" || !e.message) continue;
    if (Array.isArray(e.message.content)) tools += e.message.content.filter((b) => b?.type === "tool_use").length;
    if (e.message.usage) lastById.set(e.message.id || e.requestId || e.uuid, e.message);
  }
  const s = { ...empty, tool_calls: tools };
  const models = new Set();
  for (const m of lastById.values()) {
    const u = m.usage;
    s.requests++;
    s.input += u.input_tokens || 0;
    s.output += u.output_tokens || 0;
    s.thinking += u.output_tokens_details?.thinking_tokens || 0;
    s.cache_read += u.cache_read_input_tokens || 0;
    s.cache_write += u.cache_creation_input_tokens || 0;
    if (m.model) models.add(m.model);
  }
  s.models = [...models];
  if (stamps.length) {
    s.started = new Date(Math.min(...stamps)).toISOString();
    s.ended = new Date(Math.max(...stamps)).toISOString();
    s.duration_s = Math.round((Math.max(...stamps) - Math.min(...stamps)) / 1000);
  }
  return s;
}

// Per-model totals plus the CURRENT context size of a (main-session) transcript.
// context_now = input + cache_read + cache_write of the last model call — what the next
// message has to carry. Same last-snapshot-per-message.id rule as summarizeTranscript.
export function summarizeByModel(path) {
  const out = { requests: 0, context_now: 0, models: {}, started: null, ended: null };
  if (!path || !existsSync(path)) return out;
  let lines;
  try { lines = readFileSync(path, "utf8").split("\n").filter(Boolean); } catch { return out; }
  const lastById = new Map();
  const stamps = [];
  let lastUsage = null;
  for (const raw of lines) {
    let e;
    try { e = JSON.parse(raw); } catch { continue; }
    const t = e.timestamp ? Date.parse(e.timestamp) : NaN;
    if (Number.isFinite(t)) stamps.push(t);
    if (e.type !== "assistant" || !e.message?.usage) continue;
    lastById.set(e.message.id || e.requestId || e.uuid, e.message);
    lastUsage = e.message.usage;
  }
  for (const m of lastById.values()) {
    const u = m.usage;
    const k = m.model || "unknown";
    const a = (out.models[k] ||= { requests: 0, input: 0, output: 0, cache_read: 0, cache_write: 0 });
    a.requests++;
    a.input += u.input_tokens || 0;
    a.output += u.output_tokens || 0;
    a.cache_read += u.cache_read_input_tokens || 0;
    a.cache_write += u.cache_creation_input_tokens || 0;
    out.requests++;
  }
  if (lastUsage) out.context_now = (lastUsage.input_tokens || 0) + (lastUsage.cache_read_input_tokens || 0) + (lastUsage.cache_creation_input_tokens || 0);
  if (stamps.length) { out.started = new Date(Math.min(...stamps)).toISOString(); out.ended = new Date(Math.max(...stamps)).toISOString(); }
  return out;
}

// Locate the newest main-session transcript for a project directory. Claude Code stores
// them in <config>/projects/<cwd with every non-alphanumeric char replaced by "-">/*.jsonl.
export function findSessionTranscript(cwd, configDir) {
  const base = configDir || process.env.CLAUDE_CONFIG_DIR || join(homedir(), ".claude");
  const dir = join(base, "projects", resolve(cwd).replace(/[^A-Za-z0-9]/g, "-"));
  if (!existsSync(dir)) return null;
  let best = null;
  for (const f of readdirSync(dir)) {
    if (!f.endsWith(".jsonl")) continue;
    const full = join(dir, f);
    const m = statSync(full).mtimeMs;
    if (!best || m > best.m) best = { full, m };
  }
  return best ? best.full : null;
}
