import { closedDayInfo, weekdayOf, type ClosureRules } from "@/features/beauty-admin/closed-days";
import type { ClosedDay, DayAvailability, DayState, MinuteInterval, Weekday, WorkingHours } from "@/features/beauty-admin/types";
import { addDaysYmd } from "./zone-time";

/**
 * Єдине правило доступності (аудит TASK-705, F1/F3/F4/F8; §17, §18.1):
 * час запису = графік майстра в закладі ∩ години закладу (`null` = без обмеження) ∩ не вихідний день закладу;
 * `approved`-відсутність блокує майстра. Календар, mock-слоти, mock-огляд і публічний mock беруть доступність звідси.
 */

const DAY_END = 1440;

export const toMinutes = (hhmm: string): number => {
  const [h, m] = hhmm.split(":").map(Number);
  return h * 60 + m;
};

export const hhmm = (min: number): string => `${String(Math.floor(min / 60)).padStart(2, "0")}:${String(min % 60).padStart(2, "0")}`;

function merge(list: MinuteInterval[]): MinuteInterval[] {
  const sorted = list.filter((i) => i.to > i.from).sort((a, b) => a.from - b.from);
  const out: MinuteInterval[] = [];
  for (const i of sorted) {
    const last = out[out.length - 1];
    if (last && i.from <= last.to) last.to = Math.max(last.to, i.to);
    else out.push({ ...i });
  }
  return out;
}

/** Інтервали дня тижня з `working_hours` у хвилинах (відсутній день = порожньо). */
export function intervalsOf(hours: WorkingHours | null | undefined, day: Weekday): MinuteInterval[] {
  return merge((hours?.[day] ?? []).map((i) => ({ from: toMinutes(i.from), to: toMinutes(i.to) })));
}

export function intersect(a: MinuteInterval[], b: MinuteInterval[]): MinuteInterval[] {
  const out: MinuteInterval[] = [];
  for (const x of a) for (const y of b) {
    const from = Math.max(x.from, y.from);
    const to = Math.min(x.to, y.to);
    if (to > from) out.push({ from, to });
  }
  return merge(out);
}

export function subtract(a: MinuteInterval[], b: MinuteInterval[]): MinuteInterval[] {
  let rest = merge(a);
  for (const cut of merge(b)) {
    rest = rest.flatMap((i) => {
      if (cut.to <= i.from || cut.from >= i.to) return [i];
      return [
        ...(cut.from > i.from ? [{ from: i.from, to: cut.from }] : []),
        ...(cut.to < i.to ? [{ from: cut.to, to: i.to }] : []),
      ];
    });
  }
  return rest;
}

export interface PlaceRules extends ClosureRules {
  /** Графік майстра в цьому закладі. */
  masterHours: WorkingHours;
  /** Години закладу; `null`/відсутнє = обмеження немає. */
  locationHours?: WorkingHours | null;
}

export function dayAvailability(date: string, place: PlaceRules, absent = false): DayAvailability {
  const key = weekdayOf(date);
  const master = intervalsOf(place.masterHours, key);
  const location = place.locationHours == null ? null : intervalsOf(place.locationHours, key);
  const base = location ?? [{ from: 0, to: DAY_END }];
  let state: DayState = "open";
  if (closedDayInfo(date, place)) state = "location_closed";
  else if (absent) state = "absence";
  else if (location && location.length === 0) state = "location_hours_off";
  else if (master.length === 0) state = "master_off";
  return {
    date,
    state,
    master,
    location,
    free: state === "open" ? intersect(master, base) : [],
    masterOff: subtract(base, master),
    hoursOff: location == null ? [] : subtract([{ from: 0, to: DAY_END }], location),
  };
}

/** Чи вміщується `[start, start+duration]` у вільний інтервал дня (слот вважається чинним за тим самим правилом, що й backend). */
export const bookableAt = (day: DayAvailability, startMin: number, durationMin: number): boolean =>
  day.state === "open" && day.free.some((f) => f.from <= startMin && startMin + durationMin <= f.to);

/**
 * Початки слотів у хвилинах: сітка 15 хв прив'язана до початку інтервалу майстра, слот вміщується в інтервал
 * майстра й в один інтервал годин закладу, не перетинає зайняте й не починається раніше `minStart`.
 */
export function slotStarts(
  day: DayAvailability,
  durationMin: number,
  opts: { busy?: MinuteInterval[]; minStart?: number; step?: number } = {},
): number[] {
  if (day.state !== "open") return [];
  const step = opts.step ?? 15;
  const loc = day.location ?? [{ from: 0, to: DAY_END }];
  const out: number[] = [];
  for (const m of day.master) {
    for (let s = m.from; s + durationMin <= m.to; s += step) {
      if (s < (opts.minStart ?? 0)) continue;
      if (!loc.some((l) => s >= l.from && s + durationMin <= l.to)) continue;
      if ((opts.busy ?? []).some((b) => s < b.to && s + durationMin > b.from)) continue;
      out.push(s);
    }
  }
  return [...new Set(out)].sort((a, b) => a - b);
}

export const DEFAULT_GRID = { start: 9, end: 21 };

/** Діапазон сітки (години доби [start, end)): за графіком майстра, інакше за годинами закладу; розумні межі. */
export function gridRange(days: DayAvailability[]): { start: number; end: number } {
  const from = (pick: (d: DayAvailability) => MinuteInterval[]) => days.flatMap(pick);
  let spans = from((d) => d.master);
  if (spans.length === 0) spans = from((d) => d.location ?? []);
  if (spans.length === 0) return { ...DEFAULT_GRID };
  let start = Math.max(0, Math.floor(Math.min(...spans.map((s) => s.from)) / 60));
  let end = Math.min(24, Math.ceil(Math.max(...spans.map((s) => s.to)) / 60));
  if (end - start < 6) end = Math.min(24, start + 6);
  if (end - start < 6) start = Math.max(0, end - 6);
  return { start, end };
}

export interface WeekAvailability {
  availability: DayAvailability[];
  closedDays: (ClosedDay | null)[];
  gridStartHour: number;
  gridEndHour: number;
}

/** Доступність 7 днів тижня від `weekStart` (понеділок) для майстра в закладі. Відсутності сюди не входять (їх малює екран). */
export function buildWeekAvailability(weekStart: string, place: PlaceRules): WeekAvailability {
  const dates = Array.from({ length: 7 }, (_, i) => addDaysYmd(weekStart, i));
  const availability = dates.map((d) => dayAvailability(d, place));
  const g = gridRange(availability);
  return {
    availability,
    closedDays: dates.map((d) => closedDayInfo(d, place)),
    gridStartHour: g.start,
    gridEndHour: g.end,
  };
}

/** Скільки непересічних візитів тривалістю `durationMin` ще вміщується (жадібно за початками слотів) - KPI «Вільних слотів». */
export function countFreeSlots(starts: number[], durationMin: number): number {
  let end = -1;
  let n = 0;
  for (const s of starts) {
    if (s >= end) {
      n += 1;
      end = s + durationMin;
    }
  }
  return n;
}
