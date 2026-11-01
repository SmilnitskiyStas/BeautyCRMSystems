import { addDaysYmd } from "../../beauty-shared/zone-time";

/**
 * Діапазони аналітики (§19.1). Пресети рахуються на клієнті в еталонній зоні мережі; API приймає лише `from`/`to`
 * у форматі `yyyy-MM-dd`. Усі функції чисті й працюють із календарними рядками (без часу й зони).
 */

export type RangePreset = "today" | "7d" | "30d" | "month" | "prev_month" | "quarter" | "year" | "custom";

export const DEFAULT_PRESET: RangePreset = "30d";
export const MAX_RANGE_DAYS = 366;

export const PRESETS: { id: RangePreset; label: string }[] = [
  { id: "today", label: "Сьогодні" },
  { id: "7d", label: "7 днів" },
  { id: "30d", label: "30 днів" },
  { id: "month", label: "Поточний місяць" },
  { id: "prev_month", label: "Минулий місяць" },
  { id: "quarter", label: "Квартал" },
  { id: "year", label: "Рік" },
  { id: "custom", label: "Власний від–до" },
];

export interface DateRange {
  from: string;
  to: string;
}

export interface RangeState {
  preset: RangePreset;
  /** Лише для `custom`. */
  custom: DateRange | null;
  compare: boolean;
}

const YMD = /^\d{4}-\d{2}-\d{2}$/;
const utc = (ymd: string) => Date.UTC(+ymd.slice(0, 4), +ymd.slice(5, 7) - 1, +ymd.slice(8, 10));

/** Існуюча календарна дата `yyyy-MM-dd` (не `2026-02-30`). */
export function isValidYmd(s: string): boolean {
  if (!YMD.test(s)) return false;
  const d = new Date(utc(s));
  return d.toISOString().slice(0, 10) === s;
}

/** Кількість днів у діапазоні включно. */
export const daysInRange = (r: DateRange): number => Math.round((utc(r.to) - utc(r.from)) / 86_400_000) + 1;

/**
 * Дзеркало серверної валідації (§19.1): формат/існування дати, рік 2000..2100, `from <= to`, до 366 днів.
 * Повертає зрозуміле повідомлення або `null`.
 */
export function validateRange(from: string, to: string): string | null {
  if (!from || !to) return "Вкажіть початок і кінець періоду.";
  if (!isValidYmd(from) || !isValidYmd(to)) return "Вкажіть коректні дати.";
  const years = [+from.slice(0, 4), +to.slice(0, 4)];
  if (years.some((y) => y < 2000 || y > 2100)) return "Рік має бути від 2000 до 2100.";
  if (from > to) return "Початок періоду не може бути пізніше за кінець.";
  const n = daysInRange({ from, to });
  if (n > MAX_RANGE_DAYS) return `Період не може бути довшим за ${MAX_RANGE_DAYS} дні (обрано ${n}).`;
  return null;
}

/** Пресет у зоні, де `today` = сьогоднішня дата в еталонній зоні (§19.1). */
export function presetRange(preset: Exclude<RangePreset, "custom">, today: string): DateRange {
  const monthStart = `${today.slice(0, 8)}01`;
  switch (preset) {
    case "today":
      return { from: today, to: today };
    case "7d":
      return { from: addDaysYmd(today, -6), to: today };
    case "30d":
      return { from: addDaysYmd(today, -29), to: today };
    case "month":
      return { from: monthStart, to: today };
    case "prev_month": {
      const last = addDaysYmd(monthStart, -1);
      return { from: `${last.slice(0, 8)}01`, to: last };
    }
    case "quarter": {
      const startMonth = Math.floor((+today.slice(5, 7) - 1) / 3) * 3 + 1;
      return { from: `${today.slice(0, 5)}${String(startMonth).padStart(2, "0")}-01`, to: today };
    }
    case "year":
      return { from: `${today.slice(0, 4)}-01-01`, to: today };
  }
}

/** Фактичний діапазон за станом або `null` для неприпустимого власного діапазону. */
export function resolveRange(state: RangeState, today: string): DateRange | null {
  if (state.preset !== "custom") return presetRange(state.preset, today);
  if (!state.custom || validateRange(state.custom.from, state.custom.to)) return null;
  return state.custom;
}

/** Попередній період тієї ж тривалості (§19.3): `prevTo = from − 1`, `prevFrom = from − n`. */
export function previousRange(r: DateRange): DateRange {
  const n = daysInRange(r);
  return { from: addDaysYmd(r.from, -n), to: addDaysYmd(r.from, -1) };
}

// ---------- URL: ?range=today|7d|30d|month|prev_month|quarter|year|custom&from&to&compare=1 ----------

export function parseRangeParams(sp: { get(name: string): string | null }): RangeState {
  const raw = sp.get("range");
  const preset = (PRESETS.find((p) => p.id === raw)?.id ?? DEFAULT_PRESET) as RangePreset;
  const from = sp.get("from");
  const to = sp.get("to");
  return {
    preset,
    custom: preset === "custom" && from && to ? { from, to } : null,
    compare: sp.get("compare") === "1",
  };
}

/** Рядок query без початкового `?` (порожній для стану за замовчуванням). */
export function serializeRangeParams(state: RangeState): string {
  const p = new URLSearchParams();
  if (state.preset !== DEFAULT_PRESET) p.set("range", state.preset);
  if (state.preset === "custom" && state.custom) {
    p.set("from", state.custom.from);
    p.set("to", state.custom.to);
  }
  if (state.compare) p.set("compare", "1");
  return p.toString();
}

// ---------- підписи ----------

const longDate = (ymd: string) =>
  new Date(utc(ymd))
    .toLocaleDateString("uk-UA", { day: "numeric", month: "long", year: "numeric", timeZone: "UTC" })
    .replace(/\s?р\.$/, "");

export function pluralDays(n: number): string {
  const word = n % 10 === 1 && n % 100 !== 11 ? "день" : n % 10 >= 2 && n % 10 <= 4 && (n % 100 < 12 || n % 100 > 14) ? "дні" : "днів";
  return `${n} ${word}`;
}

/** «25 вересня 2026 – 24 жовтня 2026 · 30 днів». */
export function rangeLabel(r: DateRange): string {
  const days = pluralDays(daysInRange(r));
  return r.from === r.to ? `${longDate(r.from)} · ${days}` : `${longDate(r.from)} – ${longDate(r.to)} · ${days}`;
}

export function rangeLabelShort(r: DateRange): string {
  return r.from === r.to ? longDate(r.from) : `${longDate(r.from)} – ${longDate(r.to)}`;
}

/**
 * Еталонна зона мережі (§19.1): зона першого АКТИВНОГО закладу за назвою, далі за id; без активних - UTC.
 * Те саме правило, що в `OverviewService`.
 */
export function referenceZone(locations: { id: string; name: string; timezone?: string; isActive?: boolean }[]): string {
  const active = locations
    .filter((l) => l.isActive !== false)
    .sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : a.id < b.id ? -1 : a.id > b.id ? 1 : 0));
  return active[0]?.timezone || "UTC";
}
