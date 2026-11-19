import type { DayAvailability, DayStatus } from "@beauty/booking-core";

/** Стан клітинки: статус з API або `outside` (поза завантаженим горизонтом / не цього місяця). */
export type CellState = DayStatus | "outside";

export interface CalendarCell {
  iso: string;
  day: number;
  inMonth: boolean;
  state: CellState;
  freeSlots: number;
  selectable: boolean;
}

export const cellSelectable = (state: CellState): boolean => state === "available";

const pad = (n: number) => String(n).padStart(2, "0");
const isoOf = (d: Date) => d.toISOString().slice(0, 10);

/** Місяць (`YYYY-MM`) із ISO-дати. */
export const monthOf = (iso: string): string => iso.slice(0, 7);

export function shiftMonth(ym: string, delta: number): string {
  const [y, m] = ym.split("-").map(Number);
  const d = new Date(Date.UTC(y, m - 1 + delta, 1));
  return `${d.getUTCFullYear()}-${pad(d.getUTCMonth() + 1)}`;
}

/** Межі навігації: перший і останній місяці завантаженого горизонту. */
export function monthBounds(days: DayAvailability[]): { first: string; last: string } | null {
  if (days.length === 0) return null;
  const sorted = days.map((d) => d.date).sort();
  return { first: monthOf(sorted[0]), last: monthOf(sorted[sorted.length - 1]) };
}

/**
 * Сітка місяця, пн-нд, повні тижні (6 рядків не примусово). Дні поза даними й сусідніх місяців - `outside`, не вибираються.
 * `ym` = `YYYY-MM`.
 */
export function buildMonthGrid(ym: string, days: DayAvailability[]): CalendarCell[][] {
  const byDate = new Map(days.map((d) => [d.date, d]));
  const [y, m] = ym.split("-").map(Number);
  const first = new Date(Date.UTC(y, m - 1, 1));
  const lead = (first.getUTCDay() + 6) % 7; // пн = 0
  const total = new Date(Date.UTC(y, m, 0)).getUTCDate();
  const rows = Math.ceil((lead + total) / 7);
  const weeks: CalendarCell[][] = [];
  for (let r = 0; r < rows; r++) {
    const week: CalendarCell[] = [];
    for (let c = 0; c < 7; c++) {
      const date = new Date(Date.UTC(y, m - 1, 1 - lead + r * 7 + c));
      const iso = isoOf(date);
      const inMonth = monthOf(iso) === ym;
      const found = inMonth ? byDate.get(iso) : undefined;
      const state: CellState = found ? found.status : "outside";
      week.push({
        iso,
        day: date.getUTCDate(),
        inMonth,
        state,
        freeSlots: found?.freeSlots ?? 0,
        selectable: cellSelectable(state),
      });
    }
    weeks.push(week);
  }
  return weeks;
}

/** Перша доступна дата або null (авто-вибір ніколи не стає на full/closed/past). */
export function firstAvailable(days: DayAvailability[]): string | null {
  return days.filter((d) => d.status === "available").map((d) => d.date).sort()[0] ?? null;
}
