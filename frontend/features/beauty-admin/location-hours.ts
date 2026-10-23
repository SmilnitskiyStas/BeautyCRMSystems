import { localDateIn, weekdayOf } from "./closed-days";
import type { BeautyLocation, StaffMember, Weekday, WorkingHours } from "./types";
import { summarizeHours, WEEKDAYS } from "./working-hours";

/** Години роботи закладу (§18.1): `null`/відсутнє = не задано (обмеження немає). */
export const hasLocationHours = (l: Pick<BeautyLocation, "workingHours">) => l.workingHours != null;

/** «Пн 09:00–18:00; Вт …» або «Години не задано». Порожній об'єкт - заклад не працює жодного дня. */
export function locationHoursLabel(l: Pick<BeautyLocation, "workingHours">): string {
  return l.workingHours == null ? "Години не задано" : summarizeHours(l.workingHours);
}

/** «Вихідні: Нд, Пн» або «Без вихідних». */
export function closedWeekdaysLabel(days: Weekday[] | undefined): string {
  const list = WEEKDAYS.filter((d) => (days ?? []).includes(d.key));
  return list.length ? `Вихідні: ${list.map((d) => d.short).join(", ")}` : "Без вихідних";
}

/** Посилання на карту за текстовою адресою (§18.3); `null`, якщо адреса порожня. */
export function mapUrl(address: string | null | undefined): string | null {
  const a = (address ?? "").trim();
  return a ? `https://www.google.com/maps/search/?api=1&query=${encodeURIComponent(a)}` : null;
}

/** Спеціалісти закладу (§18.2): без окремого ендпоінта - з `GET /specialists` за `locations[]`. Активні перші. */
export function staffOfLocation(staff: StaffMember[], locationId: string) {
  return staff
    .flatMap((m) => {
      const loc = m.locations.find((l) => l.locationId === locationId);
      return loc ? [{ member: m, workingHours: loc.workingHours }] : [];
    })
    .sort(
      (a, b) =>
        Number(b.member.isActive) - Number(a.member.isActive) || a.member.name.localeCompare(b.member.name, "uk"),
    );
}

export const countActiveStaff = (staff: StaffMember[], locationId: string) =>
  staffOfLocation(staff, locationId).filter((s) => s.member.isActive).length;

/** Локальний час `HH:mm` у зоні закладу (ISO зі зсувом -> перерахунок; локальний `YYYY-MM-DDTHH:mm` - як є). */
export function localTimeIn(iso: string, timeZone?: string): string {
  if (/(Z|[+-]\d{2}:\d{2})$/.test(iso)) {
    const d = new Date(iso);
    if (!Number.isNaN(d.getTime())) {
      try {
        const parts = new Intl.DateTimeFormat("en-GB", { timeZone, hour: "2-digit", minute: "2-digit", hourCycle: "h23" }).format(d);
        return parts.slice(0, 5);
      } catch {
        /* невалідна зона: час із рядка */
      }
    }
  }
  return iso.slice(11, 16);
}

/** Чи потрапляє початок запису в години закладу (день без інтервалів = не працює). */
export function isInsideLocationHours(startsAt: string, hours: WorkingHours, timeZone?: string): boolean {
  const day = hours[weekdayOf(localDateIn(startsAt, timeZone))] ?? [];
  const t = localTimeIn(startsAt, timeZone);
  return day.some((i) => i.from <= t && t < i.to);
}

/** Записи, що опиняються поза годинами (для mock і тестів; backend рахує сам). */
export function outsideHours<T extends { startsAt: string }>(items: T[], hours: WorkingHours | null, timeZone?: string): T[] {
  if (hours == null) return [];
  return items.filter((a) => !isInsideLocationHours(a.startsAt, hours, timeZone));
}
