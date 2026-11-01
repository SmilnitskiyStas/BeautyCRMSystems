import { createHash, randomInt } from "node:crypto";

/** §26.6: Crockford alphabet (no I, L, O, U), 10 symbols = 50 bits from a CSPRNG. */
export const CROCKFORD = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

export function generatePersonalCode(): string {
  let s = "";
  for (let i = 0; i < 10; i++) s += CROCKFORD[randomInt(CROCKFORD.length)];
  return s;
}

/** Display form `XXXXX-XXXXX` for a 10-symbol personal code; other codes are shown as is. */
export const formatCode = (code: string): string => (code.length === 10 ? `${code.slice(0, 5)}-${code.slice(5)}` : code);

/** Upper case, no spaces or `-`, O -> 0, I|L -> 1. */
export const normalizeCode = (code: string): string =>
  code.toUpperCase().replace(/[\s-]+/g, "").replace(/O/g, "0").replace(/[IL]/g, "1");

/** lower(hex(SHA-256(UTF-8("coupon:" + normalize(code))))). */
export const hashCode = (code: string): string => createHash("sha256").update(`coupon:${normalizeCode(code)}`, "utf8").digest("hex");

/** Last 3 symbols of the normalized code. */
export const codeHint = (code: string): string => normalizeCode(code).slice(-3);
