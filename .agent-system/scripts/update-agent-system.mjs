#!/usr/bin/env node
// Update the vendored agent library in a project to a newer release. Run from the project
// (usually through the /update-agents command); preview by default, writes only with --apply.
//
//   node .agent-system/scripts/update-agent-system.mjs [--project .]            # preview: versions + changelog
//   node .agent-system/scripts/update-agent-system.mjs --apply                  # vendor + sync + validate (never commits)
//   ... --to 2.2.1        a specific release instead of the newest v* tag
//   ... --repo <url>      release source (default: the AI_agent_only_v2 repo; env AGENT_SYSTEM_UPDATE_URL)
//   ... --source <dir>    take the new library from a local checkout instead of cloning
//   ... --force           also update when already up to date / when generated files have local edits
//
// Only generated paths are touched: .agent-system/, .claude/{agents,skills,hooks,workflow,schemas,
// templates}, .claude/settings.hooks.json, .claude/agent-system.json, .codex/{agents,agent-system.json}.
// Project-owned files (.ai/**, CLAUDE.md, .claude/docs|tasks|logs|commands, settings*.json) are never written.
import { execFileSync, spawnSync } from "node:child_process";
import { existsSync, mkdtempSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { ROOT } from "./lib/core.mjs";
import { libraryInfo, latestRemoteVersion, compareSemver, REPO_URL } from "./lib/version.mjs";

export const GENERATED_PATHS = [
  ".agent-system",
  ".claude/agents", ".claude/skills", ".claude/hooks", ".claude/workflow", ".claude/schemas", ".claude/templates",
  ".claude/settings.hooks.json", ".claude/agent-system.json",
  ".codex/agents", ".codex/agent-system.json",
];

// "## 2.1.0 ... ## 2.0.0" -> sections with from < version <= to, newest first
export function changelogBetween(text, from, to) {
  const parts = text.replace(/\r\n/g, "\n").split(/^## /m).slice(1);
  return parts
    .map((s) => ({ version: s.split("\n")[0].trim(), body: s }))
    .filter((s) => /^\d+\.\d+\.\d+$/.test(s.version) && compareSemver(s.version, from) > 0 && compareSemver(s.version, to) <= 0)
    .sort((a, b) => compareSemver(b.version, a.version))
    .map((s) => `## ${s.body.trimEnd()}`)
    .join("\n\n");
}

const hookScripts = (proj) => {
  try {
    const frag = JSON.parse(readFileSync(join(proj, ".claude", "settings.hooks.json"), "utf8"));
    return new Set(Object.values(frag.hooks || {}).flat().flatMap((e) => e.hooks.map((h) => (h.command.match(/\/([a-z0-9-]+)\.mjs/) || [])[1])).filter(Boolean));
  } catch {
    return new Set();
  }
};

function main() {
  const argv = process.argv.slice(2);
  const opt = (n, d = null) => (argv.includes(n) ? argv[argv.indexOf(n) + 1] : d);
  const apply = argv.includes("--apply");
  const force = argv.includes("--force");
  const proj = resolve(opt("--project", process.cwd()));
  const repo = opt("--repo", process.env.AGENT_SYSTEM_UPDATE_URL || REPO_URL);
  let tmp = null;
  const cleanup = () => { if (tmp) rmSync(tmp, { recursive: true, force: true }); tmp = null; };
  const fail = (m) => { console.error(`✗ ${m}`); cleanup(); process.exit(1); };

  if (ROOT !== join(proj, ".agent-system")) fail(`run this from a project that vendors the library (${join(proj, ".agent-system")} is not where this script lives). In the library checkout use scripts/vendor-into.mjs.`);
  const cur = libraryInfo().version;

  // 1. what is the target
  let src = opt("--source") ? resolve(opt("--source")) : null;
  let target = opt("--to");
  if (src) {
    target = libraryInfo(src).version;
  } else {
    target = target || latestRemoteVersion(repo);
    if (!target) fail(`could not determine the latest release of ${repo} (offline, no access, or no v* tags). Use --to <version> or --source <dir>.`);
  }
  console.log(`agent-system: installed ${cur}, ${src ? "source" : "latest"} ${target}`);
  if (compareSemver(target, cur) <= 0 && !force) {
    console.log("✓ up to date");
    return;
  }

  // 2. get the new library
  try {
    if (!src) {
      tmp = mkdtempSync(join(tmpdir(), "agent-system-"));
      const r = spawnSync("git", ["clone", "--depth", "1", "--branch", `v${target}`, "--quiet", repo, tmp], { encoding: "utf8" });
      if (r.status !== 0) fail(`could not fetch v${target} from ${repo}:\n${(r.stderr || "").trim()}`);
      src = tmp;
    }
    const clPath = join(src, "CHANGELOG.md");
    const notes = existsSync(clPath) ? changelogBetween(readFileSync(clPath, "utf8"), cur, target) : "";
    console.log(`\nWhat's new (${cur} → ${target}):\n\n${notes || "(no changelog entries found)"}\n`);

    // 3. generated files must be unmodified, otherwise the update would overwrite someone's edit
    let dirty = "";
    try {
      dirty = execFileSync("git", ["status", "--porcelain", "--", ...GENERATED_PATHS], { cwd: proj, stdio: ["ignore", "pipe", "ignore"] }).toString().trim();
    } catch {
      console.log("⚠ not a git repository — there is no easy rollback; commit/back up first.");
    }
    if (dirty && !force) fail(`generated files have uncommitted changes — commit or restore them first (or use --force):\n${dirty}`);

    if (!apply) {
      console.log(`Preview only. To update: /update-agents --apply   (or: node .agent-system/scripts/update-agent-system.mjs --apply)`);
      console.log("It replaces .agent-system/ and regenerates .claude/ and .codex/ — and commits nothing.");
      return;
    }

    // 4. apply: new vendor-into -> new sync -> new validate
    const before = hookScripts(proj);
    const run = (label, script, args) => {
      const r = spawnSync(process.execPath, [script, ...args], { encoding: "utf8" });
      if (r.status !== 0) {
        console.error(`✗ ${label} failed:\n${(r.stdout || "") + (r.stderr || "")}`);
        console.error(`\nRoll back (only generated files are touched):\n  git restore -- ${GENERATED_PATHS.join(" ")}\n  git clean -fd -- ${GENERATED_PATHS.join(" ")}`);
        cleanup();
        process.exit(1);
      }
      return r.stdout;
    };
    run("vendor-into", join(src, "scripts", "vendor-into.mjs"), [proj]);
    const sync = run("sync-agents", join(proj, ".agent-system", "scripts", "sync-agents.mjs"), ["--project", proj]);
    run("validate-config", join(proj, ".agent-system", "scripts", "validate-config.mjs"), ["--project", proj]);
    console.log(sync.trim().split("\n").filter((l) => l.startsWith("✓")).join("\n"));

    const added = [...hookScripts(proj)].filter((h) => !before.has(h));
    console.log(`\n✓ updated ${cur} → ${libraryInfo(join(proj, ".agent-system")).version}. Nothing was committed.`);
    if (added.length) {
      console.log(`\nNew hook(s) in this release are NOT enabled yet: ${added.join(", ")}`);
      console.log(`Enable (personal, not committed): node .agent-system/scripts/apply-hook-settings.mjs --apply --local --only ${added.join(",")} --project .`);
    }
    console.log("\nNext: review `git diff --stat`, then commit. Start a new Claude Code session to load the new agents.");
  } finally {
    if (tmp) rmSync(tmp, { recursive: true, force: true });
  }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) main();
