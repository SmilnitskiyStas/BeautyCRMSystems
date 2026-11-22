#!/usr/bin/env node
// Regenerate runtime adapter files from the shared agents/ + skills/ source of truth.
//
//   node scripts/sync-agents.mjs                     # write .claude/* and .codex/* here
//   node scripts/sync-agents.mjs --check             # exit 1 if anything is out of date (CI)
//   node scripts/sync-agents.mjs --target claude
//   node scripts/sync-agents.mjs --project <dir>     # emit into a consuming project's .claude/
//   node scripts/sync-agents.mjs --project <dir> --profile backend-service   # only that roster (profiles/*.yaml); remembered in <dir>/.ai/agent-profile
//
// Never overwrites project runtime state (.claude/docs, .claude/tasks, .claude/logs,
// .claude/commands, .claude/settings*.json, .codex/config.toml). Hook definitions
// compile to .claude/hooks/** and .claude/settings.hooks.json (a mergeable fragment,
// not settings.json itself) — see scripts/apply-hook-settings.mjs for the explicit
// opt-in step that actually wires them into settings.json.
import { readFileSync, writeFileSync, existsSync, mkdirSync, readdirSync, rmSync } from "node:fs";
import { join } from "node:path";
import { p, pp, isProjectMode, loadAgents, loadModelMap, copySupportDirs, applyProfile } from "./lib/core.mjs";
import * as claude from "../adapters/claude/generate.mjs";
import { syncSkills } from "../adapters/claude/generate-skills.mjs";
import { syncHooks } from "../adapters/claude/generate-hooks.mjs";
import { syncCodex } from "../adapters/codex/generate.mjs";
import { syncCodexExtras } from "../adapters/codex/generate-extras.mjs";
import { syncStamp } from "./lib/version.mjs";

const args = process.argv.slice(2);
const check = args.includes("--check");
const only = args.includes("--target") ? args[args.indexOf("--target") + 1] : null;

const TARGETS = ["claude", "codex"];

const out = (...s) => (isProjectMode() ? pp(...s) : p(...s));

function syncClaude({ agents, modelMap, skillFilter }) {
  const dir = out(claude.TARGET_DIR);
  mkdirSync(dir, { recursive: true });
  const wanted = new Map();
  for (const a of agents) wanted.set(`${a.data.id}.md`, claude.generate(a, { modelMap }));

  let drift = 0;
  const changes = [];
  for (const [fname, content] of wanted) {
    const target = join(dir, fname);
    const cur = existsSync(target) ? readFileSync(target, "utf8").replace(/\r\n/g, "\n") : null;
    if (cur !== content) {
      drift++;
      changes.push(`${cur === null ? "create" : "update"} ${claude.TARGET_DIR}/${fname}`);
      if (!check) writeFileSync(target, content);
    }
  }
  for (const f of readdirSync(dir)) {
    if (!f.endsWith(".md") || wanted.has(f)) continue;
    if (readFileSync(join(dir, f), "utf8").includes(claude.GEN_HEADER_MARK)) {
      drift++;
      changes.push(`delete ${claude.TARGET_DIR}/${f} (no source)`);
      if (!check) rmSync(join(dir, f));
    }
  }

  const sk = syncSkills({ check, skillFilter });
  const hk = syncHooks({ check });
  const sup = copySupportDirs({ check });
  return {
    drift: drift + sk.drift + hk.drift + sup.drift,
    changes: [...changes, ...sk.changes, ...hk.changes, ...sup.changes],
    count: agents.length,
    skills: sk.count,
    hooks: hk.count,
    problems: hk.problems,
  };
}

// remember an explicit --profile so a plain re-sync keeps the roster
if (!check && isProjectMode() && args.includes("--profile")) {
  mkdirSync(pp(".ai"), { recursive: true });
  writeFileSync(pp(".ai", "agent-profile"), args[args.indexOf("--profile") + 1] + "\n");
}

let totalDrift = 0;
let totalErrors = 0;

for (const name of TARGETS) {
  if (only && only !== name) continue;

  const { agents: allAgents, problems } = loadAgents();
  if (problems.length) {
    console.error(`✗ ${name}: shared definitions have ${problems.length} problem(s):`);
    for (const pr of problems) console.error(`  - ${pr}`);
    totalErrors += problems.length;
    continue;
  }

  let agents, skillFilter, profile;
  try {
    ({ agents, skillFilter, name: profile } = applyProfile(allAgents));
  } catch (e) {
    console.error(`✗ ${e.message}`);
    totalErrors++;
    continue;
  }

  let modelMap;
  try {
    modelMap = loadModelMap(name);
  } catch {
    console.log(`· ${name}: adapters/${name}/model-map.yaml missing — skipped`);
    continue;
  }

  let r;
  if (name === "claude") r = syncClaude({ agents, modelMap, skillFilter });
  else {
    r = syncCodex({ check, agents, modelMap });
    const ex = syncCodexExtras({ check, skillFilter });
    r.drift += ex.drift;
    r.changes = [...r.changes, ...ex.changes];
    r.skills = ex.skills;
    r.hooks = ex.hooks;
    r.problems = ex.problems;
  }
  if (r.problems?.length) {
    console.error(`✗ ${name}: hook definitions have ${r.problems.length} problem(s):`);
    for (const pr of r.problems) console.error(`  - ${pr}`);
    totalErrors += r.problems.length;
    continue;
  }
  const st = syncStamp({ check, dirName: name === "claude" ? ".claude" : ".codex", runtime: name });
  r.drift += st.drift;
  r.changes = [...(r.changes || []), ...st.changes];
  totalDrift += r.drift;

  if (r.drift === 0) {
    console.log(
      `✓ ${name}: up to date (${r.count} agents` +
        `${r.skills != null ? `, ${r.skills} skills` : ""}` +
        `${r.hooks != null ? `, ${r.hooks} hooks` : ""})`
    );
  } else {
    console.log(`${check ? "✗" : "✓"} ${name}: ${r.drift} file(s) ${check ? "out of date" : "written"}`);
    for (const c of r.changes) console.log(`  - ${c}`);
  }
}

if (totalErrors) process.exit(2);
if (check && totalDrift) {
  console.error(`\nRun: node scripts/sync-agents.mjs`);
  process.exit(1);
}
process.exit(0);
