#!/usr/bin/env node
// Build a Claude Code PLUGIN from the shared library, for trying it in a real session.
//
//   node scripts/build-plugin.mjs [--out dist/plugin/ai-agent-library] [--profile backend-service]
//                                 [--hooks compact-snapshot,compact-restore,path-deny-list,agent-run-log]
//   claude plugin validate dist/plugin/ai-agent-library
//   claude --plugin-dir dist/plugin/ai-agent-library
//
// Plugin layout (docs: plugins-reference): .claude-plugin/plugin.json, agents/, skills/,
// hooks/hooks.json + hooks/scripts/, plus workflow/ schemas/ templates/ that the agent
// bodies reference as ${CLAUDE_PLUGIN_ROOT}/... Plugin components are namespaced under the
// plugin name, so preloaded `skills:` frontmatter is dropped (skills stay discoverable).
// Only hooks that never block by default are included unless --hooks says otherwise.
import { mkdirSync, writeFileSync, rmSync, existsSync, cpSync, readdirSync, statSync, readFileSync } from "node:fs";
import { join, resolve, dirname } from "node:path";
import { p, loadAgents, loadModelMap, applyProfile, loadHookDefs, hookScriptFiles } from "./lib/core.mjs";
import * as claude from "../adapters/claude/generate.mjs";
import { listSharedSkills, generateSkill } from "../adapters/claude/generate-skills.mjs";

const args = process.argv.slice(2);
const opt = (name, dflt) => (args.includes(name) ? args[args.indexOf(name) + 1] : dflt);

const NAME = "ai-agent-library";
const out = resolve(opt("--out", `dist/plugin/${NAME}`));
const hookIds = new Set(opt("--hooks", "compact-snapshot,compact-restore,path-deny-list,agent-run-log").split(",").filter(Boolean));
const PREFIX = "${CLAUDE_PLUGIN_ROOT}";

const { agents: all, problems } = loadAgents();
if (problems.length) { console.error(problems.join("\n")); process.exit(2); }
const { agents, skillFilter, name: profile } = applyProfile(all);
const modelMap = loadModelMap("claude");

if (existsSync(out)) rmSync(out, { recursive: true });
const w = (rel, content) => { const f = join(out, rel); mkdirSync(dirname(f), { recursive: true }); writeFileSync(f, content); };

// agents
for (const a of agents) w(`agents/${a.data.id}.md`, claude.generate(a, { modelMap, refPrefix: PREFIX, noPreload: true }));

// skills (flat slug dirs, same as the project adapter)
const skills = listSharedSkills().filter((s) => !skillFilter || skillFilter(s.id, s));
// Skills that run bundled scripts reference them under .claude/hooks/lib/ (project layout);
// in a plugin the same files live under hooks/scripts/lib/.
const pluginPaths = (text) => text.split(".claude/hooks/lib/").join(`${PREFIX}/hooks/scripts/lib/`);
for (const s of skills) w(`skills/${s.slug}/SKILL.md`, pluginPaths(generateSkill(s, { refPrefix: PREFIX })));

// support docs referenced by bodies
for (const d of ["workflow", "schemas", "templates"]) cpSync(p(d), join(out, d), { recursive: true, filter: (src) => !/README\.md$/.test(src) || true });

// hooks
const { defs } = loadHookDefs();
const chosen = defs.filter((d) => hookIds.has(d.data.id));
const hooks = {};
for (const d of chosen) {
  const rel = d.data.script.replace(/^hooks\/scripts\//, "");
  const entry = { hooks: [{ type: "command", command: `node "${PREFIX}/hooks/scripts/${rel}"` }] };
  if (d.data.matcher) entry.matcher = d.data.matcher;
  (hooks[d.data.event] ||= []).push(entry);
}
if (chosen.length) {
  for (const f of hookScriptFiles()) w(f, readFileSync(p(f), "utf8"));
  w("hooks/hooks.json", JSON.stringify({ hooks }, null, 2) + "\n");
}

// manifest
w(".claude-plugin/plugin.json", JSON.stringify({
  name: NAME,
  displayName: "AI Agent Library (test build)",
  version: `0.0.0-test.${new Date().toISOString().slice(0, 10).replace(/-/g, "")}`,
  description: `Shared agent roster (${agents.length} agents, ${skills.length} skills${chosen.length ? `, ${chosen.length} hooks` : ""}) built from AI_agent_only${profile ? ` — profile ${profile}` : ""}`,
  author: { name: "SmilnitskiyStas" },
  repository: "https://github.com/SmilnitskiyStas/AI_agent_only_v2",
  keywords: ["agents", "workflow", "token-economy"],
}, null, 2) + "\n");

console.log(`✓ plugin built: ${out}`);
console.log(`  ${agents.length} agents, ${skills.length} skills, ${chosen.length} hooks (${[...hookIds].join(", ")})`);
