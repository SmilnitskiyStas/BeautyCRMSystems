#!/usr/bin/env node
// Memory bank = three small working-memory files next to the long-term project memory:
//
//   .ai/memory/activeContext.md   what we are doing now + the next action (overwritten, ~40 lines)
//   .ai/memory/inbox.md           raw notes / ideas, triaged later
//   .ai/memory/progress.md        milestones only, one line each
//
// Decisions stay in .ai/memory/architecture-decisions/ (ADR), research in .ai/artifacts/research/,
// tasks in .claude/tasks/ — this is an extension of .ai/memory/, not a second memory system.
//
// Self-contained (no imports outside node:*) — copied verbatim into .claude/hooks/lib/ and into
// plugins. Used by the session-context / compact-restore hooks and by the memory-bank skill:
//
//   node .claude/hooks/lib/memory-bank.mjs init            create the missing files (never overwrites)
//   node .claude/hooks/lib/memory-bank.mjs status          active context + counts
//   node .claude/hooks/lib/memory-bank.mjs archive <slug>  move the active context to memory/archive/ and reset it
import { existsSync, readFileSync, writeFileSync, mkdirSync, readdirSync } from "node:fs";
import { join, resolve } from "node:path";
import { pathToFileURL } from "node:url";

export const UNSET_MARK = "<!-- memory-bank: unset — delete this line once the file is filled in -->";

export const TEMPLATES = {
  "activeContext.md": [
    "# Active context",
    "",
    UNSET_MARK,
    "<!-- Overwrite, do not append. Keep it under ~40 lines: it is injected at session start. -->",
    "",
    "## Current focus",
    "",
    "## Next action",
    "",
    "## Open decisions / blockers",
    "",
    "## Key files",
    "",
    "## Updated",
    "",
  ].join("\n"),
  "inbox.md": [
    "# Inbox",
    "",
    "Raw notes and ideas, newest last. Triage each into a task (.claude/tasks), a decision (ADR in",
    "architecture-decisions/), `rejected-approaches.md` — or delete it.",
    "",
  ].join("\n"),
  "progress.md": [
    "# Progress",
    "",
    "Milestones only, not every turn. One line each: `YYYY-MM-DD — what changed — pointer (task id / commit / ADR)`.",
    "",
  ].join("\n"),
};

export const memoryDir = (cwd) => join(cwd || process.cwd(), ".ai", "memory");

export function initMemoryBank(cwd) {
  const dir = memoryDir(cwd);
  mkdirSync(dir, { recursive: true });
  const created = [];
  const existing = [];
  for (const [name, body] of Object.entries(TEMPLATES)) {
    const f = join(dir, name);
    if (existsSync(f)) existing.push(name);
    else { writeFileSync(f, body); created.push(name); }
  }
  return { created, existing };
}

// The filled-in active context (trimmed, capped), or null when missing / still the empty template.
export function readActiveContext(cwd, maxChars = 2000) {
  const f = join(memoryDir(cwd), "activeContext.md");
  try {
    if (!existsSync(f)) return null;
    let text = readFileSync(f, "utf8").replace(/\r\n/g, "\n").trim();
    if (!text || text.includes("memory-bank: unset")) return null;
    if (text.length > maxChars) text = `${text.slice(0, maxChars)}\n…(truncated — read .ai/memory/activeContext.md)`;
    return text;
  } catch {
    return null;
  }
}

const countItems = (file) => {
  try {
    return readFileSync(file, "utf8").split("\n").filter((l) => /^\s*(?:[-*]|\d+\.)\s+\S/.test(l)).length;
  } catch {
    return 0;
  }
};

export function memoryStatus(cwd) {
  const dir = memoryDir(cwd);
  const active = readActiveContext(cwd, 4000);
  const progressLines = (() => {
    try {
      return readFileSync(join(dir, "progress.md"), "utf8").split("\n").filter((l) => /^\s*[-*]?\s*\d{4}-\d{2}-\d{2}/.test(l)).slice(-5);
    } catch {
      return [];
    }
  })();
  const adrDir = join(dir, "architecture-decisions");
  const adrs = existsSync(adrDir) ? readdirSync(adrDir).filter((f) => f.endsWith(".md")).length : 0;
  return {
    initialized: existsSync(join(dir, "activeContext.md")),
    active,
    inbox: countItems(join(dir, "inbox.md")),
    progress: progressLines,
    adrs,
    knownIssues: existsSync(join(dir, "known-issues.md")),
  };
}

export function archiveActive(cwd, slug) {
  const clean = String(slug || "").toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "");
  if (!clean) throw new Error("archive needs a slug, e.g. `archive booking-flow`");
  const active = readActiveContext(cwd, Infinity);
  if (!active) throw new Error("nothing to archive: activeContext.md is missing or still the empty template");
  const dir = join(memoryDir(cwd), "archive");
  mkdirSync(dir, { recursive: true });
  const target = join(dir, `${new Date().toISOString().slice(0, 10)}-${clean}.md`);
  if (existsSync(target)) throw new Error(`${target} already exists — pick another slug`);
  writeFileSync(target, `${active}\n`);
  writeFileSync(join(memoryDir(cwd), "activeContext.md"), TEMPLATES["activeContext.md"]);
  return target;
}

function cli(argv) {
  const cmd = argv[0];
  const cwdIdx = argv.indexOf("--cwd");
  const cwd = resolve(cwdIdx === -1 ? process.cwd() : argv[cwdIdx + 1]);
  if (cmd === "init") {
    const r = initMemoryBank(cwd);
    console.log(r.created.length ? `created in .ai/memory/: ${r.created.join(", ")}` : "nothing to create — memory bank files already exist");
    if (r.existing.length) console.log(`kept as is: ${r.existing.join(", ")}`);
    if (readActiveContext(cwd) === null) console.log("activeContext.md is still the empty template — fill it in (and delete the `unset` line).");
    return 0;
  }
  if (cmd === "status") {
    const s = memoryStatus(cwd);
    if (!s.initialized) { console.log("memory bank is not initialized — run: node .claude/hooks/lib/memory-bank.mjs init"); return 0; }
    console.log(s.active ? `## activeContext.md\n${s.active}` : "## activeContext.md\n(empty — nothing recorded yet)");
    console.log(`\ninbox: ${s.inbox} item(s) · ADRs: ${s.adrs} · known-issues.md: ${s.knownIssues ? "yes" : "no"}`);
    if (s.progress.length) console.log(`\nlast progress:\n${s.progress.join("\n")}`);
    return 0;
  }
  if (cmd === "archive") {
    try {
      const target = archiveActive(cwd, argv[1] && !argv[1].startsWith("--") ? argv[1] : "");
      console.log(`archived to ${target}; activeContext.md reset to the empty template`);
      return 0;
    } catch (e) {
      console.error(`✗ ${e.message}`);
      return 1;
    }
  }
  console.error("usage: memory-bank.mjs init | status | archive <slug>  [--cwd <dir>]");
  return 2;
}

if (process.argv[1] && pathToFileURL(resolve(process.argv[1])).href === import.meta.url) process.exit(cli(process.argv.slice(2)));
