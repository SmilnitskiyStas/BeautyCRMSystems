#!/usr/bin/env node
// Copy (or update) the library into a project's .agent-system/ — the "pull" for a project that
// vendors it. Copies only what the generator needs, never this repo's own generated output
// (.claude/, .codex/), tests, or docs: nested .claude/ dirs could be discovered by Claude Code
// as duplicate skills/agents.
//
//   node scripts/vendor-into.mjs <project-dir> [--dry-run]
//
// Everything under <project>/.agent-system/ is replaced (files the library no longer has are
// removed). Project-specific content must live in <project>/.ai/ (agents, skills, overlay),
// never inside .agent-system/ — see MIGRATION.md.
import { cpSync, mkdirSync, rmSync, existsSync, readdirSync, writeFileSync, readFileSync, statSync } from "node:fs";
import { join, resolve } from "node:path";
import { execFileSync } from "node:child_process";
import { ROOT } from "./lib/core.mjs";

const argv = process.argv.slice(2);
const dry = argv.includes("--dry-run");
const target = argv.find((a) => !a.startsWith("--"));
if (!target) {
  console.error("usage: node scripts/vendor-into.mjs <project-dir> [--dry-run]");
  process.exit(2);
}
const proj = resolve(target);
if (!existsSync(proj)) { console.error(`✗ ${proj} does not exist`); process.exit(2); }

const DIRS = ["agents", "skills", "workflow", "schemas", "templates", "adapters", "scripts", "profiles", "hooks"];
const FILES = ["MIGRATION.md", "AGENTS.template.md", "CLAUDE.template.md"];
const dest = join(proj, ".agent-system");

function sha() {
  try { return execFileSync("git", ["rev-parse", "HEAD"], { cwd: ROOT }).toString().trim(); } catch { return "unknown"; }
}
function dirty() {
  try { return execFileSync("git", ["status", "--porcelain"], { cwd: ROOT }).toString().trim().length > 0; } catch { return false; }
}
const count = (d) => readdirSync(d, { recursive: true }).filter((f) => statSync(join(d, String(f))).isFile()).length;

let n = 0;
for (const d of DIRS) n += existsSync(join(ROOT, d)) ? count(join(ROOT, d)) : 0;
console.log(`${dry ? "Would vendor" : "Vendoring"} ${n} file(s) from ${ROOT}\n  into ${dest}`);
if (dirty()) console.warn("⚠ the library working tree has uncommitted changes — the vendored copy will not match any commit");
if (dry) process.exit(0);

if (existsSync(dest)) rmSync(dest, { recursive: true, force: true });
mkdirSync(dest, { recursive: true });
for (const d of DIRS) if (existsSync(join(ROOT, d))) cpSync(join(ROOT, d), join(dest, d), { recursive: true });
for (const f of FILES) if (existsSync(join(ROOT, f))) cpSync(join(ROOT, f), join(dest, f));
writeFileSync(
  join(dest, "VENDOR.md"),
  `# Vendored agent library\n\nSource: github.com/SmilnitskiyStas/AI_agent_only_v2 @ \`${sha()}\`${dirty() ? " (+uncommitted changes)" : ""}\nVendored: ${new Date().toISOString().slice(0, 10)}\n\n` +
    "Do not edit anything in this directory — it is replaced on every update.\n" +
    "Project agents, skills and overrides live in `.ai/agents/`, `.ai/skills/`, `.ai/agent-overlay.yaml`.\n\n" +
    "Update:\n\n```\nnode <path-to-library>/scripts/vendor-into.mjs .\nnode .agent-system/scripts/sync-agents.mjs --project .\nnode .agent-system/scripts/validate-config.mjs --project .\n```\n"
);
console.log(`✓ vendored @ ${sha().slice(0, 7)}. Next: node .agent-system/scripts/sync-agents.mjs --project <project>`);
