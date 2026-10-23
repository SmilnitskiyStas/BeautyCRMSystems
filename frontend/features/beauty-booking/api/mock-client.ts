import {
  ANY_SPECIALIST_ID,
  type Appointment,
  BookingApiError,
  type CancellationTerms,
  type Location,
  type Service,
  type Specialist,
  createAppointmentSchema,
} from "../types";
import { isClosedDate } from "../dates";
import type { BookingApi } from "./client";
import { CATALOG, DEMO_LOCATIONS, PROMO_PERCENT, STAFF, absencesSeed, appointmentsSeed, blocksOn, closuresSeed, demoToday, promoPrice } from "@/features/beauty-shared/demo-seed";
import { dayAvailability, hhmm, slotStarts, toMinutes } from "@/features/beauty-shared/availability";
import { instantToLocal, localToUtcIso } from "@/features/beauty-shared/zone-time";
import type { Weekday } from "@/features/beauty-admin/types";

/**
 * Mock публічного API (`NEXT_PUBLIC_USE_MOCK=1`): ті самі сигнатури, що й у http-клієнта. Запис адресується токеном.
 * Заклади, майстри, послуги, закриття й години беруться з ЄДИНОГО демо-набору адмінки (`beauty-shared/demo-seed.ts`),
 * а слоти рахуються за тим самим правилом: графік майстра ∩ години закладу ∩ не вихідний ∩ без відсутності (F3/F4/F8).
 */

const delay = <T>(v: T, ms = 250) => new Promise<T>((r) => setTimeout(() => r(v), ms));

const TERMS: CancellationTerms = {
  windowHours: 12,
  refundPercentInWindow: 50,
  refundPercentOutside: 100,
  deductFee: false,
  feePercent: 0,
};

/** Публічний каталог закладів: активні, без причин закриття (§17). */
const locations = (now: Date = new Date()): Location[] => {
  const closures = closuresSeed(demoToday(now));
  return DEMO_LOCATIONS.filter((l) => l.isActive !== false).map((l) => ({
    id: l.id,
    name: l.name,
    address: l.address ?? undefined,
    phone: l.phone ?? undefined,
    timezone: l.timezone ?? "UTC",
    closedWeekdays: l.closedWeekdays ?? [],
    closures: closures.filter((c) => c.locationId === l.id).map(({ dateFrom, dateTo }) => ({ dateFrom, dateTo })),
    workingHours: l.workingHours ?? null,
  }));
};

/** «Придатний» майстер закладу (§13): активний профіль і зв'язок, у графіку закладу є робочий інтервал, призначена послуга. */
function suitableMasters(locationId: string) {
  const loc = DEMO_LOCATIONS.find((l) => l.id === locationId);
  if (!loc || loc.isActive === false) return [];
  return STAFF.filter(
    (m) =>
      m.isActive &&
      m.services.length > 0 &&
      m.locations.some((l) => l.locationId === locationId && l.isActive !== false && Object.values(l.workingHours).some((d) => (d?.length ?? 0) > 0)),
  );
}

const serviceView = (id: string): Service | null => {
  const s = CATALOG.find((x) => x.id === id);
  if (!s) return null;
  return {
    id: s.id,
    name: s.name,
    category: s.category,
    durationMinutes: s.durationMinutes,
    priceOriginal: s.price,
    priceFinal: s.promo ? promoPrice(s.price) : s.price,
    ...(s.promo ? { promoLabel: `−${PROMO_PERCENT}% · ${s.promo}`, promotionId: `promo-${s.id}` } : {}),
  };
};

const STORE_KEY = "beauty-booking-mock-appointments";

interface Stored extends Appointment {
  locationId: string;
  specialistId: string;
}

function loadStore(): Record<string, Stored> {
  if (typeof window === "undefined") return {};
  try {
    return JSON.parse(window.sessionStorage.getItem(STORE_KEY) ?? "{}");
  } catch {
    return {};
  }
}

function saveStore(s: Record<string, Stored>) {
  window.sessionStorage.setItem(STORE_KEY, JSON.stringify(s));
}

/**
 * Слоти за правилами графік ∩ години ∩ вихідні ∩ відсутності; зайняте - записи демо-набору і цього сеансу.
 * Для «будь-якого майстра» однаковий час різних майстрів дедуплікується (перший придатний).
 */
export function mockSlots(q: { locationId: string; specialistId: string; serviceId: string; date: string }, now: Date = new Date()) {
  const loc = locations(now).find((l) => l.id === q.locationId);
  const svc = CATALOG.find((s) => s.id === q.serviceId);
  if (!loc || !svc) return [];
  const today = demoToday(now);
  const absences = absencesSeed(today);
  const seed = appointmentsSeed(today, now).filter((a) => a.status !== "cancelled");
  const stored = Object.values(loadStore()).filter((a) => a.status !== "cancelled");
  const nowLocal = instantToLocal(now.toISOString(), loc.timezone);
  const minStart = q.date < nowLocal.slice(0, 10) ? 1440 : q.date === nowLocal.slice(0, 10) ? toMinutes(nowLocal.slice(11, 16)) : 0;
  const out: { specialistId: string; start: number }[] = [];
  for (const m of suitableMasters(q.locationId)) {
    if (q.specialistId !== ANY_SPECIALIST_ID && m.id !== q.specialistId) continue;
    if (!m.services.some((s) => s.id === q.serviceId)) continue;
    const link = m.locations.find((l) => l.locationId === q.locationId && l.isActive !== false)!;
    const day = dayAvailability(
      q.date,
      { masterHours: link.workingHours, locationHours: loc.workingHours, closedWeekdays: loc.closedWeekdays as Weekday[], closures: loc.closures },
      blocksOn(absences, m.id, q.date),
    );
    const busy = [
      ...seed
        .filter((a) => a.specialistId === m.id && a.startsAt.slice(0, 10) === q.date)
        .map((a) => ({ from: toMinutes(a.startsAt.slice(11, 16)), to: toMinutes(a.startsAt.slice(11, 16)) + a.durationMinutes })),
      ...stored
        .filter((a) => a.specialistId === m.id)
        .map((a) => ({ local: instantToLocal(a.startsAt, loc.timezone), dur: a.durationMinutes }))
        .filter((a) => a.local.slice(0, 10) === q.date)
        .map((a) => ({ from: toMinutes(a.local.slice(11, 16)), to: toMinutes(a.local.slice(11, 16)) + a.dur })),
    ];
    for (const start of slotStarts(day, svc.durationMinutes, { busy, minStart })) out.push({ specialistId: m.id, start });
  }
  const seen = new Set<number>();
  return out
    .sort((a, b) => a.start - b.start)
    .filter((s) => (seen.has(s.start) ? false : (seen.add(s.start), true)))
    .map((s) => ({ ...s, label: hhmm(s.start), startsAt: localToUtcIso(`${q.date}T${hhmm(s.start)}`, loc.timezone) }));
}

