// Rewrite intra-library path references when generated files do not live at the repo root.
//
// library mode (self-hosting): agents/skills reference `workflow/x.md` and it resolves
//   because workflow/ sits at the repo root.
// project mode: everything the agents need is copied under <project>/.claude/, so
//   `workflow/x.md` must become `.claude/workflow/x.md`, and skill paths must point at
//   the flat generated `.claude/skills/<slug>/SKILL.md`.
// plugin mode (prefix given, e.g. "${CLAUDE_PLUGIN_ROOT}"): same, but rooted at the
//   plugin directory.

export function rewriteRefs(text, { projectMode, prefix = null }) {
  if (!prefix && !projectMode) return text;
  const root = prefix ? prefix : ".claude";
  return text
    // skills/<domain>/<slug>.md  or  skills/<domain>/<slug>
    .replace(/\bskills\/[a-z0-9-]+\/([a-z0-9-]+)(\/SKILL)?\.md\b/g, (_m, slug) => `${root}/skills/${slug}/SKILL.md`)
    .replace(/\bskills\/[a-z0-9-]+\/([a-z0-9-]+)\b(?!\/|\.)/g, (_m, slug) => `${root}/skills/${slug}`)
    // workflow/ schemas/ templates/  ->  <root>/<x>/
    .replace(/\b(workflow|schemas|templates)\/([a-z0-9-]+(?:\.[a-z0-9-]+)*\.(?:md|json))\b/g, (_m, d, f) => `${root}/${d}/${f}`);
}
