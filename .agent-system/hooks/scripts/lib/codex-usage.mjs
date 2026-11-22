#!/usr/bin/env node
// Token usage of Codex sessions for one project, read from Codex's own rollout logs
// (~/.codex/sessions and ~/.codex/archived_sessions). Codex has no SubagentStop hook, so this is
// the Codex counterpart of report-agent-runs.mjs.
//
//   node codex-usage.mjs [--project <dir>] [--since <ISO date>] [--until <ISO date>]
//                        [--by agent|model|day|session] [--json] [--codex-home <dir>]
//
// Numbers per group: sessions, turns, output tokens (thinking = reasoning share), cached share of
// input. Usage is read as deltas of the cumulative `token_count` events and attributed to the
// model / effort of the turn that was active, so a session that switches model is split correctly.
// "agent" is the role from session_meta when Codex records one (spawned custom agent, guardian
// auto-review); the user's own sessions are "main".
import { readFileSync, readdirSync, existsSync, statSync } from "node:fs";
import { join, resolve } from "node:path";
import { homedir } from "node:os";

const args = process.argv.slice(2);
const opt = (n, d = null) => (args.includes(`--${n}`) ? args[args.indexOf(`--${n}`) + 1] : d);
const project = resolve(opt("project", process.cwd()));
const home = opt("codex-home", join(homedir(), ".codex"));
const since = opt("since") ? Date.parse(opt("since")) : 0;
const until = opt("until") ? Date.parse(opt("until")) : Infinity;
const by = opt("by", "agent");
const norm = (s) => String(s || "").replace(/\\/g, "/").replace(/\/+$/, "").toLowerCase();
const root = norm(project);
const inProject = (cwd) => { const c = norm(cwd); return c === root || c.startsWith(`${root}/`); };

function* rollouts() {
  for (const d of ["sessions", "archived_sessions"]) {
    const base = join(home, d);
    if (!existsSync(base)) continue;
    const walk = function* (dir) {
      for (const e of readdirSync(dir, { withFileTypes: true })) {
        const f = join(dir, e.name);
        if (e.isDirectory()) yield* walk(f);
        else if (e.name.endsWith(".jsonl")) yield f;
      }
    };
    yield* walk(base);
  }
}

export function agentOf(meta) {
  const s = meta.source;
  const sub = s && typeof s === "object" ? s.subagent : null;
  if (sub) {
    const spawn = sub.thread_spawn || {};
    const role = spawn.agent_role || spawn.agent_nickname || (typeof sub.other === "string" ? sub.other : null);
    return role ? String(role) : "subagent";
  }
  return meta.agent_role || meta.agent_nickname || "main";
}

const ZERO = () => ({ sessions: new Set(), turns: 0, input: 0, cached: 0, output: 0, reasoning: 0 });
const add = (g, d) => { g.input += d.input; g.cached += d.cached; g.output += d.output; g.reasoning += d.reasoning; };
const usage = (u) => ({ input: u.input_tokens || 0, cached: u.cached_input_tokens || 0, output: u.output_tokens || 0, reasoning: u.reasoning_output_tokens || 0 });

const groups = new Map();
const bucket = (k) => { if (!groups.has(k)) groups.set(k, ZERO()); return groups.get(k); };
let scanned = 0, matched = 0;

