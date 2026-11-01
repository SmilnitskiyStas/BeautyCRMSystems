// Claude Code adapter: skills/<domain>/<slug>/SKILL.md -> .claude/skills/<slug>/SKILL.md
// Flat slug-named directories: Claude skill discovery and the agent `skills:` preload
// field both address skills by bare name. Slug uniqueness is enforced by validate-config.
import { readFileSync, writeFileSync, existsSync, mkdirSync, readdirSync, rmSync } from "node:fs";
import { join, basename } from "node:path";
import { p, pp, isProjectMode, LOCAL_SKILLS_DIR } from "../../scripts/lib/core.mjs";
import { rewriteRefs } from "../../scripts/lib/refs.mjs";

export const SKILLS_TARGET_DIR = ".claude/skills";
const GEN_MARK = "x-generated-from:";

function scan(base, srcPrefix, local) {
  if (!existsSync(base)) return [];
  return readdirSync(base, { recursive: true })
    .map((f) => String(f).replace(/\\/g, "/"))
    .filter((f) => f.endsWith("/SKILL.md"))
    .map((rel) => ({
      id: rel.slice(0, -"/SKILL.md".length),
      slug: rel.split("/").at(-2),
      abs: join(base, rel),
      src: `${srcPrefix}/${rel}`,
      local,
    }));
}

// Library skills plus (project mode) the project's own .ai/skills/**. A project skill with the
// same slug as a library skill replaces it.
export function listSharedSkills() {
  const lib = scan(p("skills"), "skills", false);
  if (!isProjectMode()) return lib;
  const local = scan(pp(LOCAL_SKILLS_DIR), LOCAL_SKILLS_DIR, true);
  const bySlug = new Set(local.map((s) => s.slug));
  return [...lib.filter((s) => !bySlug.has(s.slug)), ...local];
}

export function generateSkill(skill, { refPrefix = null } = {}) {
  const src = readFileSync(skill.abs, "utf8");
  // inject provenance into frontmatter
  const m = src.match(/^---\n([\s\S]*?)\n---\n([\s\S]*)$/);
  if (!m) throw new Error(`${skill.id}: SKILL.md has no frontmatter`);
  const fm = m[1].replace(/\n?$/, `\n${GEN_MARK} ${skill.local ? `project:${skill.id}` : `skills/${skill.id}/SKILL.md`}\n`);
  return rewriteRefs(`---\n${fm}---\n${m[2]}`, { projectMode: isProjectMode(), prefix: refPrefix });
}

export function syncSkills({ check, skillFilter = null }) {
  const dir = isProjectMode() ? pp(SKILLS_TARGET_DIR) : p(SKILLS_TARGET_DIR);
  mkdirSync(dir, { recursive: true });
  const skills = listSharedSkills().filter((s) => !skillFilter || skillFilter(s.id, s));

  const wantedSlugs = new Set();
  let drift = 0;
  const changes = [];

  for (const skill of skills) {
    if (wantedSlugs.has(skill.slug)) throw new Error(`duplicate skill slug "${skill.slug}"`);
    wantedSlugs.add(skill.slug);
    const target = join(dir, skill.slug, "SKILL.md");
    const content = generateSkill(skill);
    const current = existsSync(target) ? readFileSync(target, "utf8") : null;
    if (current !== content) {
      drift++;
      changes.push(`${current === null ? "create" : "update"} ${SKILLS_TARGET_DIR}/${skill.slug}/SKILL.md`);
      if (!check) { mkdirSync(join(dir, skill.slug), { recursive: true }); writeFileSync(target, content); }
    }
  }

  // remove generated skill dirs whose source is gone
  for (const d of readdirSync(dir)) {
    const sk = join(dir, d, "SKILL.md");
    if (!existsSync(sk)) continue;
    if (wantedSlugs.has(d)) continue;
    if (readFileSync(sk, "utf8").includes(GEN_MARK)) {
      drift++;
      changes.push(`delete ${SKILLS_TARGET_DIR}/${d}/ (no source)`);
      if (!check) rmSync(join(dir, d), { recursive: true });
    }
  }

  return { drift, changes, count: skills.length };
}
