import { BeautyApiError } from "@/features/beauty-auth/errors";
import { money, dayIndex, weekDayLabels, weekRangeLabel } from "../format";
import { mastersForCalendar } from "../calendar-masters";
import { closedDayInfo, localDateIn, validateClosure, weekdayOf } from "../closed-days";
import { isValidTimeZone } from "../locations-logic";
import { outsideHours } from "../location-hours";
import { activeLinks } from "../staff-links";
import { normalizeHours, validateHours } from "../working-hours";
import { DEMO_TZ, blocksOn, demoToday } from "../../beauty-shared/demo-seed";
import { buildWeekAvailability, countFreeSlots, dayAvailability, slotStarts, toMinutes, type PlaceRules } from "../../beauty-shared/availability";
import { instantToLocal, mondayOfYmd } from "../../beauty-shared/zone-time";
import type { BeautyAdminApi } from "./client";
import {
  AI_GOALS,
  AI_REQUESTS,
  AI_SEGMENTS,
  ANALYTICS_LOCS,
  ANALYTICS_PROMOS,
  ANALYTICS_WEEKS,
  CLOSURES_SEED,
  DEFAULT_GREETING,
  INITIAL_CHANNELS,
  LOCATIONS,
  PRICE_LIST,
  ABSENCES_SEED,
  SERVICES,
  STAFF_SEED,
  clientProfileFor,
  clientSummaries,
  demoAppointments,
  type MockAbsence,
} from "./mock-data";
import type {
  Absence,
  AiChat,
  AiChatMessage,
  AbsenceConflict,
  AbsenceResult,
  AiPromoPlan,
  Appointment,
  BeautyLocation,
  CancelledBy,
  CancellationSettings,
  ChannelConfig,
  ClientNote,
  ClosedDayConflict,
  LocationClosure,
  LocationId,
  Overview,
  PromoDraft,
  PromoGoalId,
  StaffMember,
} from "../types";

const LATENCY_MS = 150;
const wait = <T>(value: T): Promise<T> =>
  new Promise((resolve) => setTimeout(() => resolve(structuredClone(value)), LATENCY_MS));

const pct = (v: number, max: number) => Math.round((v / max) * 100);

/** Змінний стан «сервера» в межах сесії браузера. */
const channels: ChannelConfig[] = INITIAL_CHANNELS.map((c) => ({ ...c, greeting: DEFAULT_GREETING }));
const requests = AI_REQUESTS.map((r) => ({ ...r }));
const chatMessages: AiChatMessage[] = [
  { from: "client", text: "Привіт! Нагадайте, о котрій мій запис?" },
  { from: "staff", text: "Добрий день, Олено! Ваш запис найближчим часом у Марини." },
  { from: "client", text: "Чудово, дякую! А чи буде знижка на педикюр?" },
];
/** Скасовані записи: хто, коли й чому (§16) - дані не видаляються. */
const cancelled = new Map<string, { at: string; by: CancelledBy; reason?: string }>();
const moved = new Map<string, string>();
const locations: BeautyLocation[] = structuredClone(LOCATIONS);
/** Закриття закладів на дати (§17). */
const closures: LocationClosure[] = structuredClone(CLOSURES_SEED());

/** Локальний «зараз» закладу `YYYY-MM-DDTHH:mm`. */
const nowLocal = () => instantToLocal(new Date().toISOString(), DEMO_TZ);

/** Роль демо-користувача збігається з `auth-provider` (NEXT_PUBLIC_MOCK_ROLE, за замовчуванням owner). */
const mockRole = () => process.env.NEXT_PUBLIC_MOCK_ROLE ?? "owner";
const isManager = () => mockRole() !== "specialist";
const MOCK_SPECIALIST_ID = "m";

/** Запис із урахуванням перенесень і скасувань; `cancelReason` лише керівникам (§16). */
function viewOf(a: Appointment): Appointment {
  const base = { ...a, startsAt: moved.get(a.id) ?? a.startsAt };
  const c = cancelled.get(a.id);
  return c
    ? { ...base, status: "cancelled" as const, cancelledAt: c.at, cancelledBy: c.by, ...(c.reason && isManager() ? { cancelReason: c.reason } : {}) }
    : base;
}
const allMockAppointments = (): Appointment[] => demoAppointments().map(viewOf);
/** Майбутні активні (pending/confirmed) записи: правила has_future_appointments / timezone_locked / конфлікти. */
const isUpcoming = (a: Appointment) =>
  !cancelled.has(a.id) && (a.status === "pending" || a.status === "confirmed") && a.startsAt > nowLocal();
