#!/usr/bin/env node
// Agent effectiveness report (spec §48). Parses task logs and summarizes per agent /
// tier: task count, retry rate, human-correction rate, review findings.
//
//   node scripts/report-agent-usage.mjs [<task-log-dir>]
//
// Default dir: .claude/logs/tasks (falls back to .ai/logs/tasks). Task logs follow
// templates/task-log-template.md — a YAML `model:` block and a YAML `metrics:` block.
if (process.argv.includes("--runs")) {
  await import("./report-agent-runs.mjs");
  await new Promise(() => {}); // report-agent-runs exits the process itself
}
import { readFileSync, readdirSync, existsSync } from "node:fs";
import { join } from "node:path";
import { p } from "./lib/core.mjs";
import { parseYaml } from "./lib/yaml.mjs";

const dir = process.argv[2]
  ? process.argv[2]
  : ["/.claude/logs/tasks", "/.ai/logs/tasks"].map((d) => p("." + d)).find(existsSync);

if (!dir || !existsSync(dir)) {
  console.error("No task-log directory found. Pass one explicitly, or create .claude/logs/tasks/.");
  process.exit(1);
}

function yamlBlock(text, key) {
  // grab a fenced ```yaml block that contains `<key>:`
  const blocks = [...text.matchAll(/```ya?ml\n([\s\S]*?)```/g)].map((m) => m[1]);
  for (const b of blocks) if (b.includes(`${key}:`)) {
    try { return parseYaml(b); } catch { return null; }
  }
  return null;
}

const rows = new Map(); // `${agent}|${tier}` -> aggregates

for (const f of readdirSync(dir).filter((x) => x.endsWith(".md"))) {
  const text = readFileSync(join(dir, f), "utf8").replace(/\r\n/g, "\n");
  const agent = (text.match(/\*\*Agent:\*\*\s*([a-z0-9-]+)/) || [])[1] || "unknown";
  const model = yamlBlock(text, "tier")?.model || yamlBlock(text, "runtime") || {};
  const tier = model.tier || "?";
  const metrics = yamlBlock(text, "retry_count") || {};

  const key = `${agent}|${tier}`;
  const r = rows.get(key) || { tasks: 0, retries: 0, humanFixes: 0, findings: 0 };
  r.tasks++;
  r.retries += Number(metrics.retry_count || 0);
  r.humanFixes += Number(metrics.human_corrections || 0);
  r.findings += Number(metrics.review_findings || 0);
  rows.set(key, r);
}

if (rows.size === 0) {
  console.log("No task logs parsed.");
  process.exit(0);
}

const pct = (n, d) => (d ? `${Math.round((100 * n) / d)}%` : "—");
console.log("Agent".padEnd(24), "Tier".padEnd(10), "Tasks".padStart(6), "Retry".padStart(7), "HumanFix".padStart(9), "Findings".padStart(9));
console.log("-".repeat(70));
for (const [key, r] of [...rows].sort((a, b) => b[1].tasks - a[1].tasks)) {
  const [agent, tier] = key.split("|");
  console.log(
    agent.padEnd(24),
    tier.padEnd(10),
    String(r.tasks).padStart(6),
    pct(r.retries, r.tasks).padStart(7),
    pct(r.humanFixes, r.tasks).padStart(9),
    String(r.findings).padStart(9)
  );
}
console.log(`\nGoal: find the cheapest tier that reliably delivers the needed quality (spec §48).`);
