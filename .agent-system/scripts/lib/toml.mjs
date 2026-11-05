// Minimal TOML emitter for Codex agent files. Top-level key/value only:
// strings (basic + multiline), numbers, booleans, string arrays. No tables/dotted keys.

function escBasic(s) {
  return s.replace(/\\/g, "\\\\").replace(/"/g, '\\"').replace(/\n/g, "\\n").replace(/\t/g, "\\t");
}

function emitValue(v) {
  if (typeof v === "boolean" || typeof v === "number") return String(v);
  if (Array.isArray(v)) return `[${v.map((x) => `"${escBasic(String(x))}"`).join(", ")}]`;
  const s = String(v);
  if (s.includes("\n")) {
    // multiline literal-ish: use basic multiline, escape backslashes and triple quotes
    const body = s.replace(/\\/g, "\\\\").replace(/"""/g, '""\\"');
    return `"""\n${body}\n"""`;
  }
  return `"${escBasic(s)}"`;
}

export function dumpToml(obj) {
  const out = [];
  for (const [k, v] of Object.entries(obj)) {
    if (v === undefined || v === null) continue;
    out.push(`${k} = ${emitValue(v)}`);
  }
  return out.join("\n") + "\n";
}
