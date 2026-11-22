#!/usr/bin/env node
// Which version of the agent library is this — and is the generated output from it?
//
//   node scripts/version.mjs                          # library checkout
//   node .agent-system/scripts/version.mjs --project .   # a consuming project
//   ... --latest                                      # also compare with the newest v* tag on GitHub
//   ... --json
import { existsSync } from "node:fs";
import { isProjectMode, ROOT } from "./lib/core.mjs";
import { libraryInfo, readStamp, latestRemoteVersion, compareSemver } from "./lib/version.mjs";

const argv = process.argv.slice(2);
const info = libraryInfo();
const targets = [[".claude", "claude"], [".codex", "codex"]].map(([dir, rt]) => ({ rt, dir, stamp: readStamp(dir) })).filter((t) => t.stamp);
const stale = targets.filter((t) => t.stamp.agent_system !== info.version);
const latest = argv.includes("--latest") ? latestRemoteVersion() : undefined;

if (argv.includes("--json")) {
  console.log(JSON.stringify({ ...info, mode: isProjectMode() ? "project" : "library", generated: targets.map((t) => ({ runtime: t.rt, version: t.stamp.agent_system })), latest: latest ?? null }, null, 2));
  process.exit(0);
}

console.log(`agent-system ${info.version}${info.commit ? ` (${info.commit})` : ""}${info.vendored ? ` — vendored ${info.vendoredAt || ""}`.trimEnd() : existsSync(ROOT) ? " — library checkout" : ""}`);
if (!targets.length) console.log("generated output: none yet — run sync-agents");
for (const t of targets) {
  const same = t.stamp.agent_system === info.version;
  console.log(`generated ${t.rt}: ${t.stamp.agent_system} ${same ? "✓" : "✗ differs from the library — run sync-agents"}`);
}
if (latest === null) console.log("latest release: unknown (offline or no v* tags on the remote)");
else if (latest !== undefined) {
  const c = compareSemver(info.version, latest);
  console.log(`latest release: ${latest} ${c >= 0 ? "✓ up to date" : "— newer than this copy; update: node <library>/scripts/vendor-into.mjs <project>"}`);
}
process.exit(stale.length ? 1 : 0);
