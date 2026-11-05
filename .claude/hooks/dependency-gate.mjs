#!/usr/bin/env node
// PreToolUse hook (matcher: Bash). See ../dependency-gate.md.
import { readHookInput, loadHooksConfig, block, allow } from "./lib/hook-io.mjs";

const INSTALL_RE =
  /\b(?:npm\s+(?:i|install|add|update|upgrade)|pnpm\s+(?:add|update|up)|yarn\s+(?:add|upgrade)|pip\s+install)\b(.*)/i;

const input = readHookInput();
const command = input.tool_input?.command || "";
const match = command.match(INSTALL_RE);
if (!match) allow();

const packages = match[1]
  .split(/\s+/)
  .map((a) => a.trim())
  .filter((a) => a && !a.startsWith("-"))
  .map((a) => a.replace(/^(.+)@[^@/]+$/, "$1")); // strip a trailing @version

if (!packages.length) allow();

const config = loadHooksConfig(input.cwd || process.cwd());
const approved = new Set(config.approved_packages || []);
const unapproved = packages.filter((pkg) => !approved.has(pkg));

if (unapproved.length) {
  block(
    `dependency-gate: package(s) not in .ai/hooks.config.yaml -> approved_packages: ${unapproved.join(", ")}.\n` +
      `Get sign-off first — see workflow/human-gates.md.`
  );
}
allow();
