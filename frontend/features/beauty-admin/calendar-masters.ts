import type { Appointment, CalendarMaster } from "./types";

export interface RosterEntry {
  id: string;
  name: string;
  isActive: boolean;
  locationNames: string[];
}

/** Записи, які ще потрібно виконати/перенести (не завершені й не скасовані). */
const needsAction = (a: Pick<Appointment, "status">) => a.status === "pending" || a.status === "confirmed";

/**
 * Селектор майстрів календаря (§16): активні завжди; НЕАКТИВНІ - лише якщо мають нескасовані записи у видимому
 * періоді (їх треба перенести). `liveAppointments` - записи без скасованих (дефолт API).
 * `onlyId` - specialist бачить лише себе.
 */
export function mastersForCalendar(
  roster: RosterEntry[],
  liveAppointments: Pick<Appointment, "specialistId" | "status">[],
  onlyId?: string | null,
): CalendarMaster[] {
  const live = liveAppointments.filter((a) => a.status !== "cancelled");
  return roster
    .filter((r) => (onlyId ? r.id === onlyId : true))
    .filter((r) => r.isActive || live.some((a) => a.specialistId === r.id))
    .map((r) => ({
      id: r.id,
      name: r.name,
      locationName: r.locationNames.join(", "),
      isActive: r.isActive,
      toMoveCount: r.isActive ? 0 : live.filter((a) => a.specialistId === r.id && needsAction(a)).length,
    }));
}
