import type { AppointmentRecord, OrderRecord } from "@/storage/tokenStore";

/** Статуси запису, після яких він уже не «попереду» (знімок з індексу; без статусу вважаємо активним). */
const APPT_DONE = new Set(["cancelled", "completed", "no_show"]);
/** Замовлення, що ще чекає дії клієнта або видачі. */
const ORDER_ACTIVE = new Set(["pending_payment", "paid"]);

/** Найближчий майбутній активний запис або null. `startsAt` - ISO зі зсувом закладу. */
export function nextAppointment(items: AppointmentRecord[], now: Date = new Date()): AppointmentRecord | null {
  let best: AppointmentRecord | null = null;
  let bestTs = Infinity;
  for (const r of items) {
    if (r.status && APPT_DONE.has(r.status)) continue;
    const ts = Date.parse(r.startsAt);
    if (Number.isNaN(ts) || ts < now.getTime()) continue;
    if (ts < bestTs) {
      best = r;
      bestTs = ts;
    }
  }
  return best;
}

export const activeOrdersCount = (items: OrderRecord[]): number =>
  items.filter((o) => !o.status || ORDER_ACTIVE.has(o.status)).length;
