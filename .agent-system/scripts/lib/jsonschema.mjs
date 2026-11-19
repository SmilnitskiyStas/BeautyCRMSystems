// Tiny JSON Schema validator — supports only the keywords used by schemas/*.json:
// type, enum, const, required, properties, additionalProperties, items, minItems,
// uniqueItems, minLength, maxLength, minimum, maximum, pattern, propertyNames,
// $ref (local "#/$defs/..."), $defs, anyOf. Returns an array of error strings.

function resolveRef(ref, root) {
  if (!ref.startsWith("#/")) throw new Error(`unsupported $ref: ${ref}`);
  return ref
    .slice(2)
    .split("/")
    .reduce((o, k) => (o == null ? o : o[k.replace(/~1/g, "/").replace(/~0/g, "~")]), root);
}

function typeOf(v) {
  if (v === null) return "null";
  if (Array.isArray(v)) return "array";
  if (Number.isInteger(v)) return "integer";
  return typeof v; // string | number | boolean | object
}

function matchesType(v, t) {
  const a = typeOf(v);
  if (t === "number") return a === "number" || a === "integer";
  return a === t;
}

export function validate(value, schema, root = schema, path = "") {
  const errs = [];
  const at = path || "(root)";

  if (schema.$ref) {
    return validate(value, resolveRef(schema.$ref, root), root, path);
  }
  if (schema.anyOf) {
    const ok = schema.anyOf.some((s) => validate(value, s, root, path).length === 0);
    if (!ok) errs.push(`${at}: does not match any of the allowed shapes`);
    return errs;
  }
  if (schema.const !== undefined && value !== schema.const) {
    errs.push(`${at}: must equal ${JSON.stringify(schema.const)}`);
  }
  if (schema.enum && !schema.enum.includes(value)) {
    errs.push(`${at}: ${JSON.stringify(value)} not in {${schema.enum.join(", ")}}`);
  }
  if (schema.type) {
    const types = Array.isArray(schema.type) ? schema.type : [schema.type];
    if (!types.some((t) => matchesType(value, t))) {
      errs.push(`${at}: expected ${types.join("|")}, got ${typeOf(value)}`);
      return errs; // further checks pointless
    }
  }

  const t = typeOf(value);

  if (t === "string") {
    if (schema.minLength != null && value.length < schema.minLength)
      errs.push(`${at}: shorter than ${schema.minLength}`);
    if (schema.maxLength != null && value.length > schema.maxLength)
      errs.push(`${at}: longer than ${schema.maxLength}`);
    if (schema.pattern && !new RegExp(schema.pattern).test(value))
      errs.push(`${at}: does not match /${schema.pattern}/`);
  }

  if (t === "integer" || t === "number") {
    if (schema.minimum != null && value < schema.minimum)
      errs.push(`${at}: below minimum ${schema.minimum}`);
    if (schema.maximum != null && value > schema.maximum)
      errs.push(`${at}: above maximum ${schema.maximum}`);
  }

  if (t === "array") {
    if (schema.minItems != null && value.length < schema.minItems)
      errs.push(`${at}: fewer than ${schema.minItems} items`);
    if (schema.uniqueItems && new Set(value.map((x) => JSON.stringify(x))).size !== value.length)
      errs.push(`${at}: items not unique`);
    if (schema.items)
      value.forEach((item, i) => errs.push(...validate(item, schema.items, root, `${at}[${i}]`)));
  }

  if (t === "object") {
    const keys = Object.keys(value);
    for (const req of schema.required || []) {
      if (!(req in value)) errs.push(`${at}: missing required "${req}"`);
    }
    if (schema.propertyNames) {
      for (const k of keys) {
        const pe = validate(k, schema.propertyNames, root, `${at} key "${k}"`);
        if (pe.length) errs.push(`${at}: invalid property name "${k}"`);
      }
    }
    for (const k of keys) {
      if (schema.properties && k in schema.properties) {
        errs.push(...validate(value[k], schema.properties[k], root, `${at}.${k}`));
      } else if (schema.additionalProperties === false) {
        errs.push(`${at}: unexpected property "${k}"`);
      } else if (schema.additionalProperties && typeof schema.additionalProperties === "object") {
        errs.push(...validate(value[k], schema.additionalProperties, root, `${at}.${k}`));
      }
    }
  }

  return errs;
}
