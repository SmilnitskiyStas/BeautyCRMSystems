export interface DateOption {
  /** `YYYY-MM-DD` - локальна дата закладу (саме її чекає `GET /slots?date=`). */
  iso: string;
  label: string;
  weekday: string;
}

/** Календарна дата `YYYY-MM-DD` у часовій зоні закладу; при невалідній зоні - у зоні браузера. */
export function todayIn(timezone: string, now: Date = new Date()): string {
  try {
    return new Intl.DateTimeFormat("en-CA", { timeZone: timezone, year: "numeric", month: "2-digit", day: "2-digit" }).format(now);
  } catch {
    const p = (n: number) => String(n).padStart(2, "0");
    return `${now.getFullYear()}-${p(now.getMonth() + 1)}-${p(now.getDate())}`;
  }
}

/** Наступні `days` днів (включно із сьогодні) за календарем закладу. */
export function dateOptions(timezone: string, days = 7, now: Date = new Date()): DateOption[] {
  const [y, m, d] = todayIn(timezone, now).split("-").map(Number);
  return Array.from({ length: days }, (_, i) => {
    const day = new Date(Date.UTC(y, m - 1, d + i));
    const iso = day.toISOString().slice(0, 10);
    const weekday = day.toLocaleDateString("uk-UA", { weekday: "short", timeZone: "UTC" });
    const num = day.toLocaleDateString("uk-UA", { day: "numeric", month: "short", timeZone: "UTC" });
    return { iso, weekday, label: i === 0 ? "Сьогодні" : i === 1 ? "Завтра" : num };
  });
}

/** Підпис дати для заголовка слотів: «понеділок, 12 жовтня». */
export const longDate = (iso: string) =>
  new Date(`${iso}T00:00:00Z`).toLocaleDateString("uk-UA", { weekday: "long", day: "numeric", month: "long", timeZone: "UTC" });
