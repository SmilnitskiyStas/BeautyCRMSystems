import { isClosedDate, type ClosedRules, type DateOption } from "@beauty/booking-core";
import { uk } from "@/i18n/uk";

/**
 * Дати БЕЗ локалізованого Intl (ризик Hermes Intl: uk-UA може мовчки віддати en-US).
 * `dateOptions`/`longDate` з core використовують `toLocaleDateString("uk-UA")` - тут їх заміна з текстами з uk.ts.
 * Єдине використання Intl - часова зона закладу через `formatToParts` (незалежно від locale) із перевіркою результату
 * та fallback на дату пристрою.
 */
const pad = (n: number) => String(n).padStart(2, "0");

/** `YYYY-MM-DD` у зоні закладу; будь-який збій/нерозумний результат Intl -> дата пристрою. */
export function todayIn(timezone: string, now: Date = new Date()): string {
  try {
    const parts = new Intl.DateTimeFormat("en-US", { timeZone: timezone, year: "numeric", month: "2-digit", day: "2-digit" }).formatToParts(now);
    const get = (t: string) => parts.find((p) => p.type === t)?.value ?? "";
    const iso = `${get("year")}-${get("month").padStart(2, "0")}-${get("day").padStart(2, "0")}`;
    if (/^\d{4}-\d{2}-\d{2}$/.test(iso)) return iso;
  } catch {
    /* Intl/зона недоступні */
  }
  return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`;
}

export function dateOptions(timezone: string, days = 7, now: Date = new Date(), rules?: ClosedRules): DateOption[] {
  const [y, m, d] = todayIn(timezone, now).split("-").map(Number);
  return Array.from({ length: days }, (_, i) => {
    const day = new Date(Date.UTC(y, m - 1, d + i));
    const iso = day.toISOString().slice(0, 10);
    const num = `${day.getUTCDate()} ${uk.months[day.getUTCMonth()].slice(0, 3)}`;
    return {
      iso,
      weekday: uk.weekdaysShort[day.getUTCDay()],
      closed: isClosedDate(iso, rules),
      label: i === 0 ? uk.today : i === 1 ? uk.tomorrow : num,
    };
  });
}

/** «понеділок, 12 жовтня». */
export function longDate(iso: string): string {
  const m = /^(\d{4})-(\d{2})-(\d{2})/.exec(iso);
  if (!m) return iso;
  const day = new Date(Date.UTC(Number(m[1]), Number(m[2]) - 1, Number(m[3])));
  return `${uk.weekdaysLong[day.getUTCDay()]}, ${day.getUTCDate()} ${uk.months[day.getUTCMonth()]}`;
}
