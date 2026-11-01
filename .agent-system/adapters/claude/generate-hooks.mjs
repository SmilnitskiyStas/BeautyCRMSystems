// Claude Code adapter: hooks/*.md (+ hooks/scripts/**) -> .claude/hooks/** +
// .claude/settings.hooks.json
//
// Copies the zero-dependency runtime scripts verbatim (self-contained — no imports
// outside .claude/hooks/, see hooks/README.md) and compiles hook frontmatter into a
// mergeable `hooks` settings fragment. Never touches .claude/settings.json itself —
// see scripts/apply-hook-settings.mjs, the explicit opt-in merge step
// (sync-agents.mjs never overwrites project runtime state, settings*.json included).
import { readFileSync, writeFileSync, existsSync, mkdirSync, readdirSync, rmSync } from "node:fs";
import { join, dirname } from "node:path";
import { p, pp, isProjectMode, loadHookDefs, hookScriptFiles } from "../../scripts/lib/core.mjs";

export const TARGET_DIR = ".claude/hooks";
export const SETTINGS_FRAGMENT = ".claude/settings.hooks.json";

function scriptRelName(scriptPath) {
  return scriptPath.replace(/^hooks\/scripts\//, "");
}

export function buildSettingsFragment(defs) {
  const hooks = {};
  for (const d of [...defs].sort((a, b) => a.data.id.localeCompare(b.data.id))) {
    const command = `node "$CLAUDE_PROJECT_DIR/${TARGET_DIR}/${scriptRelName(d.data.script)}"`;
    const entry = { hooks: [{ type: "command", command }] };
    if (d.data.matcher) entry.matcher = d.data.matcher;
    (hooks[d.data.event] ||= []).push(entry);
  }
  // worktree.baseRef=head: subagent/--worktree checkouts branch from the CURRENT HEAD, not
  // origin/HEAD. Without it parallel agents start from a stale (or, as with a v1 origin, the
  // wrong) tree — see workflow/parallel-work.md.
  return { hooks, worktree: { baseRef: "head" } };
}

function walk(base, prefix = "") {
  if (!existsSync(base)) return [];
  return readdirSync(base, { withFileTypes: true }).flatMap((e) =>
    e.isDirectory() ? walk(join(base, e.name), `${prefix}${e.name}/`) : [`${prefix}${e.name}`]
  );
}

export function syncHooks({ check }) {
  const { defs, problems } = loadHookDefs();
  if (problems.length) return { drift: 0, changes: [], problems, count: 0 };

  const out = (...s) => (isProjectMode() ? pp(...s) : p(...s));
  const dir = out(TARGET_DIR);
  mkdirSync(dir, { recursive: true });

  let drift = 0;
  const changes = [];

  // 1. copy hooks/scripts/** verbatim — every .mjs, so shared helpers ship too.
  const wanted = new Map();
  for (const f of hookScriptFiles()) {
    const rel = scriptRelName(f);
    wanted.set(rel, readFileSync(p(f), "utf8"));
  }
  for (const [rel, content] of wanted) {
    const target = join(dir, rel);
    const cur = existsSync(target) ? readFileSync(target, "utf8") : null;
    if (cur !== content) {
      drift++;
      changes.push(`${cur === null ? "create" : "update"} ${TARGET_DIR}/${rel}`);
      if (!check) {
        mkdirSync(dirname(target), { recursive: true });
        writeFileSync(target, content);
      }
    }
  }
  for (const rel of walk(dir)) {
    if (!rel.endsWith(".mjs") || wanted.has(rel)) continue;
    drift++;
    changes.push(`delete ${TARGET_DIR}/${rel} (no source)`);
    if (!check) rmSync(join(dir, rel));
  }

  // 2. settings fragment — a mergeable partial, not settings.json itself.
  const fragment = `${JSON.stringify(buildSettingsFragment(defs), null, 2)}\n`;
  const fragmentTarget = out(SETTINGS_FRAGMENT);
  const curFragment = existsSync(fragmentTarget) ? readFileSync(fragmentTarget, "utf8") : null;
  if (curFragment !== fragment) {
    drift++;
    changes.push(`${curFragment === null ? "create" : "update"} ${SETTINGS_FRAGMENT}`);
    if (!check) writeFileSync(fragmentTarget, fragment);
  }

  return { drift, changes, problems: [], count: defs.length };
}