const upcomingAt = (locationId: string) => allMockAppointments().filter((a) => a.locationId === locationId && isUpcoming(a));
const hasFutureAt = (locationId: string) => upcomingAt(locationId).length > 0;

const locationRules = (locationId: string) => ({
  closedWeekdays: locations.find((l) => l.id === locationId)?.closedWeekdays ?? [],
  closures: closures.filter((c) => c.locationId === locationId),
});

const staff: StaffMember[] = structuredClone(STAFF_SEED);
const absences: MockAbsence[] = ABSENCES_SEED();
const mockInvites: { staffId: string; id: string; email: string; expiresAt: string }[] = [];

/** Правила доступності майстра в закладі (графік ∩ години ∩ вихідні) за поточним станом mock. */
function placeOf(master: StaffMember | undefined, locationId: string): PlaceRules | null {
  const link = master?.locations.find((l) => l.locationId === locationId && l.isActive !== false);
  const loc = locations.find((l) => l.id === locationId);
  if (!link || !loc) return null;
  return { masterHours: link.workingHours, locationHours: loc.workingHours, ...locationRules(locationId) };
}

/**
 * Активні записи закладу, що потрапляють на НОВІ закриті дні (`isNewlyClosed`) - без клієнтських даних (§17).
 * Враховуються лише майбутні pending/confirmed записи (як у backend).
 */
function conflictsOnNewClosedDays(locationId: string, isNewlyClosed: (date: string) => boolean): ClosedDayConflict[] {
  const tz = locations.find((l) => l.id === locationId)?.timezone;
  return upcomingAt(locationId)
    .filter((a) => isNewlyClosed(localDateIn(a.startsAt, tz)))
    .map((a) => ({
      appointmentId: a.id,
      startsAt: a.startsAt,
      serviceName: a.serviceName,
      specialistName: staff.find((m) => m.id === a.specialistId)?.name ?? "",
    }));
}

const confirmNeeded = (conflicts: ClosedDayConflict[]) =>
  new BeautyApiError(409, "has_appointments_on_closed_days", "appointments on closed days", undefined, conflicts);

const nowLabel = () => new Date().toISOString();
let cancellationSettings: CancellationSettings = {
  windowHours: 12,
  refundPercentInWindow: 50,
  refundPercentOutside: 100,
  deductFee: false,
  feePercent: 0,
};
const addedNotes: Record<string, ClientNote[]> = {};

const forbid = () => new BeautyApiError(403, "forbidden_role", "forbidden");
const notFound = () => new BeautyApiError(404, "not_found", "not found");

/** Правило видимості §13: `note` лише керівнику й автору; інакше поля немає взагалі. */
function view(a: MockAbsence): Absence {
  const { by, note, ...rest } = a;
  return isManager() || by === "me" ? { ...rest, note } : rest;
}

function conflictsFor(a: MockAbsence): AbsenceConflict[] {
  if (!isManager()) return [];
  return allMockAppointments()
    .filter((x) => x.specialistId === a.specialistId && isUpcoming(x))
    .filter((x) => x.startsAt.slice(0, 10) >= a.dateFrom && x.startsAt.slice(0, 10) <= a.dateTo)
    .map((x) => ({ appointmentId: x.id, startsAt: x.startsAt, serviceName: x.serviceName }));
}

function findAbsence(id: string): MockAbsence {
  const a = absences.find((x) => x.id === id);
  if (!a) throw notFound();
  return a;
}

function staffView(m: StaffMember): StaffMember {
  return isManager() ? m : { ...m, phone: null };
}

function findChannel(id: string): ChannelConfig {
  const c = channels.find((x) => x.id === id);
  if (!c) throw new Error(`Канал не знайдено: ${id}`);
  return c;
}

function locationName(id: string): string {
  return locations.find((l) => l.id === id)?.name ?? id;
}

const activeLocations = () => locations.filter((l) => l.isActive !== false);

