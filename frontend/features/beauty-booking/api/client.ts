import {
  ANY_SPECIALIST_ID,
  type Appointment,
  BookingApiError,
  type CreateAppointmentRequest,
  type Location,
  type Service,
  type Slot,
  type Specialist,
  createAppointmentSchema,
} from "../types";

/**
 * Mock-клієнт за контрактом `/api/beauty/...` (.claude/docs/beauty-contracts.md §3).
 * Заміна на реальний fetch не змінює сигнатур.
 */
export interface BookingApi {
  getLocations(): Promise<Location[]>;
  getSpecialists(locationId: string): Promise<Specialist[]>;
  getServices(locationId: string, specialistId: string): Promise<Service[]>;
  /** GET /slots?locationId&specialistId&serviceId&date */
  getSlots(q: {
    locationId: string;
    specialistId: string;
    serviceId: string;
    date: string;
  }): Promise<Slot[]>;
  /** POST /appointments */
  createAppointment(req: CreateAppointmentRequest): Promise<Appointment>;
  getAppointment(id: string): Promise<Appointment>;
}

const delay = <T>(v: T, ms = 250) => new Promise<T>((r) => setTimeout(() => r(v), ms));

const locations: Location[] = [
  { id: "c", name: "Beauty Lab · Центр", address: "вул. Хрещатик, 22", hours: "щодня 09:00–21:00", hasPromo: true },
  { id: "p", name: "Beauty Lab · Поділ", address: "вул. Сагайдачного, 10", hours: "щодня 10:00–20:00", hasPromo: false },
  { id: "k", name: "Beauty Lab · Печерськ", address: "вул. Лаврська, 7", hours: "пн–сб 09:00–20:00", hasPromo: true },
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

function loadStore(): Record<string, Appointment> {
  if (typeof window === "undefined") return {};
  try {
    return JSON.parse(window.sessionStorage.getItem(STORE_KEY) ?? "{}");
  } catch {
    return {};
  }
}

function saveStore(s: Record<string, Appointment>) {
  window.sessionStorage.setItem(STORE_KEY, JSON.stringify(s));
}

function slotIso(date: string, hhmm: string): string {
  const [h, m] = hhmm.split(":").map(Number);
  const d = new Date(`${date}T00:00:00`);
  d.setHours(h, m, 0, 0);
  return d.toISOString();
}

export const mockBookingApi: BookingApi = {
  getLocations: () => delay(locations),

  getSpecialists: (locationId) =>
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

  async getSlots({ locationId, specialistId, serviceId, date }) {
    const taken = Object.values(loadStore())
      .filter(
        (a) =>
          a.locationId === locationId &&
          a.serviceId === serviceId &&
          (specialistId === ANY_SPECIALIST_ID || a.specialistId === specialistId),
      )
      .map((a) => a.startsAt);
    const now = Date.now();
    const slots = SLOT_TIMES.map((label) => ({ label, startsAt: slotIso(date, label) }))
      .filter((s) => new Date(s.startsAt).getTime() > now)
      .filter((s) => !taken.includes(s.startsAt));
    return delay(slots);
  },

  async createAppointment(req) {
    const parsed = createAppointmentSchema.safeParse(req);
    if (!parsed.success) throw new BookingApiError(422, "Перевірте введені дані");
    const r = parsed.data;
    const location = locations.find((l) => l.id === r.locationId);
    const service = services.find((s) => s.id === r.serviceId);
    if (!location || !service) throw new BookingApiError(422, "Невідомий заклад або послугу");

    const pool = specialists[r.locationId] ?? [];
    const store = loadStore();
    const busy = (specId: string) =>
      Object.values(store).some((a) => a.specialistId === specId && a.startsAt === r.startsAt);
    const specialist =
      r.specialistId === ANY_SPECIALIST_ID
        ? pool.find((s) => !busy(s.id))
        : pool.find((s) => s.id === r.specialistId && !busy(s.id));
    if (!specialist) throw new BookingApiError(409, "Цей час уже зайнято. Оберіть інший.");

    const appointment: Appointment = {
      id: crypto.randomUUID(),
      locationId: location.id,
      locationName: location.name,
      specialistId: specialist.id,
      specialistName: specialist.name,
      serviceId: service.id,
      serviceName: service.name,
      startsAt: r.startsAt,
      durationMinutes: service.durationMinutes,
      status: "confirmed",
      source: "online",
      priceOriginal: service.priceOriginal,
      priceFinal: service.priceFinal,
      promotionId: service.promotionId,
      reminderOption: r.reminder,
      paymentMethod: r.paymentMethod,
    };
    store[appointment.id] = appointment;
    saveStore(store);
    return delay(appointment, 400);
  },

  async getAppointment(id) {
    const a = loadStore()[id];
    if (!a) throw new BookingApiError(404, "Запис не знайдено");
    return delay(a);
  },
};

export const bookingApi: BookingApi = mockBookingApi;
