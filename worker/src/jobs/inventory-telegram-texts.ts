/**
 * §32.24.5 texts of the owner's Telegram inventory alerts (uk, plain text, no parse_mode).
 * Allowed: venue, product, quantity + unit, lot number, expiry date, kind, link. NEVER: client data, specialist names,
 * appointment_id, operation_id, prices.
 */
export interface AlertTextInput {
  kind: "low_stock" | "expiring" | "shortage";
  location: string;
  product: string;
  unit: string;
  quantity: number | string;
  threshold: number | string | null;
  lot: string | null;
  expiresOn: string | null; // ISO yyyy-mm-dd
  daysBucket: number | null;
}

const MAX_NAME = 80;

/** Strips control characters and truncates to 80 chars. */
export function cleanName(s: string | null | undefined): string {
  const t = (s ?? "").replace(/[\p{Cc}\p{Cf}\p{Zl}\p{Zp}]/gu, " ").replace(/\s+/g, " ").trim(); // control, format/bidi, line separators
  const chars = [...t];
  return chars.length > MAX_NAME ? `${chars.slice(0, MAX_NAME - 1).join("")}…` : t;
}

/** uk number format: comma, no trailing zeros (numeric(14,3) arrives as a string). */
export function fmtNum(v: number | string): string {
  const n = Number(v);
  if (!Number.isFinite(n)) return "0";
  return String(Math.round(n * 1000) / 1000).replace(".", ",");
}

export function fmtDate(iso: string): string {
  const m = /^(\d{4})-(\d{2})-(\d{2})/.exec(iso);
  return m ? `${m[3]}.${m[2]}.${m[1]}` : "";
}

export function alertsUrl(base: string | undefined, locationId?: string): string {
  return base ? `\n${base}/beauty/inventory/alerts${locationId ? `?locationId=${locationId}` : ""}` : "";
}

export function alertText(a: AlertTextInput, url: string): string {
  const head = `Склад · ${cleanName(a.location)}`;
  const p = cleanName(a.product), u = cleanName(a.unit);
  switch (a.kind) {
    case "low_stock":
      return `${head}\nМало: ${p} — залишок ${fmtNum(a.quantity)} ${u} (мінімум ${fmtNum(a.threshold ?? 0)}).${url}`;
    case "expiring": {
      const lot = a.lot ? cleanName(a.lot) : "без номера";
      const label = a.daysBucket === 0 ? "Прострочено" : "Закінчується термін";
      return `${head}\n${label}: ${p}, партія ${lot} — ${fmtNum(a.quantity)} ${u}, до ${fmtDate(a.expiresOn ?? "")}.${url}`;
    }
    case "shortage":
      return `${head}\nНестача при списанні на послугу: ${p} — бракує ${fmtNum(a.quantity)} ${u}. Поповніть залишок або проведіть інвентаризацію.${url}`;
  }
}

export interface DigestRow { location: string; low: number; expiring: number; shortage: number }

const MAX_DIGEST_ROWS = 30;

export function digestText(total: number, rows: DigestRow[], url: string): string {
  const sorted = [...rows].sort((x, y) => x.location.localeCompare(y.location, "uk"));
  const lines = sorted.slice(0, MAX_DIGEST_ROWS).map((r) => `• ${cleanName(r.location)}: мало — ${r.low}, термін — ${r.expiring}, нестача — ${r.shortage}`);
  if (sorted.length > MAX_DIGEST_ROWS) lines.push(`і ще ${sorted.length - MAX_DIGEST_ROWS}`);
  return `Склад: ${total} нових сповіщень\n${lines.join("\n")}${url}`;
}