/** Перевірки переносу в порядку backend (§17-§18): slot_in_past, location_closed, specialist_unavailable, outside_location_hours, outside_working_hours, slot_unavailable. */
function checkMove(a: Appointment, startsAt: string): void {
  if (startsAt <= nowLocal()) throw new BeautyApiError(409, "slot_in_past", "past");
  const date = startsAt.slice(0, 10);
  const place = placeOf(staff.find((m) => m.id === a.specialistId), a.locationId);
  if (!place) throw new BeautyApiError(422, "specialist_not_at_location", "not at location");
  const day = dayAvailability(date, place, blocksOn(absences, a.specialistId, date));
  if (day.state === "location_closed") throw new BeautyApiError(409, "location_closed", "closed");
  if (day.state === "absence") throw new BeautyApiError(409, "specialist_unavailable", "absent");
  const start = toMinutes(startsAt.slice(11, 16));
  const inside = (list: { from: number; to: number }[]) => list.some((i) => i.from <= start && start + a.durationMinutes <= i.to);
  if (day.location && !inside(day.location)) throw new BeautyApiError(409, "outside_location_hours", "outside location hours");
  if (!inside(day.master)) throw new BeautyApiError(422, "outside_working_hours", "outside working hours");
  const clash = allMockAppointments().some((x) => {
    if (x.id === a.id || x.specialistId !== a.specialistId || x.status === "cancelled" || x.startsAt.slice(0, 10) !== date) return false;
    const s = toMinutes(x.startsAt.slice(11, 16));
    return start < s + x.durationMinutes && start + a.durationMinutes > s;
  });
  if (clash) throw new BeautyApiError(409, "slot_unavailable", "busy");
}

/** Вільні слоти сьогодні (KPI огляду): за найкоротшою послугою майстра, з урахуванням годин, відсутностей і зайнятого. */
function freeSlotsToday(locationId: LocationId | null): number {
  const today = demoToday();
  const now = nowLocal();
  const minStart = toMinutes(now.slice(11, 16));
  const appts = allMockAppointments().filter((a) => a.startsAt.slice(0, 10) === today && a.status !== "cancelled");
  let total = 0;
  for (const m of staff.filter((x) => x.isActive)) {
    const shortest = Math.min(...m.services.map((s) => SERVICES.find((x) => x.id === s.id)?.durationMinutes ?? 60), 600);
    for (const link of activeLinks(m).filter((l) => !locationId || l.locationId === locationId)) {
      const place = placeOf(m, link.locationId);
      if (!place) continue;
      const day = dayAvailability(today, place, blocksOn(absences, m.id, today));
      const busy = appts
        .filter((a) => a.specialistId === m.id && a.locationId === link.locationId)
        .map((a) => ({ from: toMinutes(a.startsAt.slice(11, 16)), to: toMinutes(a.startsAt.slice(11, 16)) + a.durationMinutes }));
      total += countFreeSlots(slotStarts(day, shortest, { busy, minStart }), shortest);
    }
  }
  return total;
}

