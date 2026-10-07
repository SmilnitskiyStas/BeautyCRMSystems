import { money } from "../format";
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
  STAFF,
  appointmentsFor,
  staffProfile,
} from "./mock-data";
import type {
  AiChat,
  AiChatMessage,
  AiPromoPlan,
  CancellationSettings,
  ChannelConfig,
  ClientNote,
  LocationId,
  Overview,
  PromoDraft,
  PromoGoalId,
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

  getStaff: () => wait(STAFF),
  getStaffProfile: (id) => wait(staffProfile(id)),
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
