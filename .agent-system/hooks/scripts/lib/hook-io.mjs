// Shared runtime helpers for generated Claude Code hook scripts. Self-contained by
// design: adapters/claude/generate-hooks.mjs copies hooks/scripts/** verbatim into
// .claude/hooks/**, which has no access to this repo's scripts/lib/ — so nothing here
// may import anything outside this file.
import { readFileSync, existsSync } from "node:fs";
import { join, dirname } from "node:path";

export function readHookInput() {
  try {
    const raw = readFileSync(0, "utf8");
    return raw.trim() ? JSON.parse(raw) : {};
  } catch {
    return {};
  }
}

export function block(reason) {
  process.stderr.write(`${reason}\n`);
  process.exit(2);
}

export function allow() {
  process.exit(0);
}

// Minimal parser for .ai/hooks.config.yaml — NOT a general YAML parser. Supports only
// the shapes this file needs: `key: scalar`, `key: [a, b]`, `key: []`, and a block list
// (`key:` then indented `  - item` lines). See scripts/lib/yaml.mjs for the fuller
// parser this repo's generator uses — duplicated here in miniature because generated
// hook scripts must not import it (see hooks/README.md).
function parseHooksConfig(text) {
  const out = {};
  let currentKey = null;
  for (const raw of text.replace(/\r\n/g, "\n").split("\n")) {
    const line = (raw.includes("#") ? raw.slice(0, raw.indexOf("#")) : raw).replace(/\s+$/, "");
    if (line.trim() === "") continue;
    const listItem = line.match(/^\s+-\s*(.+)$/);
    if (listItem && currentKey) {
      (out[currentKey] ??= []).push(listItem[1].trim().replace(/^["']|["']$/g, ""));
      continue;
    }
    const kv = line.match(/^([A-Za-z0-9_.-]+):\s*(.*)$/);
    if (!kv) continue;
    const [, key, rest] = kv;
    currentKey = key;
    const val = rest.trim();
    if (val === "") { out[key] = out[key] ?? []; continue; }
    if (val === "[]") { out[key] = []; continue; }
    if (val.startsWith("[") && val.endsWith("]")) {
      out[key] = val.slice(1, -1).split(",").map((s) => s.trim().replace(/^["']|["']$/g, "")).filter(Boolean);
      continue;
    }
    out[key] = val.replace(/^["']|["']$/g, "");
  }
  return out;
}

// Walk upward from `startDir` looking for .ai/hooks.config.yaml. Returns {} if none
// exists — every hook must behave sensibly with no config at all.
export function loadHooksConfig(startDir) {
  let dir = startDir || process.cwd();
  for (let i = 0; i < 10; i++) {
    const candidate = join(dir, ".ai", "hooks.config.yaml");
    if (existsSync(candidate)) {
      try {
        return parseHooksConfig(readFileSync(candidate, "utf8"));
      } catch {
        return {};
      }
    }
    const parent = dirname(dir);
    if (parent === dir) break;
    dir = parent;
  }
  return {};
}
