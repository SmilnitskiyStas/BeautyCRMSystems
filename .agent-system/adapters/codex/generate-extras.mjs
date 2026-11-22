// Codex CLI adapter, part 2 — everything besides .codex/agents/*.toml:
//   hooks   hooks/*.md + hooks/scripts/** -> .codex/hooks.available.json (inert) + .codex/hooks/**
//           .codex/hooks.json is written only by the explicit opt-in: apply-hook-settings.mjs --codex --apply
//   skills  skills/**/SKILL.md            -> .agents/skills/<slug>/SKILL.md
//
// Verified against codex-cli 0.171.0 (live `codex exec`, 2026-11-20; see adapters/COMPATIBILITY.md):
//   - .codex/hooks.json has the same shape as Claude's `hooks` settings block; commands run with the
//     session cwd; SessionStart/PreToolUse/Stop fire; hook input carries session_id, cwd, hook_event_name,
//     tool_name, tool_input (+ turn_id, model). A project's hooks need the project to be trusted and the
//     hooks to be approved once (hook trust).
//   - SessionStart matches `startup|resume` only; PreToolUse/PostToolUse see shell commands only
//     (edits go through apply_patch and are not intercepted); no SubagentStop / PreCompact / PostCompact.
//   - Skills live in .agents/skills/<name>/SKILL.md; Claude-only frontmatter has no effect there.
import { readFileSync, writeFileSync, existsSync, mkdirSync, readdirSync, rmSync } from "node:fs";
import { join, dirname } from "node:path";
import { p, pp, isProjectMode, loadHookDefs, hookScriptFiles } from "../../scripts/lib/core.mjs";
import { listSharedSkills, generateSkill } from "../claude/generate-skills.mjs";

export const HOOKS_DIR = ".codex/hooks";
export const HOOKS_AVAILABLE = ".codex/hooks.available.json";
export const SKILLS_DIR = ".agents/skills";

const CODEX_EVENTS = new Set(["SessionStart", "PreToolUse", "PostToolUse", "Stop", "UserPromptSubmit"]);
const MATCHER_EVENTS = new Set(["SessionStart", "PreToolUse", "PostToolUse"]);
// Skills that depend on Claude Code features (shell-injected output, slash-command-only, /context,
// Claude → Codex delegation) and would be wrong or useless inside Codex.
export const CLAUDE_ONLY_SKILLS = new Set(["agent-stats", "update-agents", "ctx-check", "delegate-to-codex"]);
const GEN_MARK = "x-generated-from:";

const out = (...s) => (isProjectMode() ? pp(...s) : p(...s));
const scriptRel = (scriptPath) => scriptPath.replace(/^hooks\/scripts\//, "");

export function buildHooksJson(defs) {
  const hooks = {};
  for (const d of [...defs].sort((a, b) => a.data.id.localeCompare(b.data.id))) {
    if (d.data.codex_status !== "supported" || !CODEX_EVENTS.has(d.data.event)) continue;
    const entry = { hooks: [{ type: "command", command: `node ${HOOKS_DIR}/${scriptRel(d.data.script)}` }] };
    const matcher = d.data.codex_matcher ?? d.data.matcher;
    if (matcher && MATCHER_EVENTS.has(d.data.event)) entry.matcher = matcher;
    (hooks[d.data.event] ||= []).push(entry);
  }
  return { hooks };
}

export function codexSkillText(skill) {
  const text = generateSkill(skill).split(".claude/hooks/lib/").join(`${HOOKS_DIR}/lib/`);
  const m = text.match(/^---\n([\s\S]*?)\n---\n([\s\S]*)$/);
  if (!m) return text;
  const fm = m[1]
    .split("\n")
    .filter((l) => !/^(allowed-tools|disable-model-invocation|argument-hint|model|effort):/.test(l) && !l.startsWith("#"))
    .join("\n");
  return `---\n${fm}\n---\n${m[2]}`;
}

function walk(base, prefix = "") {
  if (!existsSync(base)) return [];
  return readdirSync(base, { withFileTypes: true }).flatMap((e) =>
    e.isDirectory() ? walk(join(base, e.name), `${prefix}${e.name}/`) : [`${prefix}${e.name}`]
  );
}

export function syncCodexExtras({ check, skillFilter = null }) {
  let drift = 0;
  const changes = [];
  const write = (target, content, label) => {
    const cur = existsSync(target) ? readFileSync(target, "utf8").replace(/\r\n/g, "\n") : null;
    if (cur === content) return;
    drift++;
    changes.push(`${cur === null ? "create" : "update"} ${label}`);
    if (!check) { mkdirSync(dirname(target), { recursive: true }); writeFileSync(target, content); }
  };

  // hooks
  const { defs, problems } = loadHookDefs();
  if (problems.length) return { drift: 0, changes: [], problems, hooks: 0, skills: 0 };
  const supported = defs.filter((d) => d.data.codex_status === "supported" && CODEX_EVENTS.has(d.data.event));
  const wantedScripts = new Set([...supported.map((d) => scriptRel(d.data.script))]);
  for (const f of hookScriptFiles()) {
    const rel = scriptRel(f);
    if (rel.startsWith("lib/") || wantedScripts.has(rel)) write(out(HOOKS_DIR, rel), readFileSync(p(f), "utf8"), `${HOOKS_DIR}/${rel}`);
    else wantedScripts.delete(rel);
  }
  const keep = new Set(hookScriptFiles().map(scriptRel).filter((r) => r.startsWith("lib/") || wantedScripts.has(r)));
  for (const rel of walk(out(HOOKS_DIR))) {
    if (!rel.endsWith(".mjs") || keep.has(rel)) continue;
    drift++;
    changes.push(`delete ${HOOKS_DIR}/${rel} (no source)`);
    if (!check) rmSync(out(HOOKS_DIR, rel));
  }
  const hooksJson = buildHooksJson(defs);
  if (Object.keys(hooksJson.hooks).length) write(out(HOOKS_AVAILABLE), `${JSON.stringify(hooksJson, null, 2)}\n`, HOOKS_AVAILABLE);
  else if (existsSync(out(HOOKS_AVAILABLE))) { drift++; changes.push(`delete ${HOOKS_AVAILABLE}`); if (!check) rmSync(out(HOOKS_AVAILABLE)); }

  // skills
  const skills = listSharedSkills().filter((s) => (!skillFilter || skillFilter(s.id, s)) && !CLAUDE_ONLY_SKILLS.has(s.slug));
  const slugs = new Set();
  for (const s of skills) {
    slugs.add(s.slug);
    write(out(SKILLS_DIR, s.slug, "SKILL.md"), codexSkillText(s), `${SKILLS_DIR}/${s.slug}/SKILL.md`);
  }
  const sdir = out(SKILLS_DIR);
  if (existsSync(sdir)) {
    for (const d of readdirSync(sdir)) {
      const f = join(sdir, d, "SKILL.md");
      if (slugs.has(d) || !existsSync(f) || !readFileSync(f, "utf8").includes(GEN_MARK)) continue;
      drift++;
      changes.push(`delete ${SKILLS_DIR}/${d}/ (no source)`);
      if (!check) rmSync(join(sdir, d), { recursive: true });
    }
  }
  return { drift, changes, problems: [], hooks: supported.length, skills: skills.length };
}
