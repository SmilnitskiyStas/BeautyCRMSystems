/** Форматування сум, тривалості й часу для UI адмінки. */

export function money(n: number): string {
  return `${n.toLocaleString("uk-UA").replace(/ | /g, " ")} ₴`;
}

export function durationLabel(minutes: number): string {
  const h = Math.floor(minutes / 60);
  const m = minutes % 60;
  return [h ? `${h} г` : "", m ? `${m} хв` : ""].filter(Boolean).join(" ");
}

export function clock(totalMinutes: number): string {
  const h = Math.floor(totalMinutes / 60);
  const m = totalMinutes % 60;
  return `${h}:${String(m).padStart(2, "0")}`;
}

/** Хвилини від початку доби з `YYYY-MM-DDTHH:mm`. */
export function minutesOfDay(startsAt: string): number {
  const [h, m] = startsAt.slice(11, 16).split(":").map(Number);
  return h * 60 + m;
}

/** Індекс дня (0 = пн) відносно понеділка тижня `weekStart` (`YYYY-MM-DD`). */
export function dayIndex(startsAt: string, weekStart: string): number {
  const day = Date.UTC(+startsAt.slice(0, 4), +startsAt.slice(5, 7) - 1, +startsAt.slice(8, 10));
  const start = Date.UTC(+weekStart.slice(0, 4), +weekStart.slice(5, 7) - 1, +weekStart.slice(8, 10));
  return Math.round((day - start) / 86_400_000);
}

export function initials(name: string): string {
  return name
    .split(" ")
    .map((w) => w[0])
    .join("")
    .slice(0, 2);
}
