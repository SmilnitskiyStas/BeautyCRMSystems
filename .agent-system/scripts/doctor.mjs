#!/usr/bin/env node
// Environment + configuration health check (spec §55).
//   node scripts/doctor.mjs
import { execFileSync } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { loadAgents, loadHookDefs, knownSkillIds, templateNames, p, listFiles } from "./lib/core.mjs";

const ok = (m) => console.log(`  ✓ ${m}`);
const warn = (m) => { console.log(`  ⚠ ${m}`); state.warn++; };
const bad = (m) => { console.log(`  ✗ ${m}`); state.bad++; };
const state = { warn: 0, bad: 0 };

function cliVersion(cmd) {
  try {
    // shell:true so Windows resolves .cmd / .exe via PATHEXT
    return execFileSync(cmd, ["--version"], { stdio: "pipe", shell: true }).toString().trim();
  } catch {
    return null;
  }
}

console.log("Runtimes");
for (const [cmd, label] of [["claude", "Claude Code"], ["codex", "Codex CLI"]]) {
  const v = cliVersion(cmd);
  if (v) ok(`${label}: ${v}`);
  else warn(`${label}: not found on PATH — its adapter cannot be verified`);
}

console.log("\nAdapters");
for (const rt of ["claude", "codex"]) {
  const mm = p("adapters", rt, "model-map.yaml");
  if (existsSync(mm)) ok(`adapters/${rt}/model-map.yaml present`);
  else warn(`adapters/${rt}/ not set up yet`);
}
if (existsSync(p("adapters/COMPATIBILITY.md"))) ok("adapters/COMPATIBILITY.md present");
else warn("adapters/COMPATIBILITY.md missing");

console.log("\nShared definitions");
const { agents, problems } = loadAgents();
if (problems.length) for (const pr of problems) bad(pr);
else ok(`${agents.length} agents parse and validate`);

const skills = knownSkillIds();
const skillSlugs = new Set([...skills].map((s) => s.split("/").pop()));
let brokenSkills = 0;
for (const a of agents) {
  for (const s of a.data.skills || []) {
    const slug = s.split("/").pop();
    if (skills.size && !skills.has(s) && !skillSlugs.has(slug)) { bad(`${a.file}: skill "${s}" unresolved`); brokenSkills++; }
  }
}
if (skills.size === 0) warn("skills/ not populated yet (Phase 3 reorg pending)");
else if (!brokenSkills) ok(`skill references resolve (${skills.size} skills)`);

console.log("\nHooks");
const { defs: hooks, problems: hookProblems } = loadHookDefs();
if (hookProblems.length) for (const pr of hookProblems) bad(pr);
else ok(`${hooks.length} hook(s) parse and validate`);

console.log("\nTemplates");
const tpl = templateNames();
for (const need of ["agent-template", "task-template", "artifact-template", "adr-template", "context-package-template"]) {
  if (tpl.has(need)) ok(`${need}.md`);
  else bad(`templates/${need}.md missing`);
}

console.log("\nWorkflow policies");
const wf = new Set(listFiles("workflow").map((f) => f.split("/").pop()));
for (const need of ["model-routing.md", "task-lifecycle.md", "context-policy.md", "review-policy.md", "human-gates.md"]) {
  wf.has(need) ? ok(need) : bad(`workflow/${need} missing`);
}

console.log("\nGenerated files");
try {
  execFileSync(process.execPath, [p("scripts/sync-agents.mjs"), "--check"], { stdio: "pipe" });
  ok("adapter output is up to date");
} catch {
  bad("adapter output is stale — run `node scripts/sync-agents.mjs`");
}

console.log(`\n${state.bad ? "✗" : "✓"} doctor: ${state.bad} error(s), ${state.warn} warning(s)`);
process.exit(state.bad ? 1 : 0);
