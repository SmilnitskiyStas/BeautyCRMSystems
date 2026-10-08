import { BeautyApiError } from "@/features/beauty-auth/errors";
import { money } from "../format";
import { normalizeHours, validateHours } from "../working-hours";
import type { BeautyAdminApi } from "./client";
import {
  AI_GOALS,
  AI_REQUESTS,
  AI_SEGMENTS,
  ANALYTICS_LOCS,
  ANALYTICS_PROMOS,
  ANALYTICS_WEEKS,
  CALENDAR_DAYS,
  CLIENTS,
  DEFAULT_GREETING,
  INITIAL_CHANNELS,
  LOCATIONS,
  MASTERS,
  OVERVIEW_ROWS,
  PRICE_LIST,
  ABSENCES_SEED,
  SERVICES,
  STAFF_SEED,
  appointmentsFor,
  type MockAbsence,
} from "./mock-data";
import type {
  Absence,
  AiChat,
  AiChatMessage,
  AbsenceConflict,
  AbsenceResult,
  AiPromoPlan,
  CancellationSettings,
  ChannelConfig,
  ClientNote,
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
  { from: "staff", text: "Добрий день, Олено! Ваш запис 19 жовтня о 15:00 до Марини." },
  { from: "client", text: "Чудово, дякую! А чи буде знижка на педикюр?" },
];
const cancelled = new Set<string>();
const moved = new Map<string, string>();
let cancellationSettings: CancellationSettings = {
  windowHours: 12,
  refundPercentInWindow: 50,
  refundPercentOutside: 100,
  deductFee: false,
  feePercent: 0,
};
const addedNotes:Record<string, ClientNote[]> = {};

const staff: StaffMember[] = structuredClone(STAFF_SEED);
const absences: MockAbsence[] = ABSENCES_SEED();
const mockInvites: { staffId: string; id: string; email: string; expiresAt: string }[] = [];

/** Роль демо-користувача збігається з `auth-provider` (NEXT_PUBLIC_MOCK_ROLE, за замовчуванням owner). */
const mockRole = () => process.env.NEXT_PUBLIC_MOCK_ROLE ?? "owner";
const isManager = () => mockRole() !== "specialist";
const MOCK_SPECIALIST_ID = "m";

const forbid = () => new BeautyApiError(403, "forbidden_role", "forbidden");
const notFound = () => new BeautyApiError(404, "not_found", "not found");

/** Правило видимості §13: `note` лише керівнику й автору; інакше поля немає взагалі. */
function view(a: MockAbsence): Absence {
  const { by, note, ...rest } = a;
  return isManager() || by === "me" ? { ...rest, note } : rest;
}

function conflictsFor(a: MockAbsence): AbsenceConflict[] {
  if (!isManager()) return [];
  return appointmentsFor(a.specialistId)
    .filter((x) => x.kind !== "break" && !cancelled.has(x.id))
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
  return LOCATIONS.find((l) => l.id === id)?.name ?? id;
}

export const mockBeautyApi: BeautyAdminApi = {
  getLocations: () => wait(LOCATIONS),

  getOverview: (locationId) => {
    const rows = OVERVIEW_ROWS.filter((r) => !locationId || r.locationId === locationId).map((r) => ({ ...r }));
    return wait({
      dateLabel: "Мережа закладів · 5 жовтня",
      kpis: [
        { label: "Записів сьогодні", value: String(locationId ? rows.length : 42), note: "з них 6 онлайн за останню годину" },
        { label: "Виручка за день", value: "38 400 ₴", note: "очікується до закриття" },
        { label: "Нові клієнти", value: "9", note: "за добу" },
        { label: "Вільних слотів", value: "14", note: "до 21:00 по мережі" },
      ],
      rows: rows as Overview["rows"],
      clients: CLIENTS.map(({ id, name, meta, tag }) => ({ id, name, meta, tag })),
    });
  },

  getCalendarWeek: (requestedId) => {
    const specialistId = requestedId || "m";
    return wait({
      weekLabel: "Тиждень 5–11 жовтня",
      weekStart: "2026-10-05",
      days: CALENDAR_DAYS,
      masters: MASTERS,
      specialistId,
      appointments: appointmentsFor(specialistId)
        .filter((a) => !cancelled.has(a.id))
        .map((a) => ({ ...a, startsAt: moved.get(a.id) ?? a.startsAt })),
    });
  },

  moveAppointment: async (id, startsAt) => {
    moved.set(id, startsAt);
    await wait(null);
  },

  getCancellationSettings: () => wait(cancellationSettings),

  updateCancellationSettings: async (next) => {
    cancellationSettings = { ...next };
    return wait(cancellationSettings);
  },

  cancelAppointment: async (id) => {
    cancelled.add(id);
    // Mock: політика ≤12 год → 50% повернення (рахує backend).
    const price = ["m", "a", "o"].flatMap(appointmentsFor).find((a) => a.id === id)?.priceFinal ?? 0;
    return wait({ refundAmount: Math.round(price * 0.5) });
  },

  getClients: () => wait(CLIENTS.map(({ id, name, meta, tag }) => ({ id, name, meta, tag }))),

  getClient: (id) => {
    const c = CLIENTS.find((x) => x.id === id);
    if (!c) return wait(null);
    return wait({
      id: c.id,
      name: c.name,
      tag: c.tag,
      ...c.profile,
      notes: [...(addedNotes[id] ?? []), ...c.profile.notes],
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
      draft.locationIds.length === LOCATIONS.length
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
