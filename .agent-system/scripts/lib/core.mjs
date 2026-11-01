// Shared loading + resolution used by sync-agents / validate-config / doctor.
import { readFileSync, readdirSync, existsSync, mkdirSync, writeFileSync, rmSync } from "node:fs";
import { join, dirname, basename, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { parseFrontmatter, parseYaml } from "./yaml.mjs";
import { validate } from "./jsonschema.mjs";

// The library root — where agents/ skills/ workflow/ schemas/ templates/ live.
export const ROOT = join(dirname(fileURLToPath(import.meta.url)), "..", "..");
export const p = (...s) => join(ROOT, ...s);
export const rel = (abs) => relative(ROOT, abs).replace(/\\/g, "/");

// The project root — where generated .claude/ and .codex/ go. Same as ROOT when
// self-hosting; a consuming project passes --project / AI_AGENT_PROJECT_ROOT.
export function projectRoot() {
  const arg = process.argv.indexOf("--project");
  const raw = arg !== -1 ? process.argv[arg + 1] : process.env.AI_AGENT_PROJECT_ROOT;
  return raw ? resolve(raw) : ROOT;
}
export const isProjectMode = () => projectRoot() !== ROOT;
export const pp = (...s) => join(projectRoot(), ...s);

// Copy workflow/ schemas/ templates/ into <project>/.claude/<x>/ so the path refs in
// generated agent/skill bodies (rewritten to .claude/<x>/) resolve. Returns {drift, changes}.
export function copySupportDirs({ check }) {
  if (!isProjectMode()) return { drift: 0, changes: [] };
  const MARK = "<!-- vendored from the agent library — regenerate with sync-agents -->";
  let drift = 0;
  const changes = [];
  for (const dir of ["workflow", "schemas", "templates"]) {
    for (const f of listFiles(dir, "").filter((x) => /\.(md|json)$/.test(x))) {
      const src = readFileSync(p(f), "utf8");
      const body = f.endsWith(".md") ? `${MARK}\n${src}` : src;
      const target = pp(".claude", f);
      const cur = existsSync(target) ? readFileSync(target, "utf8") : null;
      if (cur !== body) {
        drift++;
        changes.push(`${cur === null ? "create" : "update"} .claude/${f}`);
        if (!check) { mkdirSync(dirname(target), { recursive: true }); writeFileSync(target, body); }
      }
    }
  }
  return { drift, changes };
}

export function listFiles(dir, ext = ".md") {
  const abs = p(dir);
  if (!existsSync(abs)) return [];
  return readdirSync(abs, { recursive: true })
    .map((f) => String(f).replace(/\\/g, "/"))
    .filter((f) => f.endsWith(ext))
    .map((f) => `${dir}/${f}`);
}

export function loadJson(relPath) {
  return JSON.parse(readFileSync(p(relPath), "utf8"));
}

// --- agents ---

const CATEGORY_DIR = "agents";

// Project-owned extensions (project mode only). They live OUTSIDE the vendored library so the
// library can be updated (git subtree pull / re-copy) without losing them:
//   <project>/.ai/agents/<category>/<id>.md   extra or overriding agents (same format/schema)
//   <project>/.ai/skills/<path>/SKILL.md      project skills (stack packs, domain skills)
//   <project>/.ai/agent-overlay.yaml          per-agent additions to library agents
export const LOCAL_AGENTS_DIR = ".ai/agents";
export const LOCAL_SKILLS_DIR = ".ai/skills";
export const OVERLAY_FILE = ".ai/agent-overlay.yaml";

function walkMd(absDir, rel) {
  if (!existsSync(absDir)) return [];
  return readdirSync(absDir, { recursive: true })
    .map((f) => String(f).replace(/\\/g, "/"))
    .filter((f) => f.endsWith(".md") && basename(f) !== "README.md")
    .map((f) => `${rel}/${f}`);
}

function readAgentFile(absPath, relName, schema, problems) {
  let fm;
  try {
    fm = parseFrontmatter(readFileSync(absPath, "utf8"));
  } catch (e) {
    problems.push(`${relName}: frontmatter parse error — ${e.message}`);
    return null;
  }
  const { data, body } = fm;
  for (const e of validate(data, schema)) problems.push(`${relName}: ${e}`);
  const expectedId = basename(relName, ".md");
  if (data.id && data.id !== expectedId) problems.push(`${relName}: id "${data.id}" does not match file name "${expectedId}"`);
  const parts = relName.split("/");
  const expectedCat = parts[parts.length - 2];
  if (data.category && data.category !== expectedCat)
    problems.push(`${relName}: category "${data.category}" does not match directory "${expectedCat}"`);
  if (!body || body.trim().length < 40) problems.push(`${relName}: body is missing or too short`);
  return { file: relName, data, body };
}

// overlay: { agents: { <id>: { skills_add: [], preload_add: [], runtime_hints: {}, <any other schema key> } } }
function applyOverlay(agents, problems) {
  const file = pp(OVERLAY_FILE);
  if (!existsSync(file)) return;
  let overlay;
  try {
    overlay = parseYaml(readFileSync(file, "utf8")) || {};
  } catch (e) {
    problems.push(`${OVERLAY_FILE}: parse error — ${e.message}`);
    return;
  }
  const schema = loadJson("schemas/agent.schema.json");
  // exclude_agents: library roles this project deliberately does not use (e.g. fullstack-developer
  // in a project with a separate backend). Unknown ids are an error — a typo must not silently keep a role.
  for (const id of overlay.exclude_agents || []) {
    const i = agents.findIndex((x) => x.data.id === id);
    if (i < 0) problems.push(`${OVERLAY_FILE}: exclude_agents lists unknown agent "${id}"`);
    else agents.splice(i, 1);
  }
  for (const [id, patch] of Object.entries(overlay.agents || {})) {
    const a = agents.find((x) => x.data.id === id);
    if (!a) { problems.push(`${OVERLAY_FILE}: unknown agent "${id}"`); continue; }
    const { skills_add = [], preload_add = [], runtime_hints, ...rest } = patch || {};
    const d = a.data;
    if (skills_add.length) d.skills = [...new Set([...(d.skills || []), ...skills_add])];
    if (preload_add.length) {
      d.preload_skills = [...new Set([...(d.preload_skills || []), ...preload_add])];
      d.skills = [...new Set([...(d.skills || []), ...preload_add])];
    }
    if (runtime_hints) d.runtime_hints = { ...(d.runtime_hints || {}), ...runtime_hints };
    Object.assign(d, rest);
    for (const e of validate(d, schema)) problems.push(`${OVERLAY_FILE} (${id}): ${e}`);
    a.overlay = true;
  }
}

// A role that is not part of this project's roster (excluded by the overlay or a profile) must not
// stay listed as a handoff target elsewhere — the receiving agent could never be spawned.
export function pruneHandoffs(agents) {
  const ids = new Set(agents.map((a) => a.data.id));
  for (const a of agents) {
    if (Array.isArray(a.data.handoff_targets)) a.data.handoff_targets = a.data.handoff_targets.filter((t) => ids.has(t));
  }
  return agents;
}

export function loadAgents() {
  const schema = loadJson("schemas/agent.schema.json");
  const agents = [];
  const problems = [];
  for (const f of listFiles(CATEGORY_DIR).filter((f) => basename(f) !== "README.md")) {
    const a = readAgentFile(p(f), f, schema, problems);
    if (a) agents.push(a);
  }
  if (isProjectMode()) {
    for (const f of walkMd(pp(LOCAL_AGENTS_DIR), LOCAL_AGENTS_DIR)) {
      const a = readAgentFile(pp(f), f, schema, problems);
      if (!a) continue;
      a.local = true;
      const i = agents.findIndex((x) => x.data.id === a.data.id);
      if (i >= 0) agents[i] = a; // a project agent with the same id deliberately overrides the library's
      else agents.push(a);
    }
    applyOverlay(agents, problems);
    pruneHandoffs(agents);
  }
  return { agents, problems };
}

// --- model map ---

export function loadModelMap(runtime) {
  const mm = parseYaml(readFileSync(p("adapters", runtime, "model-map.yaml"), "utf8"));
  return mm;
}

export function resolveModel(tier, modelMap) {
  const model = modelMap.tiers?.[tier];
  if (!model) throw new Error(`model-map has no entry for tier "${tier}"`);
  const effort = modelMap.effort?.[tier] || null;
  return { model, effort };
}

// --- skills ---

export function knownSkillIds() {
  // A skill id is a directory under skills/ that contains SKILL.md, expressed as
  // "<path-below-skills>" e.g. "workflow/create-adr" or "stacks/nextjs-marketing".
  const ids = new Set();
  const roots = [p("skills"), ...(isProjectMode() ? [pp(LOCAL_SKILLS_DIR)] : [])];
  for (const abs of roots) {
    if (!existsSync(abs)) continue;
    for (const f of readdirSync(abs, { recursive: true })) {
      const s = String(f).replace(/\\/g, "/");
      if (s.endsWith("/SKILL.md")) ids.add(s.slice(0, -"/SKILL.md".length));
    }
  }
  return ids;
}

export function templateNames() {
  return new Set(listFiles("templates").map((f) => basename(f, ".md")));
}

// --- hooks ---

const HOOKS_DIR = "hooks";

export function loadHookDefs() {
  const schema = loadJson("schemas/hook.schema.json");
  const files = listFiles(HOOKS_DIR).filter((f) => basename(f) !== "README.md");
  const defs = [];
  const problems = [];
  for (const f of files) {
    let fm;
    try {
      fm = parseFrontmatter(readFileSync(p(f), "utf8"));
    } catch (e) {
      problems.push(`${f}: frontmatter parse error — ${e.message}`);
      continue;
    }
    const { data, body } = fm;
    const schemaErrs = validate(data, schema);
    for (const e of schemaErrs) problems.push(`${f}: ${e}`);

    const expectedId = basename(f, ".md");
    if (data.id && data.id !== expectedId)
      problems.push(`${f}: id "${data.id}" does not match file name "${expectedId}"`);

    if (data.script && !existsSync(p(data.script)))
      problems.push(`${f}: script "${data.script}" does not exist`);

    if (!body || body.trim().length < 20)
      problems.push(`${f}: body is missing or too short`);

    defs.push({ file: f, data, body });
  }

  const seen = new Map();
  for (const d of defs) {
    if (seen.has(d.data.id))
      problems.push(`duplicate hook id "${d.data.id}" (${seen.get(d.data.id)} and ${d.file})`);
    else seen.set(d.data.id, d.file);
  }

  return { defs, problems };
}

// Every .mjs under hooks/scripts/, including hooks/scripts/lib/** — copied verbatim
// into .claude/hooks/** regardless of which hook definitions reference them, so a
// shared helper like hook-io.mjs ships alongside the scripts that import it.
export function hookScriptFiles() {
  return listFiles(`${HOOKS_DIR}/scripts`, ".mjs");
}

// --- profiles: which roster a project actually gets (agent descriptions load every turn) ---

export function profileName() {
  const i = process.argv.indexOf('--profile');
  if (i !== -1) return process.argv[i + 1];
  if (process.env.AI_AGENT_PROFILE) return process.env.AI_AGENT_PROFILE;
  // remembered per project so a plain re-sync keeps the roster
  const f = join(projectRoot(), '.ai', 'agent-profile');
  return existsSync(f) ? readFileSync(f, 'utf8').trim() || null : null;
}

export function loadProfile(name) {
  const file = p('profiles', name + '.yaml');
  if (!existsSync(file)) throw new Error('unknown profile "' + name + '" (no profiles/' + name + '.yaml)');
  return parseYaml(readFileSync(file, 'utf8'));
}

// Returns { agents, skillFilter } — skillFilter is null (all skills) or a predicate on skill id/slug.
export function applyProfile(agents) {
  const name = profileName();
  if (!name) return { agents, skillFilter: null, name: null };
  const prof = loadProfile(name);
  const inc = prof.include === 'all' ? null : new Set(prof.include || []);
  const missing = inc ? [...inc].filter((id) => !agents.some((a) => a.data.id === id)) : [];
  if (missing.length) throw new Error('profile "' + name + '" lists unknown agent(s): ' + missing.join(', '));
  // project-owned (local) agents are never filtered out by a library roster profile
  const kept = inc ? agents.filter((a) => inc.has(a.data.id) || a.local) : agents;
  if (!inc) return { agents: kept, skillFilter: null, name };
  const slugs = new Set();
  for (const a of kept) for (const s of [...(a.data.skills || []), ...(a.data.preload_skills || [])]) slugs.add(s.split('/').pop());
  // methodology skills are always available; domain/stack skills only if a kept agent uses them
  const skillFilter = (id, skill) => skill?.local || id.startsWith('workflow/') || id.startsWith('shared/') || slugs.has(id.split('/').pop());
  return { agents: pruneHandoffs(kept), skillFilter, name };
}
