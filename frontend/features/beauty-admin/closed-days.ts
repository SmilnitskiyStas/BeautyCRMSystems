import type { ClosedDay, LocationClosure, Weekday, WorkingHours } from "./types";

/** Вихідні закладу (§17): день тижня ∈ `closedWeekdays` АБО дата в `closure`. Дата - календарна дата закладу. */

/** Порядок як у `Date#getUTCDay()` (0 = неділя). */
const BY_UTC_DAY: Weekday[] = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];
const HAS_OFFSET = /(Z|[+-]\d{2}:\d{2})$/;

export interface ClosureRules {
  closedWeekdays?: Weekday[];
  closures?: Pick<LocationClosure, "dateFrom" | "dateTo" | "reason">[];
}

/** День тижня календарної дати `YYYY-MM-DD` (без зсувів зони: дата вже "локальна"). */
export function weekdayOf(ymd: string): Weekday {
  return BY_UTC_DAY[new Date(`${ymd.slice(0, 10)}T00:00:00Z`).getUTCDay()];
}

/**
 * Календарна дата закладу для моменту часу. ISO зі зсувом/Z розбираємо як момент і переводимо в зону закладу
 * (запис о 22:30Z у Києві - уже наступний день); локальний `YYYY-MM-DDTHH:mm` вважається часом закладу.
 */
export function localDateIn(iso: string, timeZone?: string): string {
  if (HAS_OFFSET.test(iso)) {
    const d = new Date(iso);
    if (!Number.isNaN(d.getTime())) {
      try {
        return new Intl.DateTimeFormat("en-CA", { timeZone, year: "numeric", month: "2-digit", day: "2-digit" }).format(d);
      } catch {
        /* невалідна зона: падаємо до дати рядка */
      }
    }
  }
  return iso.slice(0, 10);
}

/** Закритий день закладу або `null`. Закриття за датою має пріоритет (несе причину). */
export function closedDayInfo(date: string, rules: ClosureRules): ClosedDay | null {
  const closure = rules.closures?.find((c) => c.dateFrom <= date && date <= c.dateTo);
  if (closure) return { date, source: "closure", ...(closure.reason ? { reason: closure.reason } : {}) };
  if (rules.closedWeekdays?.includes(weekdayOf(date))) return { date, source: "weekday" };
  return null;
}

export const isLocationClosedOn = (date: string, rules: ClosureRules) => closedDayInfo(date, rules) !== null;

export interface MasterLocationRules extends ClosureRules {
  /** Графік майстра в цьому закладі (§9). */
  workingHours?: WorkingHours;
}

/**
 * Чи закритий день у календарі майстра. Майстер може працювати в кількох закладах: дивимось на ті, де за графіком
 * він працює в цей день тижня; якщо таких немає - на всі його заклади. Закритий, лише якщо закриті ВСІ кандидати.
 */
export function closedDayForMaster(date: string, locations: MasterLocationRules[]): ClosedDay | null {
  if (locations.length === 0) return null;
  const key = weekdayOf(date);
  const working = locations.filter((l) => (l.workingHours?.[key]?.length ?? 0) > 0);
  const candidates = working.length > 0 ? working : locations;
  const infos = candidates.map((l) => closedDayInfo(date, l));
  if (infos.some((i) => i === null)) return null;
  return (infos.find((i) => i?.source === "closure") ?? infos[0]) as ClosedDay;
}

/** Причину закриття бачать лише керівники (specialist читає закриття без причини). */
export const canSeeClosureReason = (role: string) => role === "owner" || role === "admin";

/** Підпис закритої колонки календаря. */
export function closedDayLabel(day: ClosedDay, showReason: boolean): string {
  return showReason && day.source === "closure" && day.reason ? `Вихідний: ${day.reason}` : "Вихідний";
}

export const MAX_CLOSURE_REASON = 200;
const MAX_CLOSURE_DAYS = 366;

const utc = (ymd: string) => Date.UTC(+ymd.slice(0, 4), +ymd.slice(5, 7) - 1, +ymd.slice(8, 10));

export function validateClosure(input: { dateFrom: string; dateTo: string; reason?: string }): string | null {
  const ISO = /^\d{4}-\d{2}-\d{2}$/;
  if (!ISO.test(input.dateFrom) || !ISO.test(input.dateTo)) return "Вкажіть дати початку й кінця.";
  if (input.dateTo < input.dateFrom) return "Кінець не може бути раніше за початок.";
  if ((utc(input.dateTo) - utc(input.dateFrom)) / 86_400_000 + 1 > MAX_CLOSURE_DAYS) return "Період закриття не більше 366 днів.";
  if ((input.reason ?? "").trim().length > MAX_CLOSURE_REASON) return "Причина не довша за 200 символів.";
  return null;
}
