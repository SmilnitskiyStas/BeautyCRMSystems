#!/usr/bin/env node
// Cut a release of the library: bump VERSION, promote the changelog's "## Unreleased" section,
// regenerate (version stamp), run every check, commit, tag v<version>, push.
//
//   node scripts/release.mjs <patch|minor|major> [--dry-run] [--no-push] [--remote v2] [--force]
//
// While developing, add entries under "## Unreleased" in CHANGELOG.md. A release refuses to run on
// a dirty tree, off `main`, or with an empty Unreleased section. If a check fails, the files it
// changed are restored and nothing is committed.
import { execFileSync, spawnSync } from "node:child_process";
import { readFileSync, writeFileSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { p } from "./lib/core.mjs";
import { libraryInfo } from "./lib/version.mjs";

export function nextVersion(v, kind) {
  const [a, b, c] = String(v).split(".").map((n) => parseInt(n, 10) || 0);
  if (kind === "major") return `${a + 1}.0.0`;
  if (kind === "minor") return `${a}.${b + 1}.0`;
  if (kind === "patch") return `${a}.${b}.${c + 1}`;
  throw new Error(`unknown release kind "${kind}" (use patch, minor or major)`);
}

// Returns { text, notes } with "## Unreleased" renamed to "## <next>"; throws when it is missing/empty.
export function promoteChangelog(text, next) {
  const lines = text.replace(/\r\n/g, "\n").split("\n");
  const i = lines.findIndex((l) => l.trim() === "## Unreleased");
  if (i === -1) throw new Error('CHANGELOG.md has no "## Unreleased" section — add the release notes there first');
  let end = lines.findIndex((l, k) => k > i && l.startsWith("## "));
  if (end === -1) end = lines.length;
  const notes = lines.slice(i + 1, end).join("\n").trim();
  if (!notes) throw new Error('the "## Unreleased" section is empty — nothing to release');
  lines[i] = `## ${next}`;
  return { text: lines.join("\n"), notes };
}

function main() {
  const argv = process.argv.slice(2);
  const kind = argv.find((a) => !a.startsWith("--"));
  const opt = (n, d) => (argv.includes(n) ? argv[argv.indexOf(n) + 1] : d);
  const dry = argv.includes("--dry-run");
  const remote = opt("--remote", "v2");
  const git = (...a) => execFileSync("git", a, { cwd: p("."), stdio: ["ignore", "pipe", "pipe"] }).toString().trim();
  const fail = (m) => { console.error(`✗ ${m}`); process.exit(1); };
  if (!kind) fail("usage: node scripts/release.mjs <patch|minor|major> [--dry-run] [--no-push] [--remote v2]");

  const current = libraryInfo().version;
  let next, changelog;
  try {
    next = nextVersion(current, kind);
    changelog = promoteChangelog(readFileSync(p("CHANGELOG.md"), "utf8"), next);
  } catch (e) {
    fail(e.message);
  }
  console.log(`release ${current} → ${next} (${kind})\n\n${changelog.notes}\n`);

  if (git("status", "--porcelain")) fail("working tree is not clean — commit or stash first");
  const branch = git("rev-parse", "--abbrev-ref", "HEAD");
  if (branch !== "main" && !argv.includes("--force")) fail(`on branch "${branch}", releases are cut from main (use --force to override)`);
  if (git("tag", "--list", `v${next}`)) fail(`tag v${next} already exists`);
  if (dry) { console.log("dry run — nothing written."); return; }

  const run = (label, args) => {
    const r = spawnSync(process.execPath, args, { cwd: p("."), encoding: "utf8" });
    if (r.status !== 0) {
      console.error(`✗ ${label} failed:\n${(r.stdout || "") + (r.stderr || "")}`);
      git("checkout", "--", ".");
      console.error("Restored the working tree; nothing was committed.");
      process.exit(1);
    }
  };
  writeFileSync(p("VERSION"), `${next}\n`);
  writeFileSync(p("CHANGELOG.md"), changelog.text);
  run("sync-agents", [p("scripts/sync-agents.mjs")]);
  run("validate-config", [p("scripts/validate-config.mjs")]);
  run("doctor", [p("scripts/doctor.mjs")]);
  run("unit tests", ["--test", "tests/unit/*.test.mjs"]);

  git("add", "-A");
  git("commit", "-q", "-m", `release: v${next}\n\nCo-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`);
  git("tag", "-a", `v${next}`, "-m", `agent-system ${next}`);
  console.log(`✓ committed and tagged v${next}`);
  if (argv.includes("--no-push")) { console.log(`not pushed. Push with: git push ${remote} main v${next}`); return; }
  git("push", remote, "main", `v${next}`);
  console.log(`✓ pushed main and v${next} to ${remote}`);
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) main();
