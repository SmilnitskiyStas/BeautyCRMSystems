#!/usr/bin/env node
// SessionStart hook (matcher: startup|resume|clear). See ../session-context.md.
// 1) tells the USER which agent-system version is active (systemMessage — costs no tokens);
// 2) gives the MODEL the active context from .ai/memory/activeContext.md (capped).
import { existsSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { readHookInput, allow, loadHooksConfig } from "./lib/hook-io.mjs";
import { readActiveContext } from "./lib/memory-bank.mjs";
import { updateDisabled, updateRepo, cachedUpdate, cacheIsStale, refreshInBackground } from "./lib/update-check.mjs";

const MAX_CHARS = 2000;

function readVersion(cwd) {
  const candidates = [];
  if (process.env.CLAUDE_PLUGIN_ROOT) candidates.push(join(process.env.CLAUDE_PLUGIN_ROOT, ".claude-plugin", "plugin.json"));
  candidates.push(join(cwd, ".claude", "agent-system.json"), join(cwd, ".codex", "agent-system.json"));
  for (const f of candidates) {
    try {
      if (!existsSync(f)) continue;
      const j = JSON.parse(readFileSync(f, "utf8"));
      const v = j.agent_system || j.version;
      if (v) return { version: v, plugin: f.endsWith("plugin.json") };
    } catch {
      // try the next candidate
    }
  }
  return null;
}

const input = readHookInput();
try {
  const cwd = input.cwd || process.cwd();
  const cfg = loadHooksConfig(cwd);
  if (String(cfg.session_context || "on").toLowerCase() === "off") allow();

  const v = readVersion(cwd);
  const active = readActiveContext(cwd, MAX_CHARS);
  if (!v && !active) allow();

  // update notice: read the cache only; a stale cache triggers a detached background refresh.
  // Plugins update through their marketplace, so no check there.
  let newer = null;
  if (v && !v.plugin && !updateDisabled(cfg)) {
    const repo = updateRepo(cfg);
    newer = cachedUpdate(cwd, v.version, repo);
    if (cacheIsStale(cwd, repo)) refreshInBackground(cwd, repo);
  }

  const out = {};
  if (v) {
    out.systemMessage =
      `agent-system v${v.version}${v.plugin ? " (plugin)" : ""}` +
      (newer ? ` · update ${newer} available → /update-agents` : "") +
      (active ? " · memory bank: active context loaded" : "");
  }
  if (active) {
    out.hookSpecificOutput = {
      hookEventName: "SessionStart",
      additionalContext: `Project memory bank — active context (.ai/memory/activeContext.md). Treat it as the current state of the work; verify against the code before relying on details:\n\n${active}`,
    };
  }
  process.stdout.write(JSON.stringify(out));
} catch {
  // never block a session start
}
allow();
