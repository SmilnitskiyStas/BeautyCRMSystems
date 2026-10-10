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

/** Mock публічного API (`NEXT_PUBLIC_USE_MOCK=1`): ті самі сигнатури, що й у http-клієнта. Запис адресується токеном. */

const delay = <T>(v: T, ms = 250) => new Promise<T>((r) => setTimeout(() => r(v), ms));

const TERMS: CancellationTerms = {
  windowHours: 12,
  refundPercentInWindow: 50,
  refundPercentOutside: 100,
  deductFee: false,
  feePercent: 0,
};

const TZ = "Europe/Kyiv";

/** Закриття через `from`..`to` днів від сьогодні (календар Києва) для демо. */
function soon(from: number, to: number): { dateFrom: string; dateTo: string } {
  const [y, m, d] = new Intl.DateTimeFormat("en-CA", { timeZone: TZ, year: "numeric", month: "2-digit", day: "2-digit" })
    .format(new Date())
    .split("-")
    .map(Number);
  const iso = (n: number) => new Date(Date.UTC(y, m - 1, d + n)).toISOString().slice(0, 10);
  return { dateFrom: iso(from), dateTo: iso(to) };
}

const locations: Location[] = [
  // Центр: неділя вихідна; Поділ: закриття на 3 дні попереду (демо §17).
  { id: "c", name: "Beauty Lab · Центр", address: "вул. Хрещатик, 22", phone: "+380441112233", timezone: TZ, closedWeekdays: ["sun"], closures: [] },
  { id: "p", name: "Beauty Lab · Поділ", address: "вул. Сагайдачного, 10", timezone: TZ, closedWeekdays: [], closures: [soon(2, 4)] },
  { id: "k", name: "Beauty Lab · Печерськ", address: "вул. Лаврська, 7", phone: "+380445556677", timezone: TZ },
];

const specialists: Record<string, Specialist[]> = {
  c: [
    { id: "m", name: "Марина Бойко", role: "Майстер манікюру", nextFree: "Найближчий запис: 15:30" },
    { id: "a", name: "Анна Шевчук", role: "Стиліст-колорист", nextFree: "Найближчий запис: 17:00" },
    { id: "o", name: "Оксана Лис", role: "Косметолог", nextFree: "Найближчий запис: 16:00" },
  ],
  p: [
    { id: "d", name: "Дарина Коваль", role: "Майстер манікюру", nextFree: "Найближчий запис: 14:00" },
    { id: "i", name: "Ірина Мельник", role: "Барбер-стиліст", nextFree: "Найближчий запис: 15:00" },
  ],
  k: [
    { id: "v", name: "Вікторія Руда", role: "Косметолог", nextFree: "Найближчий запис: 16:30" },
    { id: "n", name: "Наталя Гук", role: "Майстер манікюру", nextFree: "Найближчий запис: 18:00" },
  ],
};

const services: Service[] = [
  { id: "mn", name: "Манікюр + гель-лак", durationMinutes: 90, priceOriginal: 900, priceFinal: 700, promoLabel: "−20% до кінця тижня", promotionId: "promo-week" },
  { id: "hc", name: "Жіноча стрижка", durationMinutes: 60, priceOriginal: 650, priceFinal: 650 },
  { id: "cl", name: "Чистка обличчя", durationMinutes: 75, priceOriginal: 1400, priceFinal: 1100, promoLabel: "−20% для нових клієнтів", promotionId: "promo-new" },
];

const SLOT_TIMES = ["11:30", "14:00", "17:00", "18:30"];
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

function slotIso(date: string, hhmm: string): string {
  const [h, m] = hhmm.split(":").map(Number);
  const d = new Date(`${date}T00:00:00`);
  d.setHours(h, m, 0, 0);
  return d.toISOString();
}

const publicView = (s: Stored): Appointment => {
  const a: Partial<Stored> = { ...s };
  delete a.locationId;
  delete a.specialistId;
  return a as Appointment;
};

export const mockBookingApi: BookingApi = {
  getLocations: () => delay(locations),

  getSpecialists: (_t, locationId) =>
    delay([
      {
        id: ANY_SPECIALIST_ID,
        name: "Будь-який спеціаліст",
        role: "Підберемо найближчий вільний час",
        nextFree: "Швидший запис",
      },
      ...(specialists[locationId] ?? []),
    ]),

  getServices: () => delay(services),

  async getSlots(_t, { locationId, specialistId, serviceId, date }) {
    const taken = Object.values(loadStore())
      .filter(
        (a) =>
          a.locationId === locationId &&
          a.status !== "cancelled" &&
          a.serviceName === services.find((s) => s.id === serviceId)?.name &&
          (specialistId === ANY_SPECIALIST_ID || a.specialistId === specialistId),
      )
      .map((a) => a.startsAt);
    const now = Date.now();
    // Закритий день перекриває графіки (§17): слотів немає.
    if (isClosedDate(date, locations.find((l) => l.id === locationId))) return delay([]);
    const slots = SLOT_TIMES.map((label) => ({ label, startsAt: slotIso(date, label), specialistId, cancellation: TERMS }))
      .filter((s) => new Date(s.startsAt).getTime() > now)
      .filter((s) => !taken.includes(s.startsAt));
    return delay(slots);
  },

  async createAppointment(_t, req) {
    const parsed = createAppointmentSchema.safeParse(req);
    if (!parsed.success) throw new BookingApiError(422, "invalid_request");
    const r = parsed.data;
    if (r.website) throw new BookingApiError(422, "invalid_request");
    const location = locations.find((l) => l.id === r.locationId);
    const service = services.find((s) => s.id === r.serviceId);
    if (!location || !service) throw new BookingApiError(422, "invalid_request");
    // Публічний API віддає узагальнену відповідь без причини закриття (§17).
    if (isClosedDate(r.startsAt.slice(0, 10), location)) throw new BookingApiError(409, "slot_unavailable");

    const pool = specialists[r.locationId] ?? [];
    const store = loadStore();
    const busy = (specId: string) =>
      Object.values(store).some((a) => a.specialistId === specId && a.startsAt === r.startsAt && a.status !== "cancelled");
    const specialist =
      r.specialistId === ANY_SPECIALIST_ID
        ? pool.find((s) => !busy(s.id))
        : pool.find((s) => s.id === r.specialistId && !busy(s.id));
    if (!specialist) throw new BookingApiError(409, "slot_unavailable");

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