const publicView = (s: Stored): Appointment => {
  const a: Partial<Stored> = { ...s };
  delete a.locationId;
  delete a.specialistId;
  return a as Appointment;
};

export const mockBookingApi: BookingApi = {
  getLocations: () => delay(locations()),

  getSpecialists: (_t, locationId) =>
    delay<Specialist[]>([
      {
        id: ANY_SPECIALIST_ID,
        name: "Будь-який спеціаліст",
        role: "Підберемо найближчий вільний час",
        nextFree: "Швидший запис",
      },
      ...suitableMasters(locationId).map((m) => ({ id: m.id, name: m.name, role: m.position ?? "" })),
    ]),

  getServices: (_t, locationId, specialistId) => {
    const masters = suitableMasters(locationId).filter((m) => specialistId === ANY_SPECIALIST_ID || m.id === specialistId);
    const ids = [...new Set(masters.flatMap((m) => m.services.map((s) => s.id)))];
    return delay(ids.map(serviceView).filter((s): s is Service => s !== null));
  },

  async getSlots(_t, q) {
    return delay(mockSlots(q).map((s) => ({ label: s.label, startsAt: s.startsAt, specialistId: s.specialistId, cancellation: TERMS })));
  },

  async createAppointment(_t, req) {
    const parsed = createAppointmentSchema.safeParse(req);
    if (!parsed.success) throw new BookingApiError(422, "invalid_request");
    const r = parsed.data;
    if (r.website) throw new BookingApiError(422, "invalid_request");
    const location = locations().find((l) => l.id === r.locationId);
    const service = serviceView(r.serviceId);
    if (!location || !service) throw new BookingApiError(422, "invalid_request");
    const local = instantToLocal(r.startsAt, location.timezone);
    // Публічний API віддає узагальнену відповідь без причини (закритий день, години, відсутність, зайнято - §17/§18).
    if (isClosedDate(local.slice(0, 10), location)) throw new BookingApiError(409, "slot_unavailable");
    const start = toMinutes(local.slice(11, 16));
    const match = mockSlots({ locationId: r.locationId, specialistId: r.specialistId, serviceId: r.serviceId, date: local.slice(0, 10) }).find(
      (s) => s.start === start,
    );
    const specialist = match ? STAFF.find((m) => m.id === match.specialistId) : undefined;
    if (!specialist) throw new BookingApiError(409, "slot_unavailable");

    const store = loadStore();
    const token = `mock${crypto.randomUUID().replace(/-/g, "")}`;
    const durationMs = service.durationMinutes * 60_000;
    store[token] = {
      locationId: location.id,
      specialistId: specialist.id,
      locationName: location.name,
      specialistName: specialist.name,
      serviceName: service.name,
      startsAt: r.startsAt,
      endsAt: new Date(Date.parse(r.startsAt) + durationMs).toISOString(),
      durationMinutes: service.durationMinutes,
      status: "confirmed",
      priceOriginal: service.priceOriginal,
      priceFinal: service.priceFinal,
      promotionId: service.promotionId,
      reminderOption: r.reminder,
      paymentMethod: r.paymentMethod,
      paymentStatus: r.paymentMethod === "card" ? "paid" : undefined,
      cancellation: TERMS,
    };
    saveStore(store);
    return delay({ token, appointment: publicView(store[token]) }, 400);
  },

  async getAppointment(_t, token) {
    const a = loadStore()[token];
    if (!a) throw new BookingApiError(404, "not_found");
    return delay(publicView(a));
  },

  async cancelAppointment(_t, token) {
    const store = loadStore();
    const a = store[token];
    if (!a) throw new BookingApiError(404, "not_found");
    if (a.status === "cancelled") throw new BookingApiError(409, "already_cancelled");
    const inWindow = Date.parse(a.startsAt) - Date.now() <= TERMS.windowHours * 3_600_000;
    const refundPercent = inWindow ? TERMS.refundPercentInWindow : TERMS.refundPercentOutside;
    const refundAmount = a.paymentStatus === "paid" ? Math.floor((a.priceFinal * refundPercent) / 100) : 0;
    store[token] = { ...a, status: "cancelled", paymentStatus: refundAmount > 0 ? "refunded" : a.paymentStatus };
    saveStore(store);
    return delay({ appointment: publicView(store[token]), refundAmount, refundPercent, feePercent: 0 });
  },
};
