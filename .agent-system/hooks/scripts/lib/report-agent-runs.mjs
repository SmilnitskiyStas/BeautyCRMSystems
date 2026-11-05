#!/usr/bin/env node
// Agent run analytics from .ai/logs/agent-runs.jsonl (written by the agent-run-log hook).
//
//   node scripts/report-agent-runs.mjs [--file <path>] [--since 7d|24h|2026-10-01] [--agent <id>] [--json]
//                                      [--transcript <path>] [--no-session]
// Text output ends with a "Main session" section (tokens per model, current context size) read from
// the newest transcript of this project; --json prints only the per-agent array.
//   (also reachable as: node scripts/report-agent-usage.mjs --runs ...)
//
// Prints per agent: runs, model calls, total and average tokens, cache share, tool calls,
// average duration. Token counts are API-reported; no dollar figure on purpose.
import { readFileSync, existsSync } from "node:fs";
import { resolve } from "node:path";
import { summarizeByModel, findSessionTranscript } from "./transcript-usage.mjs";

const argv = process.argv.slice(2);
const opt = (n, d = null) => (argv.includes(n) ? argv[argv.indexOf(n) + 1] : d);
const file = resolve(opt("--file", ".ai/logs/agent-runs.jsonl"));

const haveLog = existsSync(file);
const wantSession = !argv.includes("--no-session") && !argv.includes("--json");
if (!haveLog && !wantSession) {
  console.error(`No run log at ${file}. Enable the agent-run-log hook (node scripts/apply-hook-settings.mjs --apply --local --only agent-run-log) and run some subagents.`);
  process.exit(1);
}

function sinceMs(v) {
  if (!v) return 0;
  const m = v.match(/^(\d+)([hd])$/);
  if (m) return Date.now() - Number(m[1]) * (m[2] === "h" ? 3600e3 : 86400e3);
  const t = Date.parse(v);
  if (Number.isNaN(t)) throw new Error(`bad --since "${v}" (use 7d, 24h or a date)`);
  return t;
}

const since = sinceMs(opt("--since"));
const only = opt("--agent");
const rows = (haveLog ? readFileSync(file, "utf8") : "")
  .split("\n")
  .filter(Boolean)
  .map((l) => { try { return JSON.parse(l); } catch { return null; } })
  .filter((r) => r && Date.parse(r.ts) >= since && (!only || r.agent === only));

const by = new Map();
for (const r of rows) {
  const a = by.get(r.agent) || { agent: r.agent, runs: 0, requests: 0, input: 0, output: 0, thinking: 0, cache_read: 0, cache_write: 0, tools: 0, dur: 0, models: new Set() };
  a.runs++;
  a.requests += r.requests || 0;
  a.input += r.input || 0;
  a.output += r.output || 0;
  a.thinking += r.thinking || 0;
  a.cache_read += r.cache_read || 0;
  a.cache_write += r.cache_write || 0;
  a.tools += r.tool_calls || 0;
  a.dur += r.duration_s || 0;
  for (const m of r.models || []) a.models.add(m);
  by.set(r.agent, a);
}

const k = (n) => (n >= 1e6 ? `${(n / 1e6).toFixed(1)}M` : n >= 1e3 ? `${(n / 1e3).toFixed(1)}k` : String(n));
const pad = (s, n, r = false) => (r ? String(s).padStart(n) : String(s).padEnd(n));
// "total" = everything the model processed: fresh input + cache reads + cache writes + output
const total = (a) => a.input + a.cache_read + a.cache_write + a.output;
const list = [...by.values()].sort((x, y) => total(y) - total(x));

if (argv.includes("--json")) {
  console.log(JSON.stringify(list.map((a) => ({ ...a, models: [...a.models], total_tokens: total(a) })), null, 2));
  process.exit(0);
}
function sessionSection() {
  if (!wantSession) return;
  const path = opt("--transcript") || findSessionTranscript(process.cwd());
  const ses = summarizeByModel(path);
  console.log(["", "Main session" + (path ? "" : " — transcript not found")].join(String.fromCharCode(10)));
  if (!path || !ses.requests) return;
  console.log(pad("Model", 24), pad("Calls", 6, true), pad("Output", 8, true), pad("Cache read", 11, true), pad("Cache write", 12, true), pad("Fresh in", 9, true));
  console.log("-".repeat(74));
  for (const [m, a] of Object.entries(ses.models).sort((x, y) => y[1].cache_read - x[1].cache_read))
    console.log(pad(m, 24), pad(a.requests, 6, true), pad(k(a.output), 8, true), pad(k(a.cache_read), 11, true), pad(k(a.cache_write), 12, true), pad(k(a.input), 9, true));
  console.log("-".repeat(74));
  console.log("Context carried by the next message: ~" + k(ses.context_now) + " tokens.");
  const models = Object.keys(ses.models).length;
  if (ses.context_now > 300000) console.log("Large context: every message re-reads it. Consider the handoff-session skill, then /clear (or /compact <what to keep>).");
  if (models > 1) console.log(models + " models used in this session: each switch re-caches the whole conversation — avoid switching mid-session.");
}

if (!list.length) { console.log("No runs in range."); sessionSection(); process.exit(0); }

console.log(pad("Agent", 24), pad("Runs", 5, true), pad("Calls", 6, true), pad("Output", 8, true), pad("Total", 8, true), pad("Avg/run", 8, true), pad("Cache%", 7, true), pad("Tools", 6, true), pad("Avg s", 6, true), " Model");
console.log("-".repeat(104));
let g = 0, gr = 0;
for (const a of list) {
  const t = total(a);
  g += t; gr += a.runs;
  const cachePct = t ? Math.round((100 * a.cache_read) / t) : 0;
  console.log(pad(a.agent, 24), pad(a.runs, 5, true), pad(a.requests, 6, true), pad(k(a.output), 8, true), pad(k(t), 8, true), pad(k(Math.round(t / a.runs)), 8, true), pad(`${cachePct}%`, 7, true), pad(a.tools, 6, true), pad(Math.round(a.dur / a.runs), 6, true), " " + [...a.models].join(","));
}
console.log("-".repeat(104));
console.log(`${gr} subagent run(s), ${k(g)} tokens processed (Total = input + cache + output; Cache% = share served from prompt cache).`);
console.log("Agents with big Avg/run are the first tuning targets. For plan limits and cost, see /usage.");
sessionSection();
process.exit(0);
