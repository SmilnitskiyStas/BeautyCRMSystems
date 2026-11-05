#!/usr/bin/env node
// Dispatch a task to Codex CLI in an isolated git worktree, then collect the result.
// Standalone by design (no imports from scripts/lib/) so it can be copied into any git
// repo as a single file — see workflow/cross-runtime-delegation.md for the policy this
// implements (confirm before dispatch, confirm before merge, Claude reviews the diff).
//
//   node scripts/dispatch-codex.mjs start   --slug <name> --task-file <path> [--base <branch>] [--sandbox workspace-write|read-only|danger-full-access] [--model <name>]
//   node scripts/dispatch-codex.mjs status  --slug <name>
//   node scripts/dispatch-codex.mjs collect --slug <name> [--full]
//   node scripts/dispatch-codex.mjs cleanup --slug <name> [--force]
//
// `start` runs `codex exec` to completion — invoke it via a backgrounded shell call
// (e.g. Claude Code's Bash tool with run_in_background) rather than waiting on it here.
import { existsSync, mkdirSync, readFileSync, writeFileSync, statSync, openSync, rmSync } from "node:fs";
import { join, dirname, relative } from "node:path";
import { execFileSync, spawn } from "node:child_process";

const SLUG_RE = /^[a-z][a-z0-9-]*[a-z0-9]$/;
const SANDBOXES = ["read-only", "workspace-write", "danger-full-access"];

function fail(msg) {
  console.error(`✗ ${msg}`);
  process.exit(1);
}

function parseArgs(argv) {
  const out = { _: [] };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a.startsWith("--")) {
      const key = a.slice(2);
      const next = argv[i + 1];
      if (next === undefined || next.startsWith("--")) out[key] = true;
      else { out[key] = next; i++; }
    } else out._.push(a);
  }
  return out;
}

function repoRoot() {
  try {
    return execFileSync("git", ["rev-parse", "--show-toplevel"], { encoding: "utf8", shell: true }).trim();
  } catch {
    fail("not inside a git repository (git rev-parse --show-toplevel failed)");
  }
}

function git(root, args) {
  return execFileSync("git", args, { cwd: root, encoding: "utf8", shell: true });
}

function requireSlug(args) {
  const slug = args.slug;
  if (!slug || typeof slug !== "string" || !SLUG_RE.test(slug)) {
    fail(`--slug is required and must be kebab-case (got ${JSON.stringify(slug)})`);
  }
  return slug;
}

function recordPath(root, slug) {
  return join(root, ".ai", "logs", "delegations", `${slug}.json`);
}

function logDir(root, slug) {
  return join(root, ".ai", "logs", "delegations", slug);
}

function readRecord(root, slug) {
  const p = recordPath(root, slug);
  if (!existsSync(p)) fail(`no delegation record for slug "${slug}" (${relative(root, p)}) — did you run "start"?`);
  return JSON.parse(readFileSync(p, "utf8"));
}

function writeRecord(root, slug, record) {
  const p = recordPath(root, slug);
  mkdirSync(dirname(p), { recursive: true });
  writeFileSync(p, `${JSON.stringify(record, null, 2)}\n`);
}

async function cmdStart(root, args) {
  const slug = requireSlug(args);
  const taskFile = args["task-file"];
  const prompt = args.prompt;
  if (!taskFile && !prompt) fail("start requires --task-file <path> or --prompt <text>");
  const taskText = taskFile ? readFileSync(taskFile, "utf8") : prompt;

  const sandbox = args.sandbox || "workspace-write";
  if (!SANDBOXES.includes(sandbox)) fail(`--sandbox must be one of: ${SANDBOXES.join(", ")}`);

  const base = args.base || git(root, ["rev-parse", "--abbrev-ref", "HEAD"]).trim();
  const branch = `codex/${slug}`;
  const worktreeDir = join(root, ".worktrees", slug);

  if (existsSync(recordPath(root, slug))) {
    const existing = readRecord(root, slug);
    if (existing.status === "running") fail(`slug "${slug}" is already running (started ${existing.started_at})`);
  }
  if (existsSync(worktreeDir)) fail(`${relative(root, worktreeDir)} already exists — cleanup that slug first`);

  console.log(`Creating worktree ${relative(root, worktreeDir)} on branch ${branch} (from ${base})...`);
  git(root, ["worktree", "add", "-b", branch, worktreeDir, base]);

  const dir = logDir(root, slug);
  mkdirSync(dir, { recursive: true });
  const eventsPath = join(dir, "events.jsonl");
  const runLogPath = join(dir, "run.log");
  const lastMessagePath = join(dir, "last-message.txt");

  const startedAt = new Date().toISOString();
  writeRecord(root, slug, {
    slug, branch, base, sandbox,
    worktree: relative(root, worktreeDir),
    task_file: taskFile ? relative(root, taskFile) : null,
    started_at: startedAt,
    finished_at: null,
    status: "running",
    exit_code: null,
    collected: false,
  });

  console.log(`Running codex exec in ${relative(root, worktreeDir)} (sandbox: ${sandbox})...`);
  const codexArgs = ["exec", "--cd", worktreeDir, "--sandbox", sandbox, "--json", "-o", lastMessagePath];
  if (args.model) codexArgs.push("--model", args.model);

  const outFd = openSync(eventsPath, "w");
  const errFd = openSync(runLogPath, "w");
  const child = spawn("codex", codexArgs, { shell: true, stdio: ["pipe", outFd, errFd] });
  child.stdin.write(taskText);
  child.stdin.end();

  const exitCode = await new Promise((resolvePromise) => {
    child.on("close", (code) => resolvePromise(code));
  });

  const finishedAt = new Date().toISOString();
  const record = readRecord(root, slug);
  record.finished_at = finishedAt;
  record.status = exitCode === 0 ? "done" : "failed";
  record.exit_code = exitCode;
  writeRecord(root, slug, record);

  console.log(`${exitCode === 0 ? "✓" : "✗"} codex exec exited ${exitCode} — status: ${record.status}`);
  console.log(`Next: node scripts/dispatch-codex.mjs collect --slug ${slug}`);
  process.exit(exitCode === 0 ? 0 : 1);
}

