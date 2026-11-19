// Claude Code adapter: shared agent definition -> .claude/agents/<id>.md
// Verified field set against Claude Code 2.1.197 sub-agent docs (2026-08-28):
//   name, description, tools, disallowedTools, model, skills, effort, maxTurns, omitClaudeMd,
// autoCompactWindow, isolation, experimental.cacheTtl (re-verified against the docs 2026-10-31;
// unknown keys are silently dropped by Claude Code, so newer fields are safe on older builds).
import { resolveModel, isProjectMode } from "../../scripts/lib/core.mjs";
import { dumpYaml } from "../../scripts/lib/yaml.mjs";
import { rewriteRefs } from "../../scripts/lib/refs.mjs";

export const TARGET_DIR = ".claude/agents";
export const GEN_HEADER_MARK = "<!-- GENERATED";

const CAP_TO_TOOLS = {
  read: ["Read"],
  search: ["Grep", "Glob"],
  edit: ["Write", "Edit"],
  shell: ["Bash"],
  "run-tests": ["Bash"],
  web: ["WebFetch", "WebSearch"],
  "spawn-subagent": ["Agent"],
  "git-write": ["Bash"],
  skills: ["Skill"],
};

function toolList(caps, hasSkills) {
  if (!caps || caps.length === 0) return null; // inherit all
  const set = [];
  const all = hasSkills ? [...caps, "skills"] : caps; // a tools allowlist hides the Skill tool unless listed
  for (const c of all) for (const t of CAP_TO_TOOLS[c] || []) if (!set.includes(t)) set.push(t);
  return set;
}

function description(data) {
  let d = data.summary.trim();
  if (data.when_to_use) d += ` ${data.when_to_use.trim()}`;
  return d.replace(/\s+/g, " ").trim();
}

export function generate(agent, { modelMap, refPrefix = null, noPreload = false }) {
  const { data, body } = agent;
  const { model, effort } = resolveModel(data.default_model_tier, modelMap);

  const fm = { name: data.id, description: description(data), model };
  if (effort) fm.effort = effort;
  const tools = toolList(data.tools, (data.skills || []).length > 0);
  if (tools) fm.tools = tools.join(", ");
  if (data.read_only) fm.disallowedTools = "Write, Edit";
  // Only preloaded skills are injected in full at spawn. The rest stay discoverable through
  // the Skill tool by their description — that is the token saving. Without preload_skills,
  // fall back to preloading everything (legacy behaviour).
  const preload = noPreload ? [] : (data.preload_skills ?? data.skills ?? []);
  if (preload.length) fm.skills = preload.map((s) => s.split("/").pop());
  const h = data.runtime_hints || {};
  if (h.max_turns) fm.maxTurns = h.max_turns;
  if (h.omit_project_instructions) fm.omitClaudeMd = true;
  if (h.auto_compact_window) fm.autoCompactWindow = h.auto_compact_window;
  if (h.isolation) fm.isolation = h.isolation;
  if (h.cache_ttl) fm.experimental = { cacheTtl: h.cache_ttl };

  const header =
    `<!-- GENERATED — DO NOT EDIT DIRECTLY. Source: ${agent.file}\n` +
    `     Regenerate with: node scripts/sync-agents.mjs -->\n`;

  const tierNote =
    `\n> Model tier: **${data.default_model_tier}** ` +
    `(allowed: ${data.allowed_model_tiers.join(", ")}). ` +
    `Escalation and downgrade rules: workflow/model-routing.md.\n`;

  const out = `---\n${dumpYaml(fm)}\n---\n\n${header}${tierNote}\n${body.trim()}\n`;
  return rewriteRefs(out, { projectMode: isProjectMode(), prefix: refPrefix });
}
