#!/usr/bin/env node
// PreCompact hook. See ../compact-snapshot.md. Never blocks compaction.
import { execFileSync } from "node:child_process";
import { existsSync, readFileSync, writeFileSync, mkdirSync } from "node:fs";
import { join } from "node:path";
import { readHookInput, allow } from "./lib/hook-io.mjs";

const input = readHookInput();
const cwd = input.cwd || process.cwd();

const norm = (s) => s.split(String.fromCharCode(92)).join("/");

function git(args) {
  try {
    return execFileSync("git", args, { cwd, stdio: ["ignore", "pipe", "ignore"], timeout: 5000 }).toString().trim();
  } catch {
    return "";
  }
}

try {
  const branch = git(["rev-parse", "--abbrev-ref", "HEAD"]);
  const changed = git(["status", "--short"]).split("\n").filter(Boolean).slice(0, 40);
  const taskFile = [".claude/tasks/current.md", ".ai/tasks/current.md"].map((f) => join(cwd, f)).find(existsSync);
  const task = taskFile ? readFileSync(taskFile, "utf8").replace(/\r\n/g, "\n").split("\n").slice(0, 25).join("\n") : "";

  const out = [
    `# Session snapshot (saved before compaction)`,
    `saved: ${new Date().toISOString()}`,
    branch ? `branch: ${branch}` : "",
    `trigger: ${input.trigger || "unknown"}`,
    changed.length ? `\n## Changed files (git status --short)\n${changed.join("\n")}` : "",
    task ? `\n## Current task (${norm(taskFile).replace(norm(cwd) + "/", "")})\n${task}` : "",
  ].filter(Boolean).join("\n");

  const dir = join(cwd, ".ai", "state");
  mkdirSync(dir, { recursive: true });
  writeFileSync(join(dir, "session-snapshot.md"), out + "\n");
} catch {
  // never block compaction
}
allow();
