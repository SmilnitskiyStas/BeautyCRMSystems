#!/usr/bin/env node
// Deterministic helper for the integration-steward: plan / merge / cleanup for branches
// produced by parallel worktree agents. It NEVER merges into the base or a protected
// branch — only into a fresh integrate/* branch it creates. Folding that branch into
// main stays a human gate (workflow/human-gates.md).
//
//   node scripts/integrate-worktrees.mjs plan    --base main [--branches a,b] [--prefix worktree-]
//   node scripts/integrate-worktrees.mjs merge   --base main --into integrate/feat-x --branches a,b [--test "node --test ..."]
//   node scripts/integrate-worktrees.mjs cleanup --branches a,b [--force]
//   (all accept --repo <dir>; default cwd)
//
// plan exits 1 when any branch is STALE_BASE, UNCOMMITTED or has no commits.
// merge exits 3 on a conflict or a failing --test and stops with what merged so far.
import { execFileSync, spawnSync } from "node:child_process";
import { resolve } from "node:path";

const argv = process.argv.slice(2);
const cmd = argv[0];
const opt = (n, d = null) => (argv.includes(n) ? argv[argv.indexOf(n) + 1] : d);
const flag = (n) => argv.includes(n);
const repo = resolve(opt("--repo", process.cwd()));
const PROTECTED = new Set(["main", "master", "develop", "trunk"]);

function git(args, { allowFail = false } = {}) {
  const r = spawnSync("git", args, { cwd: repo, encoding: "utf8" });
  if (r.status !== 0 && !allowFail) throw new Error(`git ${args.join(" ")}: ${(r.stderr || r.stdout).trim()}`);
  return { ok: r.status === 0, out: (r.stdout || "").trim(), err: (r.stderr || "").trim() };
}

function worktreeMap() {
  const map = new Map(); // branch -> path
  let path = null;
  for (const line of git(["worktree", "list", "--porcelain"]).out.split("\n")) {
    if (line.startsWith("worktree ")) path = line.slice(9);
    else if (line.startsWith("branch refs/heads/")) map.set(line.slice("branch refs/heads/".length), path);
  }
  return map;
}

function branchList() {
  const explicit = opt("--branches");
  if (explicit) return explicit.split(",").map((s) => s.trim()).filter(Boolean);
  const prefix = opt("--prefix", "worktree-");
  return git(["for-each-ref", "--format=%(refname:short)", `refs/heads/${prefix}*`]).out.split("\n").filter(Boolean);
}

function changedFiles(base, branch) {
  const mb = git(["merge-base", base, branch]).out;
  return git(["diff", "--name-only", `${mb}..${branch}`]).out.split("\n").filter(Boolean);
}

function plan() {
  const base = opt("--base");
  if (!base) throw new Error("--base required");
  const baseSha = git(["rev-parse", base]).out;
  const wt = worktreeMap();
  const rows = [];
  for (const b of branchList()) {
    const problems = [];
    const mb = git(["merge-base", base, b]).out;
    const behind = Number(git(["rev-list", "--count", `${b}..${base}`]).out);
    const ahead = Number(git(["rev-list", "--count", `${base}..${b}`]).out);
    if (mb !== baseSha) problems.push(`STALE_BASE (branch forked ${behind} commit(s) behind ${base}; set worktree.baseRef=head)`);
    if (ahead === 0) problems.push("NO_COMMITS");
    const path = wt.get(b);
    if (path) {
      const dirty = spawnSync("git", ["status", "--porcelain"], { cwd: path, encoding: "utf8" }).stdout.trim();
      if (dirty) problems.push("UNCOMMITTED (agent left changes uncommitted — commit them first)");
    }
    rows.push({ branch: b, path: path || null, ahead, files: ahead ? changedFiles(base, b) : [], problems });
  }
  const overlaps = [];
  for (let i = 0; i < rows.length; i++)
    for (let j = i + 1; j < rows.length; j++) {
      const shared = rows[i].files.filter((f) => rows[j].files.includes(f));
      if (shared.length) overlaps.push({ a: rows[i].branch, b: rows[j].branch, files: shared });
    }
  for (const r of rows) {
    console.log(`${r.problems.length ? "✗" : "✓"} ${r.branch}  (+${r.ahead} commit(s), ${r.files.length} file(s))`);
    for (const pr of r.problems) console.log(`    - ${pr}`);
  }
  for (const o of overlaps) console.log(`⚠ overlap ${o.a} <-> ${o.b}: ${o.files.join(", ")}  (merge them in dependency order; expect a conflict)`);
  if (!rows.length) console.log("no matching branches");
  process.exit(rows.some((r) => r.problems.length) ? 1 : 0);
}

