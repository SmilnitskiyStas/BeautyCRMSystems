/**
 * Час у зоні закладу (аудит TASK-705, F2). Backend віддає `startsAt` як момент у UTC (`...+00:00`) разом із
 * `timezone` закладу (§14.5): для показу переводимо момент у стінний час зони, а при переносі збираємо момент
 * із локального часу закладу. Усе на Intl (DST враховано); без зони - зона браузера.
 */

const HAS_OFFSET = /(Z|[+-]\d{2}:\d{2})$/;
const pad = (n: number) => String(n).padStart(2, "0");

type Parts = { y: number; mo: number; d: number; h: number; mi: number; s: number };

function partsOf(ms: number, timeZone?: string): Parts {
  const make = (tz?: string) =>
    new Intl.DateTimeFormat("en-CA", {
      timeZone: tz,
      year: "numeric",
      month: "2-digit",
      day: "2-digit",
      hour: "2-digit",
      minute: "2-digit",
      second: "2-digit",
      hourCycle: "h23",
    });
  let f: Intl.DateTimeFormat;
  try {
    f = make(timeZone);
  } catch {
    f = make(undefined); // невалідна зона: зона браузера
  }
  const p = Object.fromEntries(f.formatToParts(new Date(ms)).map((x) => [x.type, x.value]));
  return { y: +p.year, mo: +p.month, d: +p.day, h: +p.hour % 24, mi: +p.minute, s: +p.second };
}

/** Різниця «стінний час зони як UTC» мінус момент, мс (зсув зони на цей момент). */
function zoneOffsetMs(ms: number, timeZone?: string): number {
  const second = Math.floor(ms / 1000) * 1000;
  const p = partsOf(second, timeZone);
  return Date.UTC(p.y, p.mo - 1, p.d, p.h, p.mi, p.s) - second;
}

/**
 * Локальний час закладу `YYYY-MM-DDTHH:mm` для ISO-моменту. Рядок без зсуву вже вважається локальним часом закладу
 * і повертається як є (mock).
 */
export function instantToLocal(iso: string, timeZone?: string): string {
  if (!HAS_OFFSET.test(iso)) return iso.slice(0, 16);
  const ms = Date.parse(iso);
  if (Number.isNaN(ms)) return iso.slice(0, 16);
  const p = partsOf(ms, timeZone);
  return `${p.y}-${pad(p.mo)}-${pad(p.d)}T${pad(p.h)}:${pad(p.mi)}`;
}

/** Момент часу для локального часу закладу (`YYYY-MM-DDTHH:mm[:ss]`). */
export function localToInstant(local: string, timeZone?: string): Date {
  const [date, time = "00:00"] = local.split("T");
  const [y, mo, d] = date.split("-").map(Number);
  const [h, mi, s = 0] = time.split(":").map(Number);
  if (!timeZone) return new Date(y, mo - 1, d, h, mi, s);
  try {
    new Intl.DateTimeFormat("en-CA", { timeZone });
  } catch {
    return new Date(y, mo - 1, d, h, mi, s);
  }
  const wall = Date.UTC(y, mo - 1, d, h, mi, s);
  const first = wall - zoneOffsetMs(wall, timeZone);
  return new Date(wall - zoneOffsetMs(first, timeZone));
}

/** UTC-момент для API: `2026-10-26T08:00:00Z`. */
export const localToUtcIso = (local: string, timeZone?: string): string =>
  localToInstant(local, timeZone).toISOString().replace(/\.\d{3}Z$/, "Z");

/** Календарна дата `YYYY-MM-DD` «сьогодні» у зоні. */
export function todayInZone(timeZone?: string, now: Date = new Date()): string {
  const p = partsOf(now.getTime(), timeZone);
  return `${p.y}-${pad(p.mo)}-${pad(p.d)}`;
}

export function addDaysYmd(ymd: string, n: number): string {
  return new Date(Date.UTC(+ymd.slice(0, 4), +ymd.slice(5, 7) - 1, +ymd.slice(8, 10) + n)).toISOString().slice(0, 10);
}

/** Понеділок тижня, що містить дату `YYYY-MM-DD`. */
export function mondayOfYmd(ymd: string): string {
  const dow = new Date(`${ymd}T00:00:00Z`).getUTCDay(); // 0 = неділя
  return addDaysYmd(ymd, -((dow + 6) % 7));
}

/** Поточний тиждень (пн..нд) у зоні закладу й межі як моменти часу для `from`/`to`. */
export function weekInZone(timeZone?: string, now: Date = new Date()) {
  const monday = mondayOfYmd(todayInZone(timeZone, now));
  return {
    monday,
    sunday: addDaysYmd(monday, 6),
    from: localToInstant(`${monday}T00:00`, timeZone),
    to: localToInstant(`${addDaysYmd(monday, 7)}T00:00`, timeZone),
  };
}
