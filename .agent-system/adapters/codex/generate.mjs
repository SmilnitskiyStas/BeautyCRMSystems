// Codex CLI adapter: shared agent definition -> .codex/agents/<id>.toml
// Verified field set against codex-cli 0.144.1 (strings in codex.exe): name, description,
// developer_instructions, model, model_reasoning_effort, sandbox_mode, mcp_servers.
import { readFileSync, writeFileSync, existsSync, mkdirSync, readdirSync, rmSync } from "node:fs";
import { join } from "node:path";
import { p, pp, isProjectMode, resolveModel } from "../../scripts/lib/core.mjs";
import { dumpToml } from "../../scripts/lib/toml.mjs";
import { rewriteRefs } from "../../scripts/lib/refs.mjs";
import { listSharedSkills } from "../claude/generate-skills.mjs";

export const TARGET_DIR = ".codex/agents";
const GEN_MARK = "# GENERATED — DO NOT EDIT";

const EDIT_CAPS = new Set(["edit", "shell", "run-tests", "git-write"]);

function sandboxMode(agent, modelMap) {
  if (agent.data.read_only) return "read-only";
  const caps = agent.data.tools || [];
  if (caps.some((c) => EDIT_CAPS.has(c))) return "workspace-write";
  return modelMap.sandbox_default || "read-only";
}

function developerInstructions(agent, skillsById) {
  const { data, body } = agent;
  let instr = body.trim();

  // guardrails block (Codex has no separate must_not field)
  if (data.must_not?.length) {
    instr += `\n\n## Never\n` + data.must_not.map((m) => `- ${m}`).join("\n");
  }
  // model tier note
  instr +=
    `\n\n## Model tier\n` +
    `Default: ${data.default_model_tier} (allowed: ${data.allowed_model_tiers.join(", ")}). ` +
    `Escalation/downgrade: workflow/model-routing.md.`;
  // skills — Codex does not preload; point at them explicitly
  if (data.skills?.length) {
    instr +=
      `\n\n## Skills\nApply these skills when relevant:\n` +
      data.skills
        .map((s) => {
          const slug = s.split("/").pop();
          const meta = skillsById.get(slug);
          return `- \`${slug}\`${meta ? ` — ${meta.desc}` : ""}`;
        })
        .join("\n");
  }
  // handoffs
  if (data.handoff_targets?.length) {
    instr += `\n\n## Handoff\nMay hand off to: ${data.handoff_targets.join(", ")}. A blocker always goes through project-manager.`;
  }
  return instr;
}

function skillIndex() {
  const idx = new Map();
  for (const sk of listSharedSkills()) {
    const src = readFileSync(sk.abs, "utf8");
    const m = src.match(/description:\s*>-?\s*\n([\s\S]*?)\n---/) || src.match(/description:\s*"?([^"\n]+)"?/);
    const d = (m ? m[1] : "").replace(/\s+/g, " ").trim();
    idx.set(sk.slug, { desc: d.length > 220 ? d.slice(0, 217).replace(/\s+\S*$/, "") + "…" : d });
  }
  return idx;
}

export function generate(agent, { modelMap }) {
  const { data } = agent;
  const { model, effort } = resolveModel(data.default_model_tier, modelMap);
  const skillsById = skillIndex();

  let description = data.summary.trim();
  if (data.when_to_use) description += ` ${data.when_to_use.trim()}`;

  const toml = {
    name: data.id,
    description: description.replace(/\s+/g, " ").trim(),
    model,
    model_reasoning_effort: effort || "medium",
    sandbox_mode: sandboxMode(agent, modelMap),
    developer_instructions: developerInstructions(agent, skillsById),
  };

  const text = `${GEN_MARK}. Source: ${agent.file}. Regenerate: node scripts/sync-agents.mjs\n\n${dumpToml(toml)}`;
  return rewriteRefs(text, { projectMode: isProjectMode() });
}

// AGENTS.md roster section (< 150 lines total when combined with the template)
export function generateRoster(agents) {
  const byCat = {};
  for (const a of agents) (byCat[a.data.category] ||= []).push(a);
  const lines = [`${GEN_MARK} BELOW THIS LINE. Regenerate: node scripts/sync-agents.mjs`, ""];
  for (const cat of ["control", "engineering", "product", "documentation"]) {
    if (!byCat[cat]) continue;
    lines.push(`### ${cat}`);
    for (const a of byCat[cat].sort((x, y) => x.data.id.localeCompare(y.data.id))) {
      lines.push(`- **${a.data.id}** (${a.data.default_model_tier}) — ${a.data.summary}`);
    }
    lines.push("");
  }
  return lines.join("\n");
}

export function syncCodex({ check, agents, modelMap }) {
  const dir = isProjectMode() ? pp(TARGET_DIR) : p(TARGET_DIR);
  mkdirSync(dir, { recursive: true });
  const wanted = new Map();
  for (const a of agents) wanted.set(`${a.data.id}.toml`, generate(a, { modelMap }));

  let drift = 0;
  const changes = [];
  for (const [fname, content] of wanted) {
    const target = join(dir, fname);
    const cur = existsSync(target) ? readFileSync(target, "utf8") : null;
    if (cur !== content) {
      drift++;
      changes.push(`${cur === null ? "create" : "update"} ${TARGET_DIR}/${fname}`);
      if (!check) writeFileSync(target, content);
    }
  }
  for (const f of readdirSync(dir)) {
    if (!f.endsWith(".toml") || wanted.has(f)) continue;
    if (readFileSync(join(dir, f), "utf8").includes(GEN_MARK)) {
      drift++;
      changes.push(`delete ${TARGET_DIR}/${f} (no source)`);
      if (!check) rmSync(join(dir, f));
    }
  }

  // AGENTS.md roster block (between markers) — only if AGENTS.md exists in the project
  const agentsMd = isProjectMode() ? pp("AGENTS.md") : p("AGENTS.md");
  if (existsSync(agentsMd)) {
    const roster = generateRoster(agents);
    const cur = readFileSync(agentsMd, "utf8");
    const marker = "<!-- roster:start -->";
    const endMarker = "<!-- roster:end -->";
    if (cur.includes(marker) && cur.includes(endMarker)) {
      const next = cur.replace(
        new RegExp(`${marker}[\\s\\S]*?${endMarker}`),
        `${marker}\n${roster}\n${endMarker}`
      );
      if (next !== cur) {
        drift++;
        changes.push("update AGENTS.md roster block");
        if (!check) writeFileSync(agentsMd, next);
      }
    }
  }

  return { drift, changes, count: agents.length };
}