for (const f of rollouts()) {
  scanned++;
  let text;
  try { if (statSync(f).mtimeMs < since) continue; text = readFileSync(f, "utf8"); } catch { continue; }
  let meta = null, model = "unknown", effort = "", prev = ZERO(), day = "", sid = f;
  const local = [];
  for (const line of text.split("\n")) {
    if (!line) continue;
    let x;
    try { x = JSON.parse(line); } catch { continue; }
    if (x.type === "session_meta") {
      // A subagent rollout embeds its parent's session_meta after its own — identity is the first one.
      if (meta) continue;
      meta = x.payload;
      sid = meta.id || sid;
      if (!inProject(meta.cwd)) { meta = null; break; }
    } else if (!meta) continue;
    else if (x.type === "turn_context") { model = x.payload.model || model; effort = x.payload.effort || effort; }
    else if (x.type === "event_msg" && x.payload?.type === "task_started") local.push({ turn: 1, ts: x.timestamp, model, effort });
    else if (x.type === "event_msg" && x.payload?.type === "token_count" && x.payload.info?.total_token_usage) {
      const ts = Date.parse(x.timestamp);
      if (ts < since || ts > until) { prev = ZERO(); Object.assign(prev, usage(x.payload.info.total_token_usage)); continue; }
      const cur = usage(x.payload.info.total_token_usage);
      const d = { input: cur.input - prev.input, cached: cur.cached - prev.cached, output: cur.output - prev.output, reasoning: cur.reasoning - prev.reasoning };
      if (d.input < 0 || d.output < 0) { prev = Object.assign(ZERO(), cur); continue; } // counter reset
      Object.assign(prev, cur);
      day = x.timestamp.slice(0, 10);
      local.push({ delta: d, day, model, effort });
    }
  }
  if (!meta) continue;
  matched++;
  const agent = agentOf(meta);
  for (const e of local) {
    if (e.turn) continue;
    const key = by === "model" ? `${e.model} ${e.effort}`.trim() : by === "day" ? e.day : by === "session" ? `${sid.slice(0, 8)} ${e.day}` : agent;
    const g = bucket(key);
    g.sessions.add(sid);
    add(g, e.delta);
  }
  for (const e of local) {
    if (!e.turn || Date.parse(e.ts) < since || Date.parse(e.ts) > until) continue;
    const key = by === "model" ? `${e.model} ${e.effort}`.trim() : by === "day" ? e.ts.slice(0, 10) : by === "session" ? `${sid.slice(0, 8)} ${e.ts.slice(0, 10)}` : agent;
    bucket(key).turns++;
  }
}

const rows = [...groups.entries()].map(([k, g]) => ({
  key: k, sessions: g.sessions.size, turns: g.turns, input: g.input, cached: g.cached, output: g.output, reasoning: g.reasoning,
  cachePct: g.input ? Math.round((100 * g.cached) / g.input) : 0,
  thinkPct: g.output ? Math.round((100 * g.reasoning) / g.output) : 0,
  outPerTurn: g.turns ? Math.round(g.output / g.turns) : 0,
})).sort((a, b) => b.output - a.output);

if (args.includes("--json")) { console.log(JSON.stringify({ project, by, since: opt("since"), rows }, null, 2)); process.exit(0); }
const k = (n) => (n >= 1e6 ? `${(n / 1e6).toFixed(1)}M` : n >= 1e3 ? `${(n / 1e3).toFixed(1)}k` : String(n));
if (!rows.length) { console.log(`No Codex sessions for ${project}${opt("since") ? ` since ${opt("since")}` : ""} (scanned ${scanned} rollout files).`); process.exit(0); }
const w = Math.max(10, ...rows.map((r) => r.key.length));
console.log(`${by.padEnd(w)}  Sess  Turns   Output  Think%  Out/turn     Input  Cache%`);
console.log("-".repeat(w + 63));
for (const r of rows) console.log(`${r.key.padEnd(w)}  ${String(r.sessions).padStart(4)}  ${String(r.turns).padStart(5)}  ${k(r.output).padStart(7)}  ${String(r.thinkPct).padStart(5)}%  ${String(r.outPerTurn).padStart(8)}  ${k(r.input).padStart(8)}  ${String(r.cachePct).padStart(5)}%`);
console.log("-".repeat(w + 63));
console.log(`${matched} session(s) of ${scanned} rollout files for ${project}. Think% = reasoning share of output; Cache% = cached share of input.`);
console.log("Agent roles appear only when Codex records them in session_meta (spawned custom agents, guardian); your own sessions are 'main'. Per-agent detail inside one session is not logged.");
