#!/usr/bin/env node
// Explicit, opt-in merge of the generated `hooks` settings fragment into real Claude Code
// settings. sync-agents.mjs never touches settings files (project runtime state,
// workflow/hooks-policy.md) — this script is the one place that does, and only when asked.
//
//   node scripts/apply-hook-settings.mjs                        # dry run — print what would change
//   node scripts/apply-hook-settings.mjs --apply                # write .claude/settings.json (all hooks)
//   node scripts/apply-hook-settings.mjs --apply --only agent-run-log,compact-snapshot
//   node scripts/apply-hook-settings.mjs --apply --local        # write .claude/settings.local.json (personal, not for git)
//   node scripts/apply-hook-settings.mjs --apply --project <dir>
//
// --only <ids>: wire just those hooks (matched by script name). Hooks already present in the
// target file that are NOT in the fragment are left alone when --only is used; without
// --only the `hooks` block is replaced by the fragment.
import { readFileSync, writeFileSync, existsSync } from "node:fs";
import { p, pp, isProjectMode } from "./lib/core.mjs";
import { SETTINGS_FRAGMENT } from "../adapters/claude/generate-hooks.mjs";

const argv = process.argv.slice(2);
const apply = argv.includes("--apply");
const local = argv.includes("--local");
const only = argv.includes("--only") ? argv[argv.indexOf("--only") + 1].split(",").filter(Boolean) : null;
const out = (...s) => (isProjectMode() ? pp(...s) : p(...s));
const fragmentPath = out(SETTINGS_FRAGMENT);
const targetName = local ? "settings.local.json" : "settings.json";
const settingsPath = out(".claude", targetName);

if (!existsSync(fragmentPath)) {
  console.error(`✗ ${SETTINGS_FRAGMENT} not found — run \`node scripts/sync-agents.mjs\` first`);
  process.exit(1);
}

const fragment = JSON.parse(readFileSync(fragmentPath, "utf8"));
const settings = existsSync(settingsPath) ? JSON.parse(readFileSync(settingsPath, "utf8")) : {};

// the command of an entry ends with ".claude/hooks/<script>.mjs"
const isSelected = (entry) => !only || entry.hooks.some((h) => only.some((id) => h.command.includes(`/${id}.mjs`)));
const selected = {};
for (const [event, entries] of Object.entries(fragment.hooks || {})) {
  const keep = entries.filter(isSelected);
  if (keep.length) selected[event] = keep;
}
if (only) {
  const found = Object.values(selected).flat().flatMap((e) => e.hooks.map((h) => h.command));
  const missing = only.filter((id) => !found.some((c) => c.includes(`/${id}.mjs`)));
  if (missing.length) {
    console.error(`✗ unknown hook id(s): ${missing.join(", ")}`);
    process.exit(1);
  }
}

// merge: with --only, add to existing hooks (replace same-command entries); otherwise replace the block
function merged() {
  if (!only) return selected;
  const res = JSON.parse(JSON.stringify(settings.hooks || {}));
  for (const [event, entries] of Object.entries(selected)) {
    const cmds = new Set(entries.flatMap((e) => e.hooks.map((h) => h.command)));
    res[event] = (res[event] || []).filter((e) => !e.hooks.some((h) => cmds.has(h.command))).concat(entries);
  }
  return res;
}

const nextHooks = merged();
const baseRefDiffers = fragment.worktree?.baseRef && settings.worktree?.baseRef === undefined;
if (JSON.stringify(settings.hooks || null) === JSON.stringify(nextHooks) && !baseRefDiffers) {
  console.log(`✓ .claude/${targetName} already has these hooks`);
  process.exit(0);
}

console.log(apply ? `Merging into .claude/${targetName}:` : `Would merge into .claude/${targetName} (dry run — pass --apply to write):`);
console.log(JSON.stringify(selected, null, 2));
if (baseRefDiffers) console.log(`+ worktree.baseRef = "${fragment.worktree.baseRef}" (only set when you have not chosen a value yourself)`);

if (apply) {
  settings.hooks = nextHooks;
  if (baseRefDiffers) settings.worktree = { ...(settings.worktree || {}), baseRef: fragment.worktree.baseRef };
  writeFileSync(settingsPath, `${JSON.stringify(settings, null, 2)}\n`);
  console.log(`✓ wrote ${settingsPath}`);
}