function merge() {
  const base = opt("--base");
  const into = opt("--into");
  const branches = (opt("--branches") || "").split(",").filter(Boolean);
  if (!base || !into || !branches.length) throw new Error("--base, --into and --branches required");
  if (PROTECTED.has(into) || into === base) throw new Error(`refusing to merge into protected/base branch "${into}" — use integrate/<name>`);
  if (!into.startsWith("integrate/")) throw new Error('integration branch must start with "integrate/"');
  if (git(["rev-parse", "--verify", `refs/heads/${into}`], { allowFail: true }).ok) throw new Error(`${into} already exists`);
  const dirty = git(["status", "--porcelain"]).out;
  if (dirty) throw new Error("working tree is not clean — commit or stash first (the helper does not touch your changes)");

  const original = git(["rev-parse", "--abbrev-ref", "HEAD"]).out;
  git(["checkout", "-q", "-b", into, base]);
  const test = opt("--test");
  const merged = [];
  for (const b of branches) {
    const before = git(["rev-parse", "HEAD"]).out;
    const m = git(["merge", "--no-ff", "-m", `Merge ${b} into ${into}`, b], { allowFail: true });
    if (!m.ok) {
      const files = git(["diff", "--name-only", "--diff-filter=U"], { allowFail: true }).out.split("\n").filter(Boolean);
      git(["merge", "--abort"], { allowFail: true });
      console.log(`✗ CONFLICT merging ${b} — files: ${files.join(", ") || "(unknown)"}`);
      console.log(`  merged so far: ${merged.join(", ") || "(none)"}. Branch ${into} left at the last good merge; you are on ${into}.`);
      console.log("  Do NOT guess a resolution for a semantic conflict — return it to the owner agent via project-manager.");
      process.exit(3);
    }
    if (test) {
      const t = spawnSync(test, { cwd: repo, shell: true, encoding: "utf8" });
      if (t.status !== 0) {
        git(["reset", "-q", "--hard", before]); // safe: only our own fresh integrate/* branch
        console.log(`✗ TESTS FAILED after merging ${b}; rolled ${into} back to before that merge.`);
        console.log((t.stdout + t.stderr).split("\n").slice(-25).join("\n"));
        process.exit(3);
      }
    }
    merged.push(b);
    console.log(`✓ merged ${b}${test ? " (tests pass)" : ""}`);
  }
  console.log(`\n${into} ready: ${merged.length} branch(es). Original branch was ${original}.`);
  console.log(`Folding it into ${base} is a HUMAN GATE — review the diff, then merge it yourself.`);
}

function cleanup() {
  const force = flag("--force");
  const wt = worktreeMap();
  for (const b of branchList()) {
    const integrateRefs = git(["for-each-ref", "--format=%(refname)", "refs/heads/integrate/"]).out.split("\n").filter(Boolean);
    const safe = ["HEAD", ...integrateRefs].some((t) => git(["merge-base", "--is-ancestor", b, t], { allowFail: true }).ok);
    if (!safe && !force) {
      console.log(`· kept ${b} — not merged into HEAD or any integrate/* branch (use --force to drop)`);
      continue;
    }
    const path = wt.get(b);
    if (path) {
      git(["worktree", "unlock", path], { allowFail: true });
      git(["worktree", "remove", "--force", path], { allowFail: true });
    }
    git(["branch", force ? "-D" : "-d", b], { allowFail: true });
    console.log(`✓ removed ${b}${path ? " + worktree" : ""}`);
  }
}

try {
  if (cmd === "plan") plan();
  else if (cmd === "merge") merge();
  else if (cmd === "cleanup") cleanup();
  else {
    console.error("usage: integrate-worktrees.mjs plan|merge|cleanup  (see header)");
    process.exit(2);
  }
} catch (e) {
  console.error(`✗ ${e.message}`);
  process.exit(2);
}
