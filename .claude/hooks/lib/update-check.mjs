#!/usr/bin/env node
// "Is there a newer agent-system release?" — without ever slowing down a session start.
//
// The SessionStart hook only READS a small cache (.ai/state/update-check.json). When the cache is
// older than a day it spawns a detached `node update-check.mjs --refresh`, which asks the remote
// for its v<semver> tags (`git ls-remote`, short timeout) and rewrites the cache. So a session
// sees the result of the previous check — a release is noticed at most one session late.
//
// Self-contained (node:* only): copied verbatim into .claude/hooks/lib/ and into plugins.
// Off switches: `update_check: off` in .ai/hooks.config.yaml, or env AGENT_SYSTEM_UPDATE_CHECK=off.
// Repo override: `update_repo:` in .ai/hooks.config.yaml, or env AGENT_SYSTEM_UPDATE_URL.
import { execFileSync, spawn } from "node:child_process";
import { existsSync, readFileSync, writeFileSync, mkdirSync } from "node:fs";
import { join, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

export const DEFAULT_REPO = "https://github.com/SmilnitskiyStas/AI_agent_only_v2.git";
export const TTL_MS = 24 * 60 * 60 * 1000;

export function compareSemver(a, b) {
  const pa = String(a).split(".").map((n) => parseInt(n, 10) || 0);
  const pb = String(b).split(".").map((n) => parseInt(n, 10) || 0);
  for (let i = 0; i < 3; i++) if ((pa[i] || 0) !== (pb[i] || 0)) return (pa[i] || 0) - (pb[i] || 0);
  return 0;
}

export function parseLatestTag(lsRemoteOutput) {
  const versions = [...String(lsRemoteOutput).matchAll(/refs\/tags\/v(\d+\.\d+\.\d+)\s*$/gm)].map((m) => m[1]);
  return versions.sort(compareSemver).pop() || null;
}

const cacheFile = (cwd) => join(cwd, ".ai", "state", "update-check.json");

export function readCache(cwd) {
  try {
    return JSON.parse(readFileSync(cacheFile(cwd), "utf8"));
  } catch {
    return null;
  }
}

export function updateDisabled(cfg = {}) {
  return String(process.env.AGENT_SYSTEM_UPDATE_CHECK || cfg.update_check || "on").toLowerCase() === "off";
}

export const updateRepo = (cfg = {}) => process.env.AGENT_SYSTEM_UPDATE_URL || cfg.update_repo || DEFAULT_REPO;

// Newer version than `current` according to the cache, else null.
export function cachedUpdate(cwd, current, repo) {
  const c = readCache(cwd);
  if (!c || !c.latest || (repo && c.repo && c.repo !== repo)) return null;
  return compareSemver(c.latest, current) > 0 ? c.latest : null;
}

export function cacheIsStale(cwd, repo, now = Date.now()) {
  const c = readCache(cwd);
  if (!c || !c.checked_at) return true;
  if (c.repo && c.repo !== repo) return true;
  return now - Date.parse(c.checked_at) > TTL_MS;
}

// Fire-and-forget refresh; never throws, never waits.
export function refreshInBackground(cwd, repo) {
  try {
    const child = spawn(process.execPath, [fileURLToPath(import.meta.url), "--refresh", "--cwd", cwd, "--repo", repo], {
      detached: true,
      stdio: "ignore",
      windowsHide: true,
    });
    child.unref();
  } catch {
    // best effort
  }
}

// Synchronous refresh (what the detached child runs). Records the attempt even on failure so an
// offline machine does not retry on every session start; keeps the last known `latest`.
export function refreshNow(cwd, repo) {
  const prev = readCache(cwd);
  let latest = prev?.latest || null;
  try {
    const out = execFileSync("git", ["ls-remote", "--tags", "--refs", repo], { stdio: ["ignore", "pipe", "ignore"], timeout: 8000 }).toString();
    latest = parseLatestTag(out) || latest;
  } catch {
    // offline / no access — keep previous
  }
  try {
    mkdirSync(join(cwd, ".ai", "state"), { recursive: true });
    writeFileSync(cacheFile(cwd), JSON.stringify({ checked_at: new Date().toISOString(), latest, repo }, null, 2) + "\n");
  } catch {
    // read-only checkout: nothing to cache
  }
  return latest;
}

if (process.argv[1] && pathToFileURL(resolve(process.argv[1])).href === import.meta.url) {
  const a = process.argv.slice(2);
  const opt = (n, d) => (a.includes(n) ? a[a.indexOf(n) + 1] : d);
  if (a.includes("--refresh")) {
    const latest = refreshNow(resolve(opt("--cwd", process.cwd())), opt("--repo", DEFAULT_REPO));
    console.log(latest ? `latest release: ${latest}` : "latest release: unknown");
  } else {
    console.error("usage: update-check.mjs --refresh [--cwd <dir>] [--repo <url>]");
    process.exit(2);
  }
}
