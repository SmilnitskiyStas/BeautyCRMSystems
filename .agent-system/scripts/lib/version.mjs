// Library version + generated-output stamp.
//
// Version source: <root>/VERSION. In a vendored copy (<project>/.agent-system/) the commit comes
// from .vendor.json (written by vendor-into); in the library repo it comes from git.
// The stamp written into generated output carries the version ONLY (no commit, no time) so
// `sync-agents --check` stays deterministic between commits.
import { readFileSync, existsSync, writeFileSync, mkdirSync } from "node:fs";
import { join, dirname } from "node:path";
import { execFileSync } from "node:child_process";
import { ROOT, p, pp, isProjectMode } from "./core.mjs";

export const REPO_URL = "https://github.com/SmilnitskiyStas/AI_agent_only_v2.git";
export const STAMP_FILE = "agent-system.json";

export function libraryInfo(root = ROOT) {
  let version = "unknown";
  try { version = readFileSync(join(root, "VERSION"), "utf8").trim() || "unknown"; } catch { /* keep unknown */ }
  const info = { version, commit: null, vendored: false, vendoredAt: null };
  const vj = join(root, ".vendor.json");
  if (existsSync(vj)) {
    try {
      const j = JSON.parse(readFileSync(vj, "utf8"));
      info.commit = j.commit || null;
      info.vendored = true;
      info.vendoredAt = j.vendored || null;
    } catch { /* ignore */ }
  } else {
    try {
      info.commit = execFileSync("git", ["rev-parse", "--short", "HEAD"], { cwd: root, stdio: ["ignore", "pipe", "ignore"] }).toString().trim();
    } catch { /* not a git checkout */ }
  }
  return info;
}

export function compareSemver(a, b) {
  const pa = String(a).split(".").map((n) => parseInt(n, 10) || 0);
  const pb = String(b).split(".").map((n) => parseInt(n, 10) || 0);
  for (let i = 0; i < 3; i++) if ((pa[i] || 0) !== (pb[i] || 0)) return (pa[i] || 0) - (pb[i] || 0);
  return 0;
}

// Highest v<semver> tag on the remote, or null (offline / no tags).
export function latestRemoteVersion(url = process.env.AGENT_SYSTEM_UPDATE_URL || REPO_URL) {
  try {
    const out = execFileSync("git", ["ls-remote", "--tags", "--refs", url], { stdio: ["ignore", "pipe", "ignore"], timeout: 15000 }).toString();
    const versions = [...out.matchAll(/refs\/tags\/v(\d+\.\d+\.\d+)\s*$/gm)].map((m) => m[1]);
    return versions.sort(compareSemver).pop() || null;
  } catch {
    return null;
  }
}

const outPath = (...s) => (isProjectMode() ? pp(...s) : p(...s));

export function stampPath(dirName) {
  return outPath(dirName, STAMP_FILE);
}

export function readStamp(dirName) {
  try { return JSON.parse(readFileSync(stampPath(dirName), "utf8")); } catch { return null; }
}

// Write (or, with check, verify) <dirName>/agent-system.json for one runtime.
export function syncStamp({ check, dirName, runtime }) {
  const content = `${JSON.stringify({ agent_system: libraryInfo().version, runtime }, null, 2)}\n`;
  const target = stampPath(dirName);
  const cur = existsSync(target) ? readFileSync(target, "utf8") : null;
  if (cur === content) return { drift: 0, changes: [] };
  if (!check) { mkdirSync(dirname(target), { recursive: true }); writeFileSync(target, content); }
  return { drift: 1, changes: [`${cur === null ? "create" : "update"} ${dirName}/${STAMP_FILE}`] };
}
