#!/usr/bin/env node
// PreToolUse hook (matcher: Write|Edit|MultiEdit). See ../path-deny-list.md.
import { readHookInput, loadHooksConfig, block, allow } from "./lib/hook-io.mjs";

function globToRegExp(glob) {
  let out = "";
  for (let i = 0; i < glob.length; i++) {
    if (glob.slice(i, i + 2) === "**") { out += ".*"; i++; continue; }
    const c = glob[i];
    if (c === "*") { out += "[^/]*"; continue; }
    out += /[.+^${}()|[\]\\]/.test(c) ? `\\${c}` : c;
  }
  return new RegExp(`^${out}$`);
}

const input = readHookInput();
const filePath = input.tool_input?.file_path;
if (!filePath) allow();

const cwd = (input.cwd || process.cwd()).replace(/\\/g, "/");
const cwdEscaped = cwd.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
const rel = filePath.replace(/\\/g, "/").replace(new RegExp(`^${cwdEscaped}/?`), "");

const config = loadHooksConfig(input.cwd || process.cwd());
const denyPaths = config.deny_paths || [];

for (const pattern of denyPaths) {
  if (globToRegExp(pattern).test(rel)) {
    block(
      `path-deny-list: "${rel}" matches denied pattern "${pattern}" in .ai/hooks.config.yaml.\n` +
        `Generated files must be edited via their source and regenerated ` +
        `(node scripts/sync-agents.mjs), not edited directly.`
    );
  }
}
allow();
