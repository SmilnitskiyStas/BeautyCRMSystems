/**
 * Розбір публічного коду закладу та посилань на нього (ADR-010 §3, ADR-011 §1).
 * Вхід - НЕДОВІРЕНИЙ (QR, посилання, ручний ввід): до будь-якого запиту проходить нормалізацію й перевірку формату.
 * Посилання несуть лише публічний код; усе інше (query, hash, userinfo) ігнорується/відхиляється.
 */

/** 8 символів Crockford Base32 (без I, L, O, U). */
export const PUBLIC_CODE_RE = /^[0-9A-HJKMNP-TV-Z]{8}$/;

/** trim, верхній регістр, без `-` і пробілів, аліаси Crockford (O->0, I/L->1). `null`, якщо формат хибний. */
export function normalizePublicCode(input: string | null | undefined): string | null {
  if (typeof input !== "string") return null;
  const code = input
    .trim()
    .toUpperCase()
    .replace(/[-\s]/g, "")
    .replace(/O/g, "0")
    .replace(/[IL]/g, "1");
  return PUBLIC_CODE_RE.test(code) ? code : null;
}

/** `XXXX-XXXX` для показу людині. */
export const formatPublicCode = (code: string): string => `${code.slice(0, 4)}-${code.slice(4)}`;

export interface LinkConfig {
  /** Домен universal/app links з конфігу (без схеми й слеша). */
  webDomain: string;
  /** Власна схема, без `://`. */
  scheme: string;
}

const URL_RE = /^([a-z][a-z0-9+.-]*):\/\/([^/?#]*)([^?#]*)/i;

/**
 * Код закладу з посилання: `beautycrm://l/{code}` або `https://{domain}/l/{code}`.
 * Відхиляє: чужий домен, `http`, userinfo (`user@host`), нестандартний порт, зайві сегменти шляху, хибний код.
 */
export function parseLocationLink(url: string | null | undefined, cfg: LinkConfig): string | null {
  if (typeof url !== "string") return null;
  const m = URL_RE.exec(url.trim());
  if (!m) return null;
  const proto = m[1].toLowerCase();
  const authority = m[2];
  const path = m[3];

  let segments: string[];
  if (proto === cfg.scheme.toLowerCase()) {
    // beautycrm://l/CODE -> authority="l", path="/CODE" (також терпимо beautycrm:///l/CODE)
    segments = [authority, ...path.split("/")].filter((s) => s.length > 0);
  } else if (proto === "https") {
    if (authority.toLowerCase() !== cfg.webDomain.toLowerCase()) return null; // userinfo/порт теж не збігаються
    segments = path.split("/").filter((s) => s.length > 0);
  } else {
    return null;
  }
  if (segments.length !== 2 || segments[0] !== "l") return null;
  let raw = segments[1];
  try {
    raw = decodeURIComponent(raw);
  } catch {
    return null;
  }
  return normalizePublicCode(raw);
}

/** Ручний ввід/QR: або повне посилання, або сам код. */
export function parseLocationInput(input: string | null | undefined, cfg: LinkConfig): string | null {
  if (typeof input !== "string") return null;
  return parseLocationLink(input, cfg) ?? normalizePublicCode(input);
}
