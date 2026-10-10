import type { TimeInterval, Weekday, WorkingHours } from "./types";

/** Формат `working_hours` з контракту §9: `{"mon":[{"from":"09:00","to":"18:00"}], ...}`; відсутній день = вихідний. */
export const WEEKDAYS: { key: Weekday; label: string; short: string }[] = [
  { key: "mon", label: "Понеділок", short: "Пн" },
  { key: "tue", label: "Вівторок", short: "Вт" },
  { key: "wed", label: "Середа", short: "Ср" },
  { key: "thu", label: "Четвер", short: "Чт" },
  { key: "fri", label: "Пʼятниця", short: "Пт" },
  { key: "sat", label: "Субота", short: "Сб" },
  { key: "sun", label: "Неділя", short: "Нд" },
];

export const DEFAULT_INTERVAL: TimeInterval = { from: "09:00", to: "18:00" };
export const MAX_INTERVALS_PER_DAY = 4;

/**
 * Шаблон нового графіка: усі 7 днів 09:00-18:00 (§17). Вихідні задає заклад (closedWeekdays/closures), а не відсутність
 * ключа `sun` у графіку; незручні дні майстер знімає вручну.
 */
export const defaultHours = (): WorkingHours =>
  Object.fromEntries(WEEKDAYS.map((d) => [d.key, [{ ...DEFAULT_INTERVAL }]])) as WorkingHours;

const TIME = /^([01]\d|2[0-3]):[0-5]\d$/;

/** Помилка для одного дня або `null`. */
export function validateDay(intervals: TimeInterval[] | undefined): string | null {
  if (!intervals || intervals.length === 0) return null;
  if (intervals.length > MAX_INTERVALS_PER_DAY) return `Не більше ${MAX_INTERVALS_PER_DAY} інтервалів на день.`;
  for (const i of intervals) {
    if (!TIME.test(i.from) || !TIME.test(i.to)) return "Вкажіть початок і кінець у форматі ГГ:ХХ.";
    if (i.from >= i.to) return "Початок має бути раніше за кінець.";
  }
  const sorted = [...intervals].sort((a, b) => a.from.localeCompare(b.from));
  for (let k = 1; k < sorted.length; k++) {
    if (sorted[k].from < sorted[k - 1].to) return "Інтервали не повинні перетинатися.";
  }
  return null;
}

export function validateHours(h: WorkingHours): Partial<Record<Weekday, string>> {
  const errors: Partial<Record<Weekday, string>> = {};
  for (const d of WEEKDAYS) {
    const e = validateDay(h[d.key]);
    if (e) errors[d.key] = e;
  }
  return errors;
}

/** Прибирає порожні дні й сортує інтервали — те, що йде в API. */
export function normalizeHours(h: WorkingHours): WorkingHours {
  const out: WorkingHours = {};
  for (const d of WEEKDAYS) {
    const list = h[d.key];
    if (list && list.length > 0) out[d.key] = [...list].sort((a, b) => a.from.localeCompare(b.from)).map(({ from, to }) => ({ from, to }));
  }
  return out;
}

export const hasWorkingDays = (h: WorkingHours) => WEEKDAYS.some((d) => (h[d.key]?.length ?? 0) > 0);

/** «Пн–Пт 09:00–18:00; Сб 10:00–15:00» або «Немає робочих днів». */
export function summarizeHours(h: WorkingHours): string {
  const parts = WEEKDAYS.filter((d) => (h[d.key]?.length ?? 0) > 0).map(
    (d) => `${d.short} ${h[d.key]!.map((i) => `${i.from}–${i.to}`).join(", ")}`,
  );
  return parts.length ? parts.join("; ") : "Немає робочих днів";
}
