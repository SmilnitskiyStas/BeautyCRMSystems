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

const UTC = (ymd: string) => Date.UTC(+ymd.slice(0, 4), +ymd.slice(5, 7) - 1, +ymd.slice(8, 10));

/** `YYYY-MM-DD` у локальній зоні браузера. */
export function todayIso(now = new Date()): string {
  const p = (n: number) => String(n).padStart(2, "0");
  return `${now.getFullYear()}-${p(now.getMonth() + 1)}-${p(now.getDate())}`;
}

export function addDaysIso(ymd: string, n: number): string {
  return new Date(UTC(ymd) + n * 86_400_000).toISOString().slice(0, 10);
}

/** «5 жовт.» з `YYYY-MM-DD`. */
export function shortDate(ymd: string): string {
  return new Date(UTC(ymd)).toLocaleDateString("uk-UA", { day: "numeric", month: "short", timeZone: "UTC" });
}

export function dateRangeLabel(from: string, to: string): string {
  return from === to ? shortDate(from) : `${shortDate(from)} – ${shortDate(to)}`;
}

/** «5 жовт., 10:30» з `YYYY-MM-DDTHH:mm`. */
const HAS_OFFSET = /(Z|[+-]\d{2}:\d{2})$/;

/**
 * "5 жовт., 10:30". Рядок з Z/зсувом (backend часто віддає UTC) НЕ обрізаємо: розбираємо як момент часу й
 * форматуємо в зоні закладу (`timeZone`), а без неї - у зоні браузера. Локальний `YYYY-MM-DDTHH:mm` - як є.
 */
export function dateTimeLabel(startsAt: string, timeZone?: string): string {
  if (HAS_OFFSET.test(startsAt)) {
    const d = new Date(startsAt);
    if (!Number.isNaN(d.getTime())) {
      const parts = (tz?: string) => {
        const f = new Intl.DateTimeFormat("en-CA", {
          timeZone: tz,
          year: "numeric",
          month: "2-digit",
          day: "2-digit",
          hour: "2-digit",
          minute: "2-digit",
          hourCycle: "h23",
        });
        return Object.fromEntries(f.formatToParts(d).map((p) => [p.type, p.value])) as Record<string, string>;
      };
      let p: Record<string, string>;
      try {
        p = parts(timeZone);
      } catch {
        p = parts(undefined);
      }
      return `${shortDate(`${p.year}-${p.month}-${p.day}`)}, ${p.hour}:${p.minute}`;
    }
  }
  return `${shortDate(startsAt.slice(0, 10))}, ${startsAt.slice(11, 16)}`;
}