export const mockBeautyApi: BeautyAdminApi = {
  getLocations: () => wait(activeLocations()),

  getManagedLocations: async () => {
    if (!isManager()) throw forbid();
    return wait(locations);
  },

  createLocation: async (input) => {
    if (!isManager()) throw forbid();
    const name = input.name.trim();
    if (!isValidTimeZone(input.timezone)) throw new BeautyApiError(422, "invalid_timezone", "invalid timezone");
    if (locations.some((l) => l.name.toLowerCase() === name.toLowerCase())) throw new BeautyApiError(409, "location_name_taken", "name taken");
    const created: BeautyLocation = {
      id: `loc${Date.now()}`,
      name,
      address: input.address.trim() || null,
      phone: input.phone.trim() || null,
      timezone: input.timezone.trim(),
      isActive: true,
      closedWeekdays: [],
      workingHours: null,
    };
    locations.push(created);
    return wait(created);
  },

  updateLocation: async (id, input) => {
    if (!isManager()) throw forbid();
    const loc = locations.find((l) => l.id === id);
    if (!loc) throw notFound();
    const name = input.name.trim();
    if (!isValidTimeZone(input.timezone)) throw new BeautyApiError(422, "invalid_timezone", "invalid timezone");
    if (locations.some((l) => l.id !== id && l.name.toLowerCase() === name.toLowerCase())) throw new BeautyApiError(409, "location_name_taken", "name taken");
    if (loc.isActive !== false && !input.isActive && hasFutureAt(id)) throw new BeautyApiError(409, "has_future_appointments", "future");
    if (input.timezone.trim() !== loc.timezone && hasFutureAt(id)) throw new BeautyApiError(409, "timezone_locked", "locked");
    Object.assign(loc, { name, address: input.address.trim() || null, phone: input.phone.trim() || null, timezone: input.timezone.trim(), isActive: input.isActive });
    return wait(loc);
  },

  setClosedWeekdays: async (id, weekdays, confirm) => {
    if (!isManager()) throw forbid();
    const loc = locations.find((l) => l.id === id);
    if (!loc) throw new BeautyApiError(404, "location_not_found", "not found");
    const next = [...new Set(weekdays)];
    const old = loc.closedWeekdays ?? [];
    const added = next.filter((d) => !old.includes(d));
    if (added.length > 0 && !confirm) {
      // Дні, що вже закриті закриттям за датою, не є «новими».
      const conflicts = conflictsOnNewClosedDays(id, (date) => added.includes(weekdayOf(date)) && !closedDayInfo(date, locationRules(id)));
      if (conflicts.length > 0) throw confirmNeeded(conflicts);
    }
    loc.closedWeekdays = next;
    return wait(next);
  },

  setLocationWorkingHours: async (id, workingHours, confirm) => {
    if (!isManager()) throw forbid();
    const loc = locations.find((l) => l.id === id);
    if (!loc) throw new BeautyApiError(404, "location_not_found", "not found");
    if (workingHours && Object.keys(validateHours(workingHours)).length > 0) {
      throw new BeautyApiError(422, "invalid_working_hours", "invalid");
    }
    const next = workingHours ? normalizeHours(workingHours) : null;
    if (next && !confirm) {
      const rows = upcomingAt(id).map((a) => ({ a, startsAt: a.startsAt }));
      const bad = new Set(outsideHours(rows, next, loc.timezone).map((r) => r.a.id));
      const conflicts = rows
        .filter((r) => bad.has(r.a.id))
        .map(({ a, startsAt }) => ({
          appointmentId: a.id,
          startsAt,
          serviceName: a.serviceName,
          specialistName: staff.find((m) => m.id === a.specialistId)?.name ?? "",
        }));
      if (conflicts.length > 0) {
        throw new BeautyApiError(409, "has_appointments_outside_hours", "appointments outside hours", undefined, conflicts);
      }
    }
    loc.workingHours = next;
    return wait(next);
  },

  getClosures: async (id, { from, to }) => {
    const list = closures.filter((c) => c.locationId === id && c.dateTo >= from && c.dateFrom <= to);
    // `reason` лише керівникам (§17).
    return wait(list.map((c) => (isManager() ? c : { id: c.id, locationId: c.locationId, dateFrom: c.dateFrom, dateTo: c.dateTo })));
  },

  addClosure: async (id, input) => {
    if (!isManager()) throw forbid();
    if (!locations.some((l) => l.id === id)) throw new BeautyApiError(404, "location_not_found", "not found");
    if (validateClosure(input)) {
      const tooLong = (input.reason ?? "").trim().length > 200;
      throw new BeautyApiError(422, tooLong ? "invalid_reason" : "invalid_dates", "invalid");
    }
    if (closures.some((c) => c.locationId === id && c.dateFrom <= input.dateTo && c.dateTo >= input.dateFrom)) {
      throw new BeautyApiError(409, "closure_overlap", "overlap");
    }
    if (!input.confirm) {
      const before = locationRules(id);
      const conflicts = conflictsOnNewClosedDays(
        id,
        (date) => date >= input.dateFrom && date <= input.dateTo && !closedDayInfo(date, before),
      );
      if (conflicts.length > 0) throw confirmNeeded(conflicts);
    }
    const reason = input.reason?.trim();
    const created: LocationClosure = {
      id: `cl${Date.now()}`,
      locationId: id,
      dateFrom: input.dateFrom,
      dateTo: input.dateTo,
      ...(reason ? { reason } : {}),
    };
    closures.push(created);
    return wait(created);
  },

  deleteClosure: async (id, closureId) => {
    if (!isManager()) throw forbid();
    const k = closures.findIndex((c) => c.id === closureId && c.locationId === id);
    if (k < 0) throw notFound();
    closures.splice(k, 1);
    await wait(null);
  },

  getOverview: (locationId) => {
    const today = demoToday();
    const todays = allMockAppointments()
      .filter((a) => a.startsAt.slice(0, 10) === today && a.status !== "cancelled" && (!locationId || a.locationId === locationId))
      .sort((a, b) => a.startsAt.localeCompare(b.startsAt));
    const revenue = todays.filter((a) => a.status === "completed").reduce((sum, a) => sum + a.priceFinal, 0);
    const dateLabel = new Date(`${today}T00:00:00Z`).toLocaleDateString("uk-UA", { day: "numeric", month: "long", timeZone: "UTC" });
    const overview: Overview = {
      dateLabel: locationId ? `${locationName(locationId)} · ${dateLabel}` : `Мережа закладів · ${dateLabel}`,
      kpis: [
        { label: "Записів сьогодні", value: String(todays.length), note: `завершено: ${todays.filter((a) => a.status === "completed").length}` },
        { label: "Виручка за день", value: money(revenue), note: "завершені візити" },
        { label: "Нові клієнти", value: String(new Set(todays.filter((a) => a.kind === "new").map((a) => a.clientId)).size), note: "за добу" },
        { label: "Вільних слотів", value: String(freeSlotsToday(locationId)), note: "до кінця дня" },
      ],
      rows: todays.map((a) => ({
        id: a.id,
        time: a.startsAt.slice(11, 16),
        clientId: a.clientId,
        clientName: a.clientName,
        serviceName: a.serviceName,
        specialistId: a.specialistId,
        specialistName: staff.find((m) => m.id === a.specialistId)?.name ?? "",
        locationId: a.locationId,
        locationName: locationName(a.locationId),
        status: a.status,
      })),
      clients: clientSummaries(allMockAppointments()),
    };
    return wait(overview);
  },

  getCalendarWeek: (requestedId, includeCancelled = false, requestedLocationId) => {
    const weekStart = mondayOfYmd(demoToday());
    const inWeek = (a: Appointment) => {
      const d = dayIndex(a.startsAt, weekStart);
      return d >= 0 && d <= 6;
    };
    const weekAppts = allMockAppointments().filter(inWeek);
    const live = weekAppts.filter((a) => a.status !== "cancelled");
    const me = isManager() ? null : MOCK_SPECIALIST_ID;
    const masters = mastersForCalendar(
      staff.map((m) => ({ id: m.id, name: m.name, isActive: m.isActive, locationNames: activeLinks(m).map((l) => l.locationName) })),
      live,
      me,
    );
    const specialistId = masters.find((m) => m.id === requestedId)?.id ?? masters[0]?.id ?? requestedId;
    const master = staff.find((m) => m.id === specialistId);
    // Заклад, обраний у календарі: доступність і записи рахуються для нього (майстер може працювати в кількох закладах).
    const links = activeLinks(master ?? { locations: [] }).filter((l) => locations.some((x) => x.id === l.locationId && x.isActive !== false));
    const chosen = links.find((l) => l.locationId === requestedLocationId) ?? links[0];
    const place = chosen ? placeOf(master, chosen.locationId) : null;
    const wa = place ? buildWeekAvailability(weekStart, place) : null;
    const mine = weekAppts.filter((a) => a.specialistId === specialistId && (includeCancelled || a.status !== "cancelled"));
    const appointments = mine.filter((a) => !chosen || a.locationId === chosen.locationId);
    return wait({
      weekLabel: weekRangeLabel(weekStart),
      weekStart,
      // `reason` лише керівникам: specialist отримує закритий день без причини.
      closedDays: wa?.closedDays.map((info) => (info && !isManager() ? { date: info.date, source: info.source } : info)),
      days: weekDayLabels(weekStart),
      masters,
      specialistId,
      appointments,
      locations: links.map((l) => ({ id: l.locationId, name: l.locationName })),
      ...(chosen ? { locationId: chosen.locationId } : {}),
      ...(wa ? { availability: wa.availability, gridStartHour: wa.gridStartHour, gridEndHour: wa.gridEndHour } : {}),
      otherLocationsCount: mine.length - appointments.length,
    });
  },

  getUpcomingAppointmentsCount: async (specialistId) =>
    wait(allMockAppointments().filter((a) => a.specialistId === specialistId && isUpcoming(a)).length),

  moveAppointment: async (id, startsAt) => {
    const appt = allMockAppointments().find((a) => a.id === id);
    if (!appt) throw notFound();
    checkMove(appt, startsAt);
    moved.set(id, startsAt);
    await wait(null);
  },

  getCancellationSettings: () => wait(cancellationSettings),

  updateCancellationSettings: async (next) => {
    cancellationSettings = { ...next };
    return wait(cancellationSettings);
  },

  cancelAppointment: async (id, reason) => {
    if (cancelled.has(id)) throw new BeautyApiError(409, "already_cancelled", "already");
    const text = reason?.trim().slice(0, 300);
    cancelled.set(id, {
      at: nowLabel(),
      by: { type: "staff", ...(isManager() ? { name: "Світлана Коваленко" } : {}) },
      ...(text ? { reason: text } : {}),
    });
    // Mock: політика ≤12 год → 50% повернення (рахує backend).
    const price = allMockAppointments().find((a) => a.id === id)?.priceFinal ?? 0;
    return wait({ refundAmount: Math.round(price * 0.5) });
  },

  getClients: () => wait(clientSummaries(allMockAppointments())),

  getClient: (id) => {
    const profile = clientProfileFor(id, allMockAppointments());
    const seed = clientSummaries(allMockAppointments()).find((x) => x.id === id);
    if (!profile || !seed) return wait(null);
    return wait({
      id,
      name: seed.name,
      tag: seed.tag,
      ...profile,
      notes: [...(addedNotes[id] ?? []), ...profile.notes],
    });
  },

  addClientNote: async (id, text) => {
    addedNotes[id] = [{ id: `n${Date.now()}`, text, meta: "Адміністратор · щойно" }, ...(addedNotes[id] ?? [])];
    await wait(null);
  },

  getStaff: () => wait(staff.map(staffView)),
  getStaffProfile: (id) => {
    const m = staff.find((x) => x.id === id);
    return wait(m ? staffView(m) : null);
  },
  getServices: () => wait(SERVICES),

  createStaff: async (input) => {
    if (!isManager()) throw forbid();
    const member: StaffMember = {
      id: `st${Date.now()}`,
      name: input.name.trim(),
      phone: input.phone.trim() || null,
      position: input.position.trim() || null,
      isActive: true,
      services: SERVICES.filter((x) => input.serviceIds.includes(x.id)).map(({ id, name }) => ({ id, name })),
      locations: input.locationIds.map((id) => ({
        locationId: id,
        locationName: locationName(id),
        isActive: true,
        workingHours: normalizeHours(input.workingHours),
      })),
    };
    if (Object.keys(validateHours(input.workingHours)).length > 0) throw new BeautyApiError(422, "invalid_working_hours", "invalid");
    staff.push(member);
    return wait(member);
  },

  updateStaff: async (id, input) => {
    if (!isManager()) throw forbid();
    const m = staff.find((x) => x.id === id);
    if (!m) throw notFound();
    Object.assign(m, { name: input.name.trim(), phone: input.phone.trim() || null, position: input.position.trim() || null, isActive: input.isActive });
    await wait(null);
  },

  setStaffServices: async (id, serviceIds) => {
    if (!isManager()) throw forbid();
    const m = staff.find((x) => x.id === id);
    if (!m) throw notFound();
    m.services = SERVICES.filter((x) => serviceIds.includes(x.id)).map(({ id: sid, name }) => ({ id: sid, name }));
    await wait(null);
  },

  setStaffSchedule: async (id, locationId, workingHours) => {
    if (!isManager()) throw forbid();
    const m = staff.find((x) => x.id === id);
    if (!m) throw notFound();
    if (Object.keys(validateHours(workingHours)).length > 0) throw new BeautyApiError(422, "invalid_working_hours", "invalid");
    const loc = m.locations.find((l) => l.locationId === locationId);
    if (!loc) throw notFound();
    loc.workingHours = normalizeHours(workingHours);
    await wait(null);
  },

  inviteStaff: async (id, email) => {
    if (!isManager()) throw forbid();
    if (!staff.some((x) => x.id === id)) throw notFound();
    mockInvites.push({ staffId: id, id: crypto.randomUUID(), email, expiresAt: new Date(Date.now() + 7 * 86_400_000).toISOString() });
    return wait({ token: `mock-${crypto.randomUUID()}` });
  },

  getStaffInvites: async (staffId) => {
    if (!isManager()) throw forbid();
    return wait(mockInvites.filter((i) => i.staffId === staffId).map(({ id, email, expiresAt }) => ({ id, email, expiresAt })));
  },

  revokeInvite: async (inviteId) => {
    if (!isManager()) throw forbid();
    const k = mockInvites.findIndex((i) => i.id === inviteId);
    if (k >= 0) mockInvites.splice(k, 1);
    await wait(null);
  },

  getAbsences: ({ from, to, specialistId }) => {
    const list = absences.filter(
      (a) => a.dateTo >= from && a.dateFrom <= to && (!specialistId || a.specialistId === specialistId),
    );
    return wait(list.map(view));
  },

  createAbsence: async (specialistId, input): Promise<AbsenceResult> => {
    const manager = isManager();
    if (!manager && specialistId !== MOCK_SPECIALIST_ID) throw forbid();
    if (!staff.some((x) => x.id === specialistId)) throw notFound();
    if (input.dateTo < input.dateFrom) throw new BeautyApiError(422, "invalid_request", "invalid dates");
    const overlap = absences.some(
      (a) =>
        a.specialistId === specialistId &&
        (a.status === "requested" || a.status === "approved") &&
        a.dateFrom <= input.dateTo &&
        a.dateTo >= input.dateFrom,
    );
    if (overlap) throw new BeautyApiError(409, "absence_overlap", "overlap");
    const created: MockAbsence = {
      id: `ab${Date.now()}`,
      specialistId,
      type: input.type,
      dateFrom: input.dateFrom,
      dateTo: input.dateTo,
      status: manager ? "approved" : "requested",
      note: input.note ?? "",
      by: "me",
    };
    absences.push(created);
    return wait({ absence: view(created), conflicts: conflictsFor(created) });
  },

  approveAbsence: async (id): Promise<AbsenceResult> => {
    if (!isManager()) throw forbid();
    const a = findAbsence(id);
    a.status = "approved";
    return wait({ absence: view(a), conflicts: conflictsFor(a) });
  },

  rejectAbsence: async (id) => {
    if (!isManager()) throw forbid();
    findAbsence(id).status = "rejected";
    await wait(null);
  },

  cancelAbsence: async (id) => {
    const a = findAbsence(id);
    if (!isManager() && a.by !== "me") throw forbid();
    a.status = "cancelled";
    await wait(null);
  },
  getPriceList: () => wait(PRICE_LIST),

  previewPromotion: (draft: PromoDraft) => {
    const factor = (100 - draft.percent) / 100;
    const active = draft.locationIds.length > 0;
    const where =
      draft.locationIds.length === activeLocations().length
        ? "Усі заклади"
        : draft.locationIds.map(locationName).join(", ") || "Не вибрано";
    const base = PRICE_LIST[0].price;
    return wait({
      rows: PRICE_LIST.map((s) => {
        const on = active && draft.serviceIds.includes(s.serviceId);
        return {
          ...s,
          promoPrice: on ? Math.round(s.price * factor) : null,
          where: on ? where : "Усі заклади",
        };
      }),
      locationSummary: where,
      samplePrice: Math.round(base * factor),
      sampleOriginal: base,
    });
  },

  createPromotion: async () => wait({ id: `promo-${Date.now()}` }),

  getNetworkAnalytics: () => {
    const l = Object.entries(ANALYTICS_LOCS);
    const totalRev = l.reduce((a, [, v]) => a + v.rev, 0);
    const totalN = l.reduce((a, [, v]) => a + v.n, 0);
    return wait({
      kpis: [
        { label: "Виручка мережі", value: money(totalRev), note: "за місяць, 3 заклади" },
        { label: "Записів", value: String(totalN), note: "з них онлайн 71%" },
        { label: "Середній чек", value: money(Math.round(totalRev / totalN)), note: "по мережі" },
        { label: "Повторні клієнти", value: "61%", note: "прийшли вдруге за 60 днів" },
      ],
      weeks: ANALYTICS_WEEKS.map((value, i) => ({ label: `Т${i + 1}`, value })),
      locations: l.map(([id, v]) => ({
        id,
        label: v.label,
        value: money(v.rev),
        pct: pct(v.rev, ANALYTICS_LOCS.c.rev),
        note: `${v.n} записів · середній чек ${money(v.avg)}`,
      })),
    });
  },

  getLocationAnalytics: (locationId: LocationId) => {
    const L = ANALYTICS_LOCS[locationId as keyof typeof ANALYTICS_LOCS] ?? ANALYTICS_LOCS.c;
    const totalRev = Object.values(ANALYTICS_LOCS).reduce((a, v) => a + v.rev, 0);
    const smax = Math.max(...L.svcs.map((r) => r[1]));
    return wait({
      locationId,
      kpis: [
        { label: "Виручка закладу", value: money(L.rev), note: `${Math.round((L.rev / totalRev) * 100)}% від мережі` },
        { label: "Записів", value: String(L.n), note: "за місяць" },
        { label: "Середній чек", value: money(L.avg), note: "по закладу" },
        { label: "Завантаження", value: `${L.load}%`, note: "зайнятих годин у графіку" },
      ],
      specialists: L.specs.map(([id, name, count, rev, load]) => ({ id, name, count, revenue: money(rev), load })),
      topServices: L.svcs.map(([label, v]) => ({ label, value: money(v), pct: pct(v, smax) })),
    });
  },

  getPromotionAnalytics: () => {
    const sum = (k: "n" | "fresh" | "rev" | "cost") => ANALYTICS_PROMOS.reduce((a, p) => a + p[k], 0);
    const totalN = Object.values(ANALYTICS_LOCS).reduce((a, v) => a + v.n, 0);
    const pmax = Math.max(...ANALYTICS_PROMOS.map((p) => p.n));
    return wait({
      kpis: [
        { label: "Записів по акціях", value: String(sum("n")), note: `${Math.round((sum("n") / totalN) * 100)}% від усіх записів` },
        { label: "Нових клієнтів", value: String(sum("fresh")), note: "прийшли через акції" },
        { label: "Виручка з акцій", value: money(sum("rev")), note: "за місяць" },
        { label: "Віддано знижками", value: money(sum("cost")), note: "вартість знижок" },
      ],
      rows: ANALYTICS_PROMOS.map((p) => ({
        id: p.id, name: p.name, period: p.period, where: p.where, count: p.n, fresh: p.fresh, revenue: money(p.rev), cost: money(p.cost),
      })),
      bars: ANALYTICS_PROMOS.map((p) => ({ label: p.name, value: String(p.n), pct: pct(p.n, pmax) })),
    });
  },

  getChannels: () => wait(channels),

  updateChannel: async (id, patch) => {
    const c = findChannel(id);
    const { handoff, ...rest } = patch;
    Object.assign(c, rest);
    if (handoff) c.handoff = { ...c.handoff, ...handoff };
    return wait(c);
  },

  connectChannel: async ({ id, secret }) => {
    if (!secret.trim()) throw new Error("Вкажіть дані для підключення");
    const c = findChannel(id);
    c.connected = true;
    c.maskedSecret = "••••••••••••";
    return wait(c);
  },

  disconnectChannel: async (id) => {
    const c = findChannel(id);
    c.connected = false;
    c.maskedSecret = null;
    return wait(c);
  },

  getAiRequests: () => wait(requests),

  approveAiRequest: async (id) => {
    const r = requests.find((x) => x.id === id);
    if (r) r.replied = true;
    await wait(null);
  },

  getAiChat: () => {
    const chat: AiChat = {
      clientName: "Олена Кравченко",
      channel: "Telegram",
      messages: chatMessages,
      suggestions: [
        "Так, на педикюр діє −15% до 14 жовтня. Додати його до вашого візиту?",
        "Можу записати вас на педикюр одразу після манікюру, щоб не приходити двічі.",
        "Нагадаю про акції ближче до візиту.",
      ],
      context: [
        { label: "Статус", value: "VIP, 24 візити" },
        { label: "Майстер", value: "Марина Бойко" },
        { label: "Наступний запис", value: "19 жовт., 15:00" },
      ],
      warning: "Алергія на латекс. AI враховує це в пропозиціях.",
    };
    return wait(chat);
  },

  sendAiChatMessage: async (text) => {
    chatMessages.push({ from: "staff", text });
    await wait(null);
  },

  getAiPromoPlan: (goalId: PromoGoalId) => {
    const g = AI_GOALS[goalId];
    const plan: AiPromoPlan = {
      goalId,
      goals: (Object.keys(AI_GOALS) as PromoGoalId[]).map((id) => ({ id, label: AI_GOALS[id].label })),
      reason: g.reason,
      when: g.when,
      percent: g.percent,
      rows: g.rows.map(([name, old]) => ({ name, old, price: Math.round((old * (100 - g.percent)) / 100) })),
      text: g.text(g.percent),
      segments: AI_SEGMENTS,
      recommendedSegmentIds: g.rec,
    };
    return wait(plan);
  },

  launchAiCampaign: ({ segmentIds }) =>
    wait({ reach: AI_SEGMENTS.filter((s) => segmentIds.includes(s.id)).reduce((a, s) => a + s.count, 0) }),
};