function cmdStatus(root, args) {
  const slug = requireSlug(args);
  const record = readRecord(root, slug);
  console.log(JSON.stringify(record, null, 2));
  const runLogPath = join(logDir(root, slug), "run.log");
  if (existsSync(runLogPath) && statSync(runLogPath).size > 0) {
    const lines = readFileSync(runLogPath, "utf8").trim().split("\n");
    console.log(`\n--- last ${Math.min(10, lines.length)} line(s) of run.log ---`);
    console.log(lines.slice(-10).join("\n"));
  }
}

function cmdCollect(root, args) {
  const slug = requireSlug(args);
  const record = readRecord(root, slug);
  if (record.status === "running") fail(`slug "${slug}" is still running — check status first`);

  const worktreeDir = join(root, record.worktree);
  const lastMessagePath = join(logDir(root, slug), "last-message.txt");

  console.log(`Status: ${record.status} (exit code ${record.exit_code})`);
  console.log(`Worktree: ${record.worktree}  Branch: ${record.branch}  Base: ${record.base}\n`);

  if (existsSync(lastMessagePath)) {
    console.log("--- Codex's last message ---");
    console.log(readFileSync(lastMessagePath, "utf8").trim());
    console.log("");
  }

  console.log(`--- diff ${record.base}...${record.branch} (stat) ---`);
  try {
    console.log(git(root, ["diff", "--stat", `${record.base}...${record.branch}`]).trim());
  } catch (e) {
    console.log(`(could not diff: ${e.message})`);
  }

  if (args.full) {
    console.log(`\n--- full diff ---`);
    try {
      console.log(git(root, ["diff", `${record.base}...${record.branch}`]));
    } catch (e) {
      console.log(`(could not diff: ${e.message})`);
    }
  }

  record.collected = true;
  record.collected_at = new Date().toISOString();
  writeRecord(root, slug, record);
}

function cmdCleanup(root, args) {
  const slug = requireSlug(args);
  const record = readRecord(root, slug);
  const worktreeDir = join(root, record.worktree);
  const force = !!args.force;

  if (record.status === "running") fail(`slug "${slug}" is still running — do not clean up a live worktree`);

  if (!force) {
    const merged = git(root, ["branch", "--merged", record.base]).split("\n").map((l) => l.trim().replace(/^\*\s*/, ""));
    if (!merged.includes(record.branch)) {
      fail(
        `branch "${record.branch}" is not merged into "${record.base}" — pass --force to discard it anyway, ` +
          `or merge it first`
      );
    }
  }

  console.log(`Removing worktree ${record.worktree}...`);
  git(root, ["worktree", "remove", ...(force ? ["--force"] : []), worktreeDir]);

  if (force || git(root, ["branch", "--merged", record.base]).includes(record.branch)) {
    try {
      git(root, ["branch", "-D", record.branch]);
    } catch {
      /* branch already gone or never fully created — fine */
    }
  }

  record.status = "cleaned_up";
  record.cleaned_up_at = new Date().toISOString();
  writeRecord(root, slug, record);
  console.log(`✓ cleaned up "${slug}"`);
}

async function main() {
  const [sub, ...rest] = process.argv.slice(2);
  const args = parseArgs(rest);
  const root = repoRoot();

  if (sub === "start") await cmdStart(root, args);
  else if (sub === "status") cmdStatus(root, args);
  else if (sub === "collect") cmdCollect(root, args);
  else if (sub === "cleanup") cmdCleanup(root, args);
  else fail(`unknown subcommand ${JSON.stringify(sub)} — expected start|status|collect|cleanup`);
}

main();
