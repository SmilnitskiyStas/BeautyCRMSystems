#!/usr/bin/env node
// Validate the shared configuration. Exits non-zero on any error (spec §56).
//   node scripts/validate-config.mjs
import { execFileSync } from "node:child_process";
import { existsSync } from "node:fs";
import { loadAgents, loadHookDefs, loadModelMap, knownSkillIds, p, projectRoot, isProjectMode } from "./lib/core.mjs";

const errors = [];
const warnings = [];

// 1. agents parse + schema (+ id/category/file consistency, done in loadAgents)
const { agents, problems } = loadAgents();
errors.push(...problems);

// 2. duplicate ids
const seen = new Map();
for (const a of agents) {
  if (seen.has(a.data.id)) errors.push(`duplicate agent id "${a.data.id}" (${seen.get(a.data.id)} and ${a.file})`);
  else seen.set(a.data.id, a.file);
}
const ids = new Set(agents.map((a) => a.data.id));

// 3. handoff targets resolve to real agents
for (const a of agents) {
  for (const t of a.data.handoff_targets || []) {
    if (!ids.has(t)) errors.push(`${a.file}: handoff_target "${t}" is not a known agent`);
  }
}

// 4. default tier is within allowed tiers
for (const a of agents) {
  if (a.data.allowed_model_tiers && !a.data.allowed_model_tiers.includes(a.data.default_model_tier)) {
    errors.push(`${a.file}: default_model_tier "${a.data.default_model_tier}" not in allowed_model_tiers`);
  }
}

// 5. skill references resolve (once skills/ is populated; warn only while empty)
const skills = knownSkillIds();
const skillSlugs = new Set([...skills].map((s) => s.split("/").pop()));
for (const a of agents) {
  for (const s of a.data.skills || []) {
    const slug = s.split("/").pop();
    if (skills.size === 0) { warnings.push(`${a.file}: skill "${s}" — skills/ not populated yet`); continue; }
    if (!skills.has(s) && !skillSlugs.has(slug)) errors.push(`${a.file}: skill "${s}" does not resolve under skills/`);
  }
}

// 5b. frontier is optional per runtime, but if any agent allows it the map must have it
const usesFrontier = agents.some((a) => (a.data.allowed_model_tiers || []).includes("frontier"));

// 6. model maps are well-formed for every declared runtime
for (const runtime of ["claude", "codex"]) {
  const mmPath = p("adapters", runtime, "model-map.yaml");
  if (!existsSync(mmPath)) { warnings.push(`adapters/${runtime}/model-map.yaml missing (add in its phase)`); continue; }
  const mm = loadModelMap(runtime);
  for (const tier of ["cheap", "standard", "reasoning", ...(usesFrontier ? ["frontier"] : [])]) {
    if (!mm.tiers || !mm.tiers[tier]) errors.push(`adapters/${runtime}/model-map.yaml: no model for tier "${tier}"`);
  }
}

// 7. hook definitions parse + schema + script existence + duplicate ids (loadHookDefs)
const { defs: hooks, problems: hookProblems } = loadHookDefs();
errors.push(...hookProblems);

// 8. adapter drift
try {
  const syncArgs = [p("scripts/sync-agents.mjs"), "--check"];
  if (isProjectMode()) syncArgs.push("--project", projectRoot());
  execFileSync(process.execPath, syncArgs, { stdio: "pipe" });
} catch (e) {
  errors.push("adapter drift: generated files are out of date — run `node scripts/sync-agents.mjs`");
}

// report
for (const w of warnings) console.warn(`⚠ ${w}`);
if (errors.length === 0) {
  console.log(
    `✓ validate-config: ${agents.length} agents, ${hooks.length} hooks OK` +
      `${warnings.length ? `, ${warnings.length} warning(s)` : ""}`
  );
  process.exit(0);
}
console.error(`\n✗ validate-config: ${errors.length} error(s):`);
for (const e of errors) console.error(`  - ${e}`);
process.exit(1);
