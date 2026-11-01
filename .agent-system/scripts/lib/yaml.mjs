// Minimal YAML for a controlled subset — enough for agent frontmatter and *-map.yaml.
// Supports: `key: scalar`, nested maps (indent), block lists (`- scalar`), inline arrays
// (`[a, b]`), `>`/`>-` folded block scalars, `#` comments, booleans, integers, null.
// NOT a general YAML parser. Throws on anything outside the subset.

function stripComment(line) {
  // remove a trailing ` # comment` that is not inside quotes / not a '#...' value
  let inS = false, inD = false;
  for (let i = 0; i < line.length; i++) {
    const c = line[i];
    if (c === "'" && !inD) inS = !inS;
    else if (c === '"' && !inS) inD = !inD;
    else if (c === "#" && !inS && !inD && (i === 0 || line[i - 1] === " " || line[i - 1] === "\t")) {
      return line.slice(0, i);
    }
  }
  return line;
}

function parseScalar(raw) {
  const s = raw.trim();
  if (s === "" || s === "~" || s === "null") return null;
  if (s === "true") return true;
  if (s === "false") return false;
  if (/^-?\d+$/.test(s)) return parseInt(s, 10);
  if (/^-?\d+\.\d+$/.test(s)) return parseFloat(s);
  if ((s.startsWith('"') && s.endsWith('"')) || (s.startsWith("'") && s.endsWith("'"))) {
    return s.slice(1, -1);
  }
  if (s.startsWith("[") && s.endsWith("]")) {
    const inner = s.slice(1, -1).trim();
    if (inner === "") return [];
    return inner.split(",").map((x) => parseScalar(x));
  }
  return s;
}

const indentOf = (l) => l.length - l.trimStart().length;

// Parse a block of lines (already comment-stripped, tabs disallowed) into a value.
function parseBlock(lines, start, end, baseIndent) {
  // list?
  const firstReal = lines.slice(start, end).findIndex((l) => l.trim() !== "");
  if (firstReal === -1) return null;
  const anchor = start + firstReal;
  if (lines[anchor].trimStart().startsWith("- ")) {
    const out = [];
    let i = anchor;
    while (i < end) {
      const line = lines[i];
      if (line.trim() === "") { i++; continue; }
      if (indentOf(line) !== baseIndent || !line.trimStart().startsWith("- ")) break;
      const item = line.trimStart().slice(2);
      out.push(parseScalar(item));
      i++;
    }
    return out;
  }
  // map
  const out = {};
  let i = anchor;
  while (i < end) {
    const line = lines[i];
    if (line.trim() === "") { i++; continue; }
    const ind = indentOf(line);
    if (ind !== baseIndent) throw new Error(`yaml: unexpected indent at line ${i + 1}: "${line}"`);
    const m = line.trimStart().match(/^([A-Za-z0-9_./-]+):(.*)$/);
    if (!m) throw new Error(`yaml: expected "key:" at line ${i + 1}: "${line}"`);
    const key = m[1];
    let rest = m[2];
    if (rest.trim() === "" || rest.trim() === ">" || rest.trim() === ">-" || rest.trim() === "|" || rest.trim() === "|-") {
      // find the child block
      let j = i + 1;
      while (j < end && lines[j].trim() === "") j++;
      const childIndent = j < end ? indentOf(lines[j]) : baseIndent;
      if (j < end && childIndent > baseIndent) {
        if (rest.trim().startsWith(">") || rest.trim().startsWith("|")) {
          const fold = rest.trim().startsWith(">");
          const buf = [];
          let k = j;
          while (k < end && (lines[k].trim() === "" || indentOf(lines[k]) >= childIndent)) {
            buf.push(lines[k].trim() === "" ? "" : lines[k].slice(childIndent));
            k++;
          }
          if (fold) {
            const paras = buf.join("\n").split(/\n\s*\n/).map((p) =>
              p.split("\n").map((x) => x.trim()).filter(Boolean).join(" ")
            );
            out[key] = paras.filter(Boolean).join("\n").trim();
          } else {
            out[key] = buf.join("\n").replace(/\n+$/, "").trim();
          }
          i = k;
        } else {
          // nested list/map — find extent
          let k = j;
          while (k < end && (lines[k].trim() === "" || indentOf(lines[k]) >= childIndent)) k++;
          out[key] = parseBlock(lines, j, k, childIndent);
          i = k;
        }
      } else {
        out[key] = null;
        i++;
      }
    } else {
      out[key] = parseScalar(rest);
      i++;
    }
  }
  return out;
}

export function parseYaml(text) {
  const lines = text
    .replace(/\r\n/g, "\n")
    .split("\n")
    .map((l) => stripComment(l).replace(/\s+$/, ""));
  if (lines.some((l) => l.includes("\t"))) throw new Error("yaml: tabs are not allowed");
  // drop leading '---' / trailing '---' / '...'
  let s = 0, e = lines.length;
  while (s < e && (lines[s].trim() === "" || lines[s].trim() === "---")) s++;
  while (e > s && (lines[e - 1].trim() === "" || lines[e - 1].trim() === "---" || lines[e - 1].trim() === "...")) e--;
  if (s >= e) return {};
  return parseBlock(lines, s, e, indentOf(lines[s]));
}

// Split a markdown file into { data, body } where data is parsed frontmatter.
export function parseFrontmatter(text) {
  const t = text.replace(/\r\n/g, "\n");
  if (!t.startsWith("---\n")) return { data: {}, body: t };
  const end = t.indexOf("\n---\n", 4);
  const endAlt = t.endsWith("\n---") ? t.length - 4 : -1;
  const cut = end === -1 ? endAlt : end;
  if (cut === -1) return { data: {}, body: t };
  const fm = t.slice(4, cut);
  const body = end === -1 ? "" : t.slice(end + 5);
  return { data: parseYaml(fm), body: body.replace(/^\n+/, "") };
}

// --- dumping (subset, for generated frontmatter) ---

function dumpScalar(v) {
  if (v === null || v === undefined) return "";
  if (typeof v === "boolean" || typeof v === "number") return String(v);
  const s = String(v);
  if (s === "" || /^[\s]|[\s]$/.test(s) || /[:#]/.test(s) && !/^[A-Za-z0-9 ,._/-]+$/.test(s)) {
    return JSON.stringify(s);
  }
  return s;
}

export function dumpYaml(obj, indent = 0) {
  const pad = "  ".repeat(indent);
  const out = [];
  for (const [k, v] of Object.entries(obj)) {
    if (v === null || v === undefined) continue;
    if (Array.isArray(v)) {
      if (v.length === 0) { out.push(`${pad}${k}: []`); continue; }
      out.push(`${pad}${k}:`);
      for (const item of v) out.push(`${pad}  - ${dumpScalar(item)}`);
    } else if (typeof v === "object") {
      out.push(`${pad}${k}:`);
      out.push(dumpYaml(v, indent + 1));
    } else {
      out.push(`${pad}${k}: ${dumpScalar(v)}`);
    }
  }
  return out.join("\n");
}
