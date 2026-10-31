// Pure time/text helpers of the reminder worker (§22.5): venue-local time via Intl, quiet hours, message text.
// UTC / ISO strings must never reach a client-facing text.

const partsCache = new Map<string, Intl.DateTimeFormat>();
const textCache = new Map<string, Intl.DateTimeFormat>();

function partsFormat(tz: string): Intl.DateTimeFormat {
  let f = partsCache.get(tz);
  if (!f) {
    f = new Intl.DateTimeFormat("en-US", {
      timeZone: tz, hourCycle: "h23", year: "numeric", month: "numeric", day: "numeric",
      hour: "numeric", minute: "numeric", second: "numeric",
    });
    partsCache.set(tz, f);
  }
  return f;
}

export interface LocalParts { year: number; month: number; day: number; hour: number; minute: number; second: number }

/** Wall-clock components of an instant in the given IANA zone. */
export function localParts(date: Date, tz: string): LocalParts {
  const o: Record<string, number> = {};
  for (const p of partsFormat(tz).formatToParts(date)) if (p.type !== "literal") o[p.type] = Number(p.value);
  return { year: o.year!, month: o.month!, day: o.day!, hour: o.hour! % 24, minute: o.minute!, second: o.second! };
}

/** Offset (local - UTC) in ms at the instant. */
function offsetMs(t: number, tz: string): number {
  const p = localParts(new Date(t), tz);
  const asUtc = Date.UTC(p.year, p.month - 1, p.day, p.hour, p.minute, p.second);
  return asUtc - (t - (((t % 1000) + 1000) % 1000));
}

/**
 * Instant for a local wall-clock time. A time that does not exist (spring-forward gap) moves forward by the gap;
 * an ambiguous one (fall-back) resolves to the later occurrence. Deterministic, never throws for valid zones.
 */
export function zonedTimeToUtc(year: number, month: number, day: number, hour: number, minute: number, tz: string): Date {
  const guess = Date.UTC(year, month - 1, day, hour, minute);
  const o1 = offsetMs(guess, tz);
  const t1 = guess - o1;
  const o2 = offsetMs(t1, tz);
  if (o2 === o1) return new Date(t1);
  const t2 = guess - o2;
  return new Date(t2);
}

export interface QuietWindow { startMinutes: number; endMinutes: number }

/** "HH:mm" or "HH:mm:ss" -> minutes since midnight. */
export function parseClock(value: string): number {
  const m = /^(\d{1,2}):(\d{2})/.exec(value);
  if (!m) throw new Error("Invalid clock value");
  return Number(m[1]) * 60 + Number(m[2]);
}

/** Is the local time inside [start, end)? The window may wrap midnight (22:00-08:00). */
export function isQuiet(now: Date, tz: string, w: QuietWindow): boolean {
  const p = localParts(now, tz);
  const m = p.hour * 60 + p.minute;
  return w.startMinutes < w.endMinutes ? m >= w.startMinutes && m < w.endMinutes : m >= w.startMinutes || m < w.endMinutes;
}

/** The next local occurrence of quiet_end strictly after `now`. */
export function nextQuietEnd(now: Date, tz: string, endMinutes: number): Date {
  const p = localParts(now, tz);
  const h = Math.floor(endMinutes / 60), mi = endMinutes % 60;
  const today = zonedTimeToUtc(p.year, p.month, p.day, h, mi, tz);
  if (today.getTime() > now.getTime()) return today;
  const next = new Date(Date.UTC(p.year, p.month - 1, p.day + 1));
  return zonedTimeToUtc(next.getUTCFullYear(), next.getUTCMonth() + 1, next.getUTCDate(), h, mi, tz);
}

/** "пт, 31.10 о 14:30" in the venue zone. */
export function formatVisitTime(date: Date, tz: string): string {
  let f = textCache.get(tz);
  if (!f) {
    f = new Intl.DateTimeFormat("uk-UA", {
      timeZone: tz, weekday: "short", day: "2-digit", month: "2-digit", hour: "2-digit", minute: "2-digit", hourCycle: "h23",
    });
    textCache.set(tz, f);
  }
  const o: Record<string, string> = {};
  for (const p of f.formatToParts(date)) if (p.type !== "literal") o[p.type] = p.value;
  return `${o.weekday}, ${o.day}.${o.month} о ${o.hour}:${o.minute}`;
}

/** Unicode Cf (bidi controls, zero-width, BOM, soft hyphen): invisible text that can disguise a message (L9). */
export const stripFormatChars = (value: string): string => value.replace(/\p{Cf}/gu, "");

/** One line, no control or format (Cf) characters, at most 80 code points. */
export function sanitizeLine(value: string | null | undefined, max = 80): string {
  const clean = stripFormatChars(value ?? "").replace(/[\u0000-\u001f\u007f-\u009f]/g, " ").replace(/\s+/g, " ").trim();
  return [...clean].slice(0, max).join("").trim();
}

export const STOP_LINE = "Щоб не отримувати нагадування, напишіть «Стоп».";

const LEGACY_LEAD: Record<string, string> = { "1h": "через 1 годину", "2h": "через 2 години" };

/**
 * Reminder text (§22.5). `legacyOption` (rows without offset_minutes) adds "(через 1 годину)" so a legacy reminder keeps
 * saying how far the visit is; new rows carry the exact local time only.
 */
export function buildReminderText(o: {
  service: string | null; startsAt: Date; timeZone: string; locationName: string | null; locationAddress: string | null;
  legacyOption?: string | null;
}): string {
  const lead = o.legacyOption ? LEGACY_LEAD[o.legacyOption] : undefined;
  const service = sanitizeLine(o.service) || "візит";
  const name = sanitizeLine(o.locationName);
  const address = sanitizeLine(o.locationAddress);
  const lines = [`Нагадуємо про ваш візит: ${service}, ${formatVisitTime(o.startsAt, o.timeZone)}${lead ? ` (${lead})` : ""}.`];
  if (name) lines.push(`Заклад: ${name}${address ? `, ${address}` : ""}.`);
  lines.push("Скасувати чи перенести — напишіть сюди.", STOP_LINE);
  return lines.join("\n");
}
