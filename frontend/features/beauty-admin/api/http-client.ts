import { BeautyApiError } from "@/features/beauty-auth/errors";
import { apiJson, getUser } from "@/features/beauty-auth/session";
import { mastersForCalendar } from "../calendar-masters";
import { dateTimeLabel, money, weekDayLabels, weekRangeLabel } from "../format";
import { isLinkActive } from "../staff-links";
import { safeMask } from "../phone";
import { normalizeHours } from "../working-hours";
import { buildWeekAvailability } from "../../beauty-shared/availability";
import { instantToLocal, localToUtcIso, weekInZone } from "../../beauty-shared/zone-time";
import type {
  AiCatalog,
  AiKeyInfo,
  AiKeySaved,
  AiRoleId,
  AiSettings,
  AiUsage,
  AiRequest,
  Appointment,
  AppointmentReminders,
  ReminderPoint,
  AppointmentStatus,
  BeautyLocation,
  CalendarKind,
  CancelledBy,
  CancellationSettings,
  ChannelConfig,
  LocationClosure,
  Weekday,
  ChannelId,
  AnalyticsDetails,
  AnalyticsDetailsQuery,
  CallTask,
  CallTaskList,
  ClientPhoneInfo,
  ConversationDetail,
  ConversationList,
  ConversationSummary,
  DetailsItem,
  DetailsLocationRow,
  DetailsSummary,
  FinanceFields,
  PhonePendingResult,
  PhoneSource,
  PhonePendingSource,
  ServiceCost,
  ServiceCostDetail,
  ServiceCostRow,
  ClientProfile,
  ClientSummary,
  ClientTag,
  LocationBreakdown,
  LocationId,
  LocationMetrics,
  LocationStat,
  NetworkSeries,
  NetworkTotals,
  Overview,
  PriceListRow,
  PromoDraft,
  Absence,
  AbsenceConflict,
  AbsenceResult,
  AbsenceStatus,
  AbsenceType,
  ServiceItem,
  StaffLocation,
  StaffMember,
  WorkingHours,
} from "../types";
import type { BeautyAdminApi } from "./client";
import { DEFAULT_GREETING, INITIAL_CHANNELS } from "./mock-data";
import { httpMarketingApi, toAppointmentCoupon } from "./http-marketing";
import { maskCouponCodes } from "../marketing/logic";

/**
 * Реальний HTTP-клієнт за `.claude/docs/beauty-contracts.md` §9–§13; форми DTO звірені з контролерами/моделями backend (TASK-693).
 * Помилки приходять як `BeautyApiError` (`{code,message}` + статус) — тексти формує `humanizeError`.
 * Те, чого backend ще не віддає (список спеціалістів, чат AI, план акції) — див. `unsupported()`.
 */

const B = "/api/beauty";
const get = <T>(path: string) => apiJson<T>(`${B}${path}`);
const send = <T>(method: "POST" | "PUT" | "PATCH", path: string, body?: unknown) =>
  apiJson<T>(`${B}${path}`, { method, body: body === undefined ? undefined : JSON.stringify(body) });

const unsupported = (what: string): never => {
  throw new BeautyApiError(501, "not_supported", `${what}: not implemented on backend`);
};

// ---------- DTO backend ----------

interface AppointmentDto {
  id: string;
  locationId: string;
  locationName: string;
  specialistId: string;
  specialistName: string;
  serviceId: string;
  serviceName: string;
  clientId: string;
  clientName: string;
  startsAt: string;
  endsAt: string;
  durationMinutes: number;
  status: string;
  source: string;
  priceOriginal: number;
  priceFinal: number;
  promotionId: string | null;
  cancellation?: CancellationSettings | null;
  /** IANA-зона закладу (§14.5). */
  timezone?: string;
  /** §16: лише для скасованих; `cancelledBy.name` - тільки staff і тільки керівникам; `cancelReason` - лише керівникам. */
  cancelledAt?: string | null;
  cancelledBy?: { type?: string; name?: string | null } | null;
  cancelReason?: string | null;
  /** §23.10: UTC-момент підтвердження візиту клієнтом кнопкою; відсутнє, якщо NULL. */
  clientConfirmedAt?: string | null;
  /** §22.6.1: нагадування запису (staff). */
  reminders?: { method?: string; offsetsMinutes?: number[]; points?: ReminderPointDto[] } | null;
  /** §26.11.4: запис за купоном (без повного коду) і розсилка-джерело. */
  coupon?: unknown;
  campaignId?: string | null;
}
interface ReminderPointDto {
  kind?: string;
  offsetMinutes?: number;
  dueAt?: string;
  status?: string;
  skipReason?: string | null;
}
/** `GET /overview` (§14.5): доба в зоні закладу, усі статуси записів, KPI. */
interface OverviewResponse {
  date: string;
  timezone: string;
  appointments: AppointmentDto[];
  kpi: { appointmentsCount: number; revenue: number; newClients: number; freeSlots: number };
}
interface LocationDto {
  id: string;
  name: string;
  address?: string | null;
  phone?: string | null;
  timezone: string;
  isActive: boolean;
  /** §17: підмножина mon..sun. */
  closedWeekdays?: string[] | null;
  /** §18.1: формат §9; `null` = не задано. */
  workingHours?: WorkingHours | null;
}
/** §17: закриття закладу; `reason` лише керівникам. */
interface ClosureDto {
  id: string;
  locationId?: string;
  dateFrom: string;
  dateTo: string;
  reason?: string | null;
}
interface ClientDto {
  id: string;
  fullName: string;
  phone: string | null;
  email: string | null;
  visits: number;
  lastVisitAt: string | null;
  /** §16 (звірено з ClientDto backend): зведення скасувань - усі / ініційовані клієнтом. */
  cancelledCount?: number;
  cancelledByClientCount?: number;
}
interface ClientPhoneDto {
  phoneSource?: string | null;
  callConsentAt?: string | null;
  phonePending?: { phone?: string; source?: string; at?: string } | null;
  possibleDuplicate?: { id?: string; name?: string } | null;
}
interface ClientDetailDto extends ClientPhoneDto {
  client: ClientDto & ClientPhoneDto;
  notes: { id: string; body: string; createdAt: string }[];
  history: AppointmentDto[];
}
interface ServiceDto {
  id: string;
  name: string;
  description: string | null;
  category: string | null;
  durationMinutes: number;
  isActive: boolean;
  networkPrice: number | null;
}
interface PromotionDto {
  id: string;
  name: string;
  startsAt: string | null;
  endsAt: string | null;
  locationIds: string[];
}
interface NetworkTotalsDto extends FinanceFields {
  appointments: number;
  completed: number;
  cancelled: number;
  noShow: number;
  revenue: number;
  averageCheck: number;
  cancellationRate: number;
  newClients: number;
}
interface NetworkDto extends NetworkTotalsDto {
  fromDate?: string;
  toDate?: string;
  timezone?: string;
  previous?: (NetworkTotalsDto & { fromDate?: string; toDate?: string }) | null;
}
interface LocationMetricsDto extends Omit<FinanceFields, "revenueWithoutCost"> {
  appointments: number;
  completed: number;
  cancelled: number;
  noShow?: number;
  revenue: number;
  busyMinutes?: number;
  availableMinutes?: number;
  load?: number | null;
}
interface LocationStatDto extends LocationMetricsDto {
  locationId: string;
  name: string;
  timezone?: string;
  isActive?: boolean;
  previous?: LocationMetricsDto | null;
}
interface PromoStatDto {
  promotionId: string;
  name: string;
  uses: number;
  discountTotal: number;
  revenue: number;
  previous?: { uses: number; discountTotal: number; revenue: number } | null;
}
interface ChannelDto {
  id: string;
  type: string;
  name: string;
  locationId: string | null;
  isActive: boolean;
  maskedToken: string;
  settingsJson: string | null;
}
interface AiActionDto {
  id: string;
  action: string;
  target: string;
  createdAt: string;
  status: string;
}

// ---------- ШІ (§20.5): явна вибірка полів - секретів у відповіді бути не може, а зайве відкидається ----------

const ROLE_IDS: AiRoleId[] = ["chat", "analytics"];
const toRoles = (v: unknown): AiRoleId[] => (Array.isArray(v) ? ROLE_IDS.filter((r) => v.includes(r)) : []);
const toKeyInfo = (k: AiKeyInfo): AiKeyInfo => ({
  provider: k.provider,
  hasKey: !!k.hasKey,
  last4: k.last4 ?? null,
  updatedAt: k.updatedAt ?? null,
  updatedByName: k.updatedByName ?? null,
  usedByRoles: toRoles(k.usedByRoles),
});
function toAiSettings(d: AiSettings): AiSettings {
  return {
    keySource: d.keySource,
    chat: d.chat,
    analytics: d.analytics,
    mode: d.mode,
    promptAddendum: d.promptAddendum ?? "",
    isDefaultPrompt: d.isDefaultPrompt,
    defaultPromptAddendum: d.defaultPromptAddendum ?? "",
    promptAddendumMaxLength: d.promptAddendumMaxLength ?? 4000,
    limits: d.limits,
    platformQuota: d.platformQuota,
    status: d.status,
    // Для admin властивості `keys` немає взагалі: не підміняємо її порожнім масивом.
    ...(Array.isArray(d.keys) ? { keys: d.keys.map(toKeyInfo) } : {}),
    updatedAt: d.updatedAt ?? null,
    updatedByName: d.updatedByName ?? null,
  };
}
const NO_STORE = { cache: "no-store" } as const;

// ---------- §24: фінанси, §25: телефон (явна вибірка полів: зайве, зокрема ПД клієнта, відкидається) ----------

const num = (v: unknown): number | undefined => (typeof v === "number" && Number.isFinite(v) ? v : undefined);
/** Лише відомі числові фінансові поля; відсутні лишаються відсутніми (UI тоді не показує фінанси). */
function pickFinance(d: FinanceFields, withRevenueWithoutCost = true): FinanceFields {
  const out: FinanceFields = {};
  for (const k of ["cost", "masterPayout", "netIncome", "withoutCost"] as const) {
    const v = num(d[k]);
    if (v !== undefined) out[k] = v;
  }
  if (d.marginPercent === null || num(d.marginPercent) !== undefined) out.marginPercent = d.marginPercent;
  const r = num(d.revenueWithoutCost);
  if (withRevenueWithoutCost && r !== undefined) out.revenueWithoutCost = r;
  return out;
}

const SOURCES: PhoneSource[] = ["telegram_contact", "manual_chat", "web_form", "staff"];
const PENDING_SOURCES: PhonePendingSource[] = ["telegram_contact", "manual_chat"];

function toPhoneInfo(phone: string | null, d: ClientPhoneDto): ClientPhoneInfo {
  const src = SOURCES.find((x) => x === d.phoneSource);
  const p = d.phonePending;
  const pendingSource = PENDING_SOURCES.find((x) => x === p?.source);
  return {
    phone: phone || null,
    ...(src ? { phoneSource: src } : {}),
    ...(d.callConsentAt ? { callConsentAt: d.callConsentAt } : {}),
    ...(p && typeof p.phone === "string" && p.phone ? { phonePending: { phone: p.phone, source: pendingSource ?? "manual_chat", at: p.at ?? "" } } : {}),
    ...(d.possibleDuplicate?.id ? { possibleDuplicate: { id: d.possibleDuplicate.id, name: d.possibleDuplicate.name ?? "" } } : {}),
  };
}

function toDetails(d: AnalyticsDetails): AnalyticsDetails {
  const s = d.summary;
  const summary: DetailsSummary = {
    appointments: s.appointments, completed: s.completed, cancelled: s.cancelled, noShow: s.noShow, revenue: s.revenue, discountTotal: s.discountTotal,
    cost: s.cost, masterPayout: s.masterPayout, netIncome: s.netIncome, marginPercent: s.marginPercent ?? null, withoutCost: s.withoutCost, revenueWithoutCost: s.revenueWithoutCost,
  };
  const locations = (d.locations ?? []).map<DetailsLocationRow>((l) => ({
    locationId: l.locationId, name: l.name, timezone: l.timezone, completed: l.completed, revenue: l.revenue, cost: l.cost, masterPayout: l.masterPayout,
    netIncome: l.netIncome, marginPercent: l.marginPercent ?? null, withoutCost: l.withoutCost,
  }));
  // Записи: лише поля контракту §24.6.3. Імені, телефону, clientId клієнта тут не буде, навіть якщо сервер їх помилково віддасть.
  const items = (d.items ?? []).map<DetailsItem>((i) => ({
    appointmentId: i.appointmentId, localDate: i.localDate, localTime: i.localTime,
    location: { id: i.location.id, name: i.location.name }, service: { id: i.service.id, name: i.service.name }, specialist: { id: i.specialist.id, name: i.specialist.name },
    priceOriginal: i.priceOriginal, discount: i.discount, ...(i.promotion ? { promotion: { id: i.promotion.id, name: i.promotion.name } } : {}),
    priceFinal: i.priceFinal, cost: i.costMissing ? null : (i.cost ?? null), masterPercent: i.costMissing ? null : (i.masterPercent ?? null),
    masterPayout: i.costMissing ? null : (i.masterPayout ?? null), net: i.net, costMissing: !!i.costMissing,
  }));
  return {
    fromDate: d.fromDate, toDate: d.toDate, timezone: d.timezone,
    ...(d.location ? { location: { id: d.location.id, name: d.location.name, timezone: d.location.timezone } } : {}),
    summary, locations, items, hasMore: !!d.hasMore, ...(d.hasMore && d.nextCursor ? { nextCursor: d.nextCursor } : {}),
  };
}

const toCost = (c: ServiceCost): ServiceCost => ({
  costAmount: c.costAmount, masterPercent: c.masterPercent ?? null, updatedAt: c.updatedAt, ...(c.updatedBy ? { updatedBy: { id: c.updatedBy.id, name: c.updatedBy.name } } : {}),
});
const toCostDetail = (d: ServiceCostDetail): ServiceCostDetail => ({
  serviceId: d.serviceId,
  ...(d.network ? { network: toCost(d.network) } : {}),
  locations: (d.locations ?? []).map((l) => ({ ...toCost(l), locationId: l.locationId, locationName: l.locationName })),
});

/** `client.phonePending` задачі дзвінка: рядок (повний номер) або `{phone}`; усе інше відкидається. */
function toCallTasks(d: CallTaskList): CallTaskList {
  if (!d || !Array.isArray(d.items)) return d;
  return {
    ...d,
    items: (d.items ?? []).map<CallTask>((t) => {
      const raw = t.client as CallTask["client"] & { phonePending?: unknown };
      const pending = typeof raw.phonePending === "string" ? raw.phonePending : (raw.phonePending as { phone?: string } | null | undefined)?.phone;
      return { ...t, client: { id: raw.id, name: raw.name, ...(raw.phone ? { phone: raw.phone } : {}), ...(pending ? { phonePending: pending } : {}) } };
    }),
  };
}

/** Клієнт розмови: лише маска телефону (перемаскована, якщо сервер помилково віддав більше) і прапорець pending. */
function toSummary(c: ConversationSummary): ConversationSummary {
  if (!c?.client) return c;
  const mask = safeMask(c.client.phoneMasked);
  return { ...c, client: { id: c.client.id, name: c.client.name, ...(mask ? { phoneMasked: mask } : {}), ...(c.client.phonePending ? { phonePending: true } : {}) } };
}
const aiKeyPath = (provider: string) => `/ai/settings/keys/${encodeURIComponent(provider)}`;

// ---------- дати ----------

const dayLabel = (iso: string) =>
  new Date(`${iso.slice(0, 10)}T00:00:00`).toLocaleDateString("uk-UA", { day: "numeric", month: "long" });
const addDays = (d: Date, n: number) => new Date(d.getFullYear(), d.getMonth(), d.getDate() + n);
const range = (from: Date, to: Date) => `from=${encodeURIComponent(from.toISOString())}&to=${encodeURIComponent(to.toISOString())}`;
/** Датний режим (§19.1): `from`/`to` = `yyyy-MM-dd`, `compare=true` лише на запит. Legacy-режим із часом не використовується. */
const rangeQuery = (q: { from: string; to: string; compare?: boolean }) =>
  `from=${encodeURIComponent(q.from)}&to=${encodeURIComponent(q.to)}${q.compare ? "&compare=true" : ""}`;

function toLocationMetrics(l: LocationMetricsDto): LocationMetrics {
  return {
    appointments: l.appointments,
    completed: l.completed,
    cancelled: l.cancelled,
    noShow: l.noShow ?? 0,
    revenue: l.revenue,
    busyMinutes: l.busyMinutes ?? 0,
    availableMinutes: l.availableMinutes ?? 0,
    load: l.load ?? null, // §19.6: відсутній `load` = null
    ...pickFinance(l, false),
  };
}
const toLocationStat = (l: LocationStatDto, compare: boolean): LocationStat => ({
  ...toLocationMetrics(l),
  locationId: l.locationId,
  name: l.name,
  timezone: l.timezone ?? "UTC",
  isActive: l.isActive ?? true,
  previous: compare && l.previous ? toLocationMetrics(l.previous) : null,
});

/**
 * Зона закладу кожного завантаженого запису: backend віддає `startsAt` у UTC (F2), тож для показу переводимо момент у
 * зону закладу, а при переносі збираємо UTC-момент із локального часу саме цієї зони.
 */
const zones = new Map<string, string>();

// ---------- мапінг ----------

const STATUSES: AppointmentStatus[] = ["pending", "confirmed", "in_progress", "completed", "cancelled", "no_show"];
const statusOf = (s: string): AppointmentStatus => (STATUSES as string[]).includes(s) ? (s as AppointmentStatus) : "pending";

function kindOf(a: AppointmentDto): CalendarKind {
  if (a.promotionId) return "promo";
  if (a.source !== "admin") return "online";
  return "visit";
}

function toCancelledBy(a: AppointmentDto): CancelledBy | undefined {
  const t = a.cancelledBy?.type;
  if (t !== "client" && t !== "staff" && t !== "system") return undefined;
  const name = t === "staff" ? a.cancelledBy?.name?.trim() : undefined;
  return name ? { type: t, name } : { type: t };
}

/** §23.10: у повідомлення беремо лише відомі поля; `buttons` - рядки, `pressedLabel` - рядок. Нічого службового в UI не потрапляє. */
function toConversationDetail(d: ConversationDetail): ConversationDetail {
  if (!d || typeof d !== "object") return d;
  return {
    ...d,
    conversation: toSummary(d.conversation) as ConversationDetail["conversation"],
    messages: (d.messages ?? []).map((m) => {
      const x = m as ConversationDetail["messages"][number];
      const labels = Array.isArray(x.buttons) ? x.buttons.filter((b): b is string => typeof b === "string" && b.trim() !== "").slice(0, 10) : [];
      return {
        id: x.id,
        direction: x.direction,
        senderType: x.senderType,
        ...(x.senderUser ? { senderUser: x.senderUser } : {}),
        kind: x.kind,
        // §26.9.3: код купона в тілі повідомлення не показуємо (лише останні 3 символи).
        body: typeof x.body === "string" ? maskCouponCodes(x.body) : x.body,
        status: x.status,
        ...(x.failureCode ? { failureCode: x.failureCode } : {}),
        ...(x.sentAt ? { sentAt: x.sentAt } : {}),
        createdAt: x.createdAt,
        ...(labels.length > 0 ? { buttons: labels } : {}),
        ...(typeof x.pressedLabel === "string" && x.pressedLabel ? { pressedLabel: x.pressedLabel } : {}),
        ...(x.kind === "contact" && ["own", "foreign", "invalid"].includes(x.contactVerdict as string) ? { contactVerdict: x.contactVerdict } : {}),
      };
    }),
    upcomingAppointments: (d.upcomingAppointments ?? []).map((a) => {
      const { clientConfirmedAt, ...rest } = a;
      return { ...rest, ...(typeof clientConfirmedAt === "string" && clientConfirmedAt ? { clientConfirmedAt } : {}) };
    }),
  };
}

const WEEKDAY_KEYS: Weekday[] = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];
const toWeekdays = (v: string[] | null | undefined): Weekday[] =>
  WEEKDAY_KEYS.filter((k) => (v ?? []).includes(k));

const toClosure = (c: ClosureDto, locationId: string): LocationClosure => ({
  id: c.id,
  locationId: c.locationId ?? locationId,
  dateFrom: c.dateFrom.slice(0, 10),
  dateTo: c.dateTo.slice(0, 10),
  ...(typeof c.reason === "string" && c.reason.trim() ? { reason: c.reason } : {}),
});

const toLocation = (l: LocationDto): BeautyLocation => ({
  id: l.id,
  closedWeekdays: toWeekdays(l.closedWeekdays),
  workingHours: l.workingHours && typeof l.workingHours === "object" ? normalizeHours(l.workingHours) : null,
  name: l.name,
  address: l.address ?? null,
  phone: l.phone ?? null,
  timezone: l.timezone,
  isActive: l.isActive,
});

const REMINDER_METHODS = ["none", "telegram", "call", "auto"] as const;
function toReminders(r: NonNullable<AppointmentDto["reminders"]>): AppointmentReminders {
  const method = (REMINDER_METHODS as readonly string[]).includes(r.method ?? "") ? (r.method as AppointmentReminders["method"]) : "none";
  const points = (r.points ?? [])
    .filter((p) => typeof p.offsetMinutes === "number" && typeof p.dueAt === "string")
    .map<ReminderPoint>((p) => ({
      kind: p.kind === "call" ? "call" : "message",
      offsetMinutes: p.offsetMinutes as number,
      dueAt: p.dueAt as string,
      status: p.status ?? "scheduled",
      ...(p.skipReason ? { skipReason: p.skipReason } : {}),
    }));
  return { method, offsetsMinutes: r.offsetsMinutes ?? [], ...(r.points ? { points } : {}) };
}

function toAppointment(a: AppointmentDto, fallbackZone?: string): Appointment {
  const zone = a.timezone ?? fallbackZone;
  if (zone) zones.set(a.id, zone);
  return {
    id: a.id,
    locationId: a.locationId,
    specialistId: a.specialistId,
    serviceId: a.serviceId,
    clientId: a.clientId,
    clientName: a.clientName,
    serviceName: a.serviceName,
    startsAt: instantToLocal(a.startsAt, zone),
    durationMinutes: a.durationMinutes,
    status: statusOf(a.status),
    source: (["admin", "online", "telegram", "instagram"].includes(a.source) ? a.source : "admin") as Appointment["source"],
    kind: kindOf(a),
    priceFinal: a.priceFinal,
    promotionId: a.promotionId,
    promotionName: a.promotionId ? "Акція" : undefined,
    cancellation: a.cancellation ?? undefined,
    ...(a.cancelledAt ? { cancelledAt: a.cancelledAt } : {}),
    ...(toCancelledBy(a) ? { cancelledBy: toCancelledBy(a) } : {}),
    ...(a.cancelReason?.trim() ? { cancelReason: a.cancelReason.trim() } : {}),
    ...(a.reminders ? { reminders: toReminders(a.reminders) } : {}),
    ...(typeof a.clientConfirmedAt === "string" && a.clientConfirmedAt ? { clientConfirmedAt: a.clientConfirmedAt } : {}),
    ...(toAppointmentCoupon(a.coupon) ? { coupon: toAppointmentCoupon(a.coupon) } : {}),
    ...(typeof a.campaignId === "string" && a.campaignId ? { campaignId: a.campaignId } : {}),
  };
}

function clientTag(c: ClientDto): ClientTag {
  if (c.visits >= 10) return "vip";
  if (c.visits <= 1) return "new";
  const last = c.lastVisitAt ? Date.parse(c.lastVisitAt) : 0;
  return last && Date.now() - last > 60 * 86_400_000 ? "sleep" : "vip";
}

const clientSummary = (c: ClientDto): ClientSummary => ({
  id: c.id,
  name: c.fullName,
  meta: c.lastVisitAt ? `Візитів: ${c.visits} · останній ${dayLabel(c.lastVisitAt)}` : "Ще не було візитів",
  tag: clientTag(c),
});

/** «5 жовтня, 10:00» у зоні закладу запису (F2: `startsAt` приходить у UTC). */
function historyLabel(a: AppointmentDto): string {
  const local = instantToLocal(a.startsAt, a.timezone);
  return `${dayLabel(local)}, ${local.slice(11, 16)}`;
}

const TYPE_BY_ID: Record<ChannelId, string> = {
  tg: "telegram",
  ig: "instagram",
  fb: "facebook",
  wa: "whatsapp",
  vb: "viber",
  web: "widget",
};

type ChannelSettings = Partial<Pick<ChannelConfig, "inbox" | "booking" | "ai" | "mode" | "greeting" | "handoff">> & {
  /** §23.2: `{ enabled, buttons }`; решту ключів зберігаємо як є (PUT перезаписує `settingsJson` цілком). */
  assistant?: { enabled?: unknown; buttons?: unknown; [k: string]: unknown };
  [k: string]: unknown;
};

function parseSettings(json: string | null | undefined): ChannelSettings {
  if (!json) return {};
  try {
    const v: unknown = JSON.parse(json);
    return v && typeof v === "object" && !Array.isArray(v) ? (v as ChannelSettings) : {};
  } catch {
    return {};
  }
}

function toChannel(id: ChannelId, row?: ChannelDto): ChannelConfig {
  const base = INITIAL_CHANNELS.find((c) => c.id === id)!;
  const s = parseSettings(row?.settingsJson);
  const connected = !!row && row.isActive && row.maskedToken !== "";
  const assistant = s.assistant && typeof s.assistant === "object" && !Array.isArray(s.assistant) ? s.assistant : undefined;
  return {
    ...base,
    connected,
    maskedSecret: row?.maskedToken ? row.maskedToken : null,
    inbox: s.inbox ?? base.inbox,
    booking: s.booking ?? base.booking,
    ai: s.ai ?? base.ai,
    mode: s.mode ?? base.mode,
    greeting: s.greeting ?? DEFAULT_GREETING,
    handoff: { ...base.handoff, ...s.handoff },
    assistantEnabled: assistant?.enabled === true,
    // Відсутнє `buttons` = true (§23.2).
    buttons: assistant?.buttons !== false,
  };
}

async function loadChannels(): Promise<{ rows: ChannelDto[]; list: ChannelConfig[] }> {
  const rows = await get<ChannelDto[]>("/channels");
  const list = INITIAL_CHANNELS.map((c) => toChannel(c.id, rows.find((r) => r.type === TYPE_BY_ID[c.id])));
  return { rows, list };
}

async function saveChannel(
  id: ChannelId,
  change: { settings?: ChannelSettings; isActive?: boolean; token?: string; buttons?: boolean },
): Promise<ChannelConfig> {
  const { rows } = await loadChannels();
  const row = rows.find((r) => r.type === TYPE_BY_ID[id]);
  const current = toChannel(id, row);
  // Незнайомі ключі (зокрема `assistant.enabled`) не губимо: беремо наявний JSON і накладаємо зміни.
  const raw = parseSettings(row?.settingsJson);
  const settings: ChannelSettings = {
    ...raw,
    inbox: current.inbox,
    booking: current.booking,
    ai: current.ai,
    mode: current.mode,
    greeting: current.greeting,
    handoff: current.handoff,
    ...change.settings,
  };
  if (change.buttons !== undefined) {
    const prev = raw.assistant && typeof raw.assistant === "object" && !Array.isArray(raw.assistant) ? raw.assistant : {};
    settings.assistant = { ...prev, buttons: change.buttons };
  }
  const saved = await send<ChannelDto>("PUT", `/channels/${row?.id ?? crypto.randomUUID()}`, {
    type: TYPE_BY_ID[id],
    name: current.name,
    locationId: row?.locationId ?? null,
    isActive: change.isActive ?? row?.isActive ?? false,
    token: change.token ?? null,
    webhookSecret: null,
    settingsJson: JSON.stringify(settings),
  });
  return toChannel(id, saved);
}

const parseDraftDate = (d: string, endOfDay: boolean) => (d ? `${d}T${endOfDay ? "23:59:59" : "00:00:00"}Z` : null);

async function weeklyAppointments(from: Date, to: Date, extra = ""): Promise<AppointmentDto[]> {
  return get<AppointmentDto[]>(`/appointments?${range(from, to)}${extra}`);
}

// ---------- працівники й відсутність (§13, v0.6) ----------
// Форми точно за `StaffModels.cs` (SpecialistDto, AbsenceDto, AbsenceConflictDto).

interface SpecialistDto {
  id: string;
  name: string;
  /** Відсутнє для ролі specialist (і коли порожнє): `JsonIgnore(WhenWritingNull)`. */
  phone?: string;
  position: string | null;
  photoUrl: string | null;
  isActive: boolean;
  hasAccount?: boolean;
  services: { id: string; name: string }[];
  locations: { locationId: string; locationName: string; isActive: boolean; workingHours: WorkingHours | null }[];
}
interface AbsenceDto {
  id: string;
  specialistId: string;
  type: AbsenceType;
  /** `yyyy-MM-dd` (DateOnly). */
  dateFrom: string;
  dateTo: string;
  status: AbsenceStatus;
  /** Присутнє лише для owner/admin і автора (`WhenWritingNull`) — не припускаємо його наявності. */
  note?: string;
  /** Лише для керівників при створенні та `approve`. */
  conflicts?: ConflictDto[];
}
interface ConflictDto {
  appointmentId: string;
  /** DateTimeOffset: може бути в UTC, тому НЕ обрізаємо - форматуємо як момент часу (див. dateTimeLabel). */
  startsAt: string;
  /** Зона закладу, якщо backend її додасть (TASK-689); наразі поля немає. */
  timezone?: string;
  serviceName: string;
}

/** InviteDto (AuthModels.cs); `status`: pending | accepted | revoked | expired. Шлях - `/api/invites`, не `/api/beauty`. */
interface InviteDto {
  id: string;
  email: string;
  role: string;
  specialistId: string | null;
  expiresAt: string;
  status: string;
}

async function loadServices(): Promise<ServiceItem[]> {
  // `GET /services` без `includeInactive` віддає лише активні.
  const rows = await get<ServiceDto[]>("/services");
  return rows.map((s) => ({ id: s.id, name: s.name, category: s.category, durationMinutes: s.durationMinutes }));
}

const toStaff = (r: SpecialistDto): StaffMember => ({
  id: r.id,
  name: r.name,
  phone: r.phone ?? null,
  position: r.position,
  isActive: r.isActive,
  services: r.services.map((x) => ({ id: x.id, name: x.name })),
  locations: r.locations.map<StaffLocation>((l) => ({
    locationId: l.locationId,
    locationName: l.locationName,
    workingHours: l.workingHours ?? {},
    // F5: знятий зв'язок (`isActive=false`) не відкидаємо - інакше він виглядає як чинний заклад майстра.
    isActive: l.isActive !== false,
  })),
});

const toAbsence = (a: AbsenceDto): Absence => ({
  id: a.id,
  specialistId: a.specialistId,
  type: a.type,
  dateFrom: a.dateFrom,
  dateTo: a.dateTo,
  status: a.status,
  ...(typeof a.note === "string" ? { note: a.note } : {}),
});

const toAbsenceResult = (a: AbsenceDto): AbsenceResult => ({
  absence: toAbsence(a),
  conflicts: (a.conflicts ?? []).map<AbsenceConflict>((c) => ({
    appointmentId: c.appointmentId,
    startsAt: c.startsAt,
    timezone: c.timezone,
    serviceName: c.serviceName,
  })),
});

/** Закриття закладу на тиждень (§17). Збій не ламає календар, але про нього треба повідомити (F9): `failed`. */
async function loadClosures(locationId: string, weekStart: string, weekEnd: string): Promise<{ list: LocationClosure[]; failed: boolean }> {
  try {
    const rows = await get<ClosureDto[]>(`/locations/${encodeURIComponent(locationId)}/closures?from=${weekStart}&to=${weekEnd}`);
    return { list: rows.map((c) => toClosure(c, locationId)), failed: false };
  } catch {
    return { list: [], failed: true };
  }
}

export const httpBeautyApi: BeautyAdminApi = {
  ...httpMarketingApi,
  // `GET /locations` (§14.5/§16) доступний усім ролям; без `includeInactive` віддає лише активні.
  getLocations: async () => (await get<LocationDto[]>("/locations")).map(toLocation),

  getManagedLocations: async () => (await get<LocationDto[]>("/locations?includeInactive=true")).map(toLocation),

  createLocation: async (input) =>
    toLocation(
      await send<LocationDto>("POST", "/locations", {
        name: input.name.trim(),
        address: input.address.trim() || null,
        phone: input.phone.trim() || null,
        timezone: input.timezone.trim(),
      }),
    ),

  updateLocation: async (id, input) =>
    toLocation(
      await send<LocationDto>("PUT", `/locations/${encodeURIComponent(id)}`, {
        name: input.name.trim(),
        address: input.address.trim() || null,
        phone: input.phone.trim() || null,
        timezone: input.timezone.trim(),
        isActive: input.isActive,
      }),
    ),

  setClosedWeekdays: async (id, weekdays, confirm) => {
    const r = await send<LocationDto | undefined>("PUT", `/locations/${encodeURIComponent(id)}/closed-weekdays`, {
      closedWeekdays: weekdays,
      ...(confirm ? { confirm: true } : {}),
    });
    return Array.isArray(r?.closedWeekdays) ? toWeekdays(r.closedWeekdays) : weekdays;
  },

  setLocationWorkingHours: async (id, workingHours, confirm) => {
    const r = await send<LocationDto | undefined>("PUT", `/locations/${encodeURIComponent(id)}/working-hours`, {
      workingHours: workingHours ? normalizeHours(workingHours) : null,
      ...(confirm ? { confirm: true } : {}),
    });
    if (r && "workingHours" in r) return r.workingHours ? normalizeHours(r.workingHours) : null;
    return workingHours ? normalizeHours(workingHours) : null;
  },

  getClosures: async (id, { from, to }) =>
    (await get<ClosureDto[]>(`/locations/${encodeURIComponent(id)}/closures?from=${from}&to=${to}`)).map((c) => toClosure(c, id)),

  addClosure: async (id, input) =>
    toClosure(
      await send<ClosureDto>("POST", `/locations/${encodeURIComponent(id)}/closures`, {
        dateFrom: input.dateFrom,
        dateTo: input.dateTo,
        reason: input.reason?.trim() ? input.reason.trim() : null,
        ...(input.confirm ? { confirm: true } : {}),
      }),
      id,
    ),

  deleteClosure: async (id, closureId) => {
    await apiJson<void>(`${B}/locations/${encodeURIComponent(id)}/closures/${encodeURIComponent(closureId)}`, { method: "DELETE" });
  },

  getOverview: async (locationId: LocationId | null) => {
    // F9: `GET /overview` (§14.5) віддає записи доби в зоні закладу й KPI з урахуванням годин/вихідних/відсутностей.
    // Доступний owner/admin - саме їм належить екран «Огляд» (specialist на нього не потрапляє, див. `canOpen`).
    const q = locationId ? `?locationId=${encodeURIComponent(locationId)}` : "";
    const [o, clients] = await Promise.all([get<OverviewResponse>(`/overview${q}`), get<ClientDto[]>("/clients?pageSize=6")]);
    const live = o.appointments.filter((a) => a.status !== "cancelled").sort((a, b) => Date.parse(a.startsAt) - Date.parse(b.startsAt));
    const completed = live.filter((a) => a.status === "completed").length;
    const overview: Overview = {
      dateLabel: new Date(`${o.date.slice(0, 10)}T00:00:00Z`).toLocaleDateString("uk-UA", { day: "numeric", month: "long", timeZone: "UTC" }),
      kpis: [
        { label: "Записів сьогодні", value: String(o.kpi.appointmentsCount), note: `завершено: ${completed}` },
        { label: "Виручка за день", value: money(o.kpi.revenue), note: "завершені візити" },
        { label: "Нові клієнти", value: String(o.kpi.newClients), note: "за добу" },
        { label: "Вільних слотів", value: String(o.kpi.freeSlots), note: "до кінця дня" },
      ],
      rows: live.map((a) => ({
        id: a.id,
        time: instantToLocal(a.startsAt, a.timezone ?? o.timezone).slice(11, 16),
        clientId: a.clientId,
        clientName: a.clientName,
        serviceName: a.serviceName,
        specialistId: a.specialistId,
        specialistName: a.specialistName,
        locationId: a.locationId,
        locationName: a.locationName,
        status: statusOf(a.status),
      })),
      clients: clients.map(clientSummary),
    };
    return overview;
  },

  getCalendarWeek: async (specialistId, includeCancelled = false, requestedLocationId) => {
    const [roster, known] = await Promise.all([get<SpecialistDto[]>("/specialists"), get<LocationDto[]>("/locations")]);
    const zoneOf = (id: string | undefined) => known.find((k) => k.id === id)?.timezone;
    const fetchWeek = (w: { from: Date; to: Date }, extra = "") => weeklyAppointments(w.from, w.to, extra);
    // Тиждень рахуємо у зоні закладу; спершу - у зоні першого закладу (майстер, а отже й заклад, ще не відомі).
    let week = weekInZone(known[0]?.timezone);
    // Без скасованих (дефолт API): за ними будуємо селектор майстрів; неактивним потрібні перенесення.
    let live = await fetchWeek(week);
    const me = getUser();
    const buildMasters = () =>
      mastersForCalendar(
        roster.map((r) => ({ id: r.id, name: r.name, isActive: r.isActive, locationNames: r.locations.filter(isLinkActive).map((l) => l.locationName) })),
        live.map((a) => ({ specialistId: a.specialistId, status: statusOf(a.status) })),
        me?.role === "specialist" ? me.specialistId : null,
      );
    let masters = buildMasters();
    const effective = masters.find((m) => m.id === specialistId)?.id ?? masters[0]?.id ?? specialistId;
    const master = roster.find((r) => r.id === effective);
    const links = master ? master.locations.filter(isLinkActive) : [];
    const chosen = links.find((l) => l.locationId === requestedLocationId) ?? links[0];
    const zone = zoneOf(chosen?.locationId) ?? known[0]?.timezone;
    const right = weekInZone(zone);
    if (right.monday !== week.monday || right.from.getTime() !== week.from.getTime()) {
      week = right;
      live = await fetchWeek(week);
      masters = buildMasters();
    }
    const shown = includeCancelled ? await fetchWeek(week, "&includeCancelled=true") : live;

    // Доступність: графік майстра в обраному закладі ∩ години закладу ∩ вихідні/закриття (§17, §18.1).
    let availability: ReturnType<typeof buildWeekAvailability> | null = null;
    let availabilityWarning = false;
    if (chosen) {
      const loc = known.find((k) => k.id === chosen.locationId);
      const closures = await loadClosures(chosen.locationId, week.monday, week.sunday);
      availabilityWarning = closures.failed || !loc;
      availability = buildWeekAvailability(week.monday, {
        masterHours: chosen.workingHours ?? {},
        locationHours: loc?.workingHours && typeof loc.workingHours === "object" ? normalizeHours(loc.workingHours) : null,
        closedWeekdays: toWeekdays(loc?.closedWeekdays),
        closures: closures.list,
      });
    }
    const mine = shown.filter((a) => a.specialistId === effective && (includeCancelled || a.status !== "cancelled"));
    const here = mine.filter((a) => !chosen || a.locationId === chosen.locationId);
    return {
      weekLabel: weekRangeLabel(week.monday),
      weekStart: week.monday,
      days: weekDayLabels(week.monday),
      masters,
      specialistId: effective,
      closedDays: availability?.closedDays ?? [null, null, null, null, null, null, null],
      appointments: here.map((a) => toAppointment(a, zoneOf(a.locationId) ?? zone)),
      locations: links.map((l) => ({ id: l.locationId, name: l.locationName })),
      ...(chosen ? { locationId: chosen.locationId } : {}),
      ...(availability
        ? { availability: availability.availability, gridStartHour: availability.gridStartHour, gridEndHour: availability.gridEndHour }
        : {}),
      ...(availabilityWarning ? { availabilityWarning: true } : {}),
      otherLocationsCount: mine.length - here.length,
    };
  },

  getUpcomingAppointmentsCount: async (specialistId) => {
    const from = new Date();
    const rows = await weeklyAppointments(from, addDays(from, 60), `&specialistId=${encodeURIComponent(specialistId)}`);
    return rows.filter((a) => a.specialistId === specialistId && (a.status === "pending" || a.status === "confirmed")).length;
  },

  cancelAppointment: async (id, reason) => {
    const text = reason?.trim();
    const r = await send<{ refundAmount: number }>("POST", `/appointments/${id}/cancel`, text ? { reason: text.slice(0, 300) } : undefined);
    return { refundAmount: r.refundAmount };
  },

  moveAppointment: async (id, startsAt) => {
    // `startsAt` - локальний час закладу; на backend йде UTC-момент, зібраний у зоні закладу саме цього запису.
    const local = startsAt.length === 16 ? `${startsAt}:00` : startsAt;
    await send("PATCH", `/appointments/${id}`, { startsAt: localToUtcIso(local, zones.get(id)) });
  },

  setAppointmentStatus: async (id, status) => {
    await send("PATCH", `/appointments/${id}`, { status });
  },

  getCancellationSettings: () => get<CancellationSettings>("/settings/cancellation"),
  updateCancellationSettings: (s) => send<CancellationSettings>("PUT", "/settings/cancellation", s),

  getClients: async () => (await get<ClientDto[]>("/clients?pageSize=200")).map(clientSummary),

  getClient: async (id) => {
    let d: ClientDetailDto;
    try {
      d = await get<ClientDetailDto>(`/clients/${id}`);
    } catch (e) {
      if (e instanceof BeautyApiError && e.status === 404) return null;
      throw e;
    }
    const done = d.history.filter((a) => a.status === "completed");
    const cancelledRows = d.history.filter((a) => a.status === "cancelled");
    const profile: ClientProfile = {
      id: d.client.id,
      name: d.client.fullName,
      tag: clientTag(d.client),
      contactLine: [d.client.phone, d.client.email].filter(Boolean).join(" · ") || "Контакти не вказано",
      kpis: [
        { label: "Візитів", value: String(d.client.visits), note: "завершених" },
        { label: "Витрачено", value: money(done.reduce((s, a) => s + a.priceFinal, 0)), note: "за весь час" },
        {
          label: "Останній візит",
          value: d.client.lastVisitAt ? dayLabel(d.client.lastVisitAt) : "—",
          note: "",
        },
      ],
      // Лічильники з `client` (ClientDto); запасний варіант - рахуємо з історії.
      cancelledCount: d.client.cancelledCount ?? cancelledRows.length,
      cancelledByClientCount:
        d.client.cancelledByClientCount ?? cancelledRows.filter((a) => a.cancelledBy?.type === "client").length,
      visits: d.history.map((a) => ({
        id: a.id,
        dateLabel: historyLabel(a),
        serviceName: a.serviceName,
        specialistId: a.specialistId,
        specialistName: a.specialistName,
        locationName: a.locationName,
        sum: money(a.priceFinal),
        status: a.status === "completed" ? "completed" : a.status === "cancelled" ? "cancelled" : a.status === "no_show" ? "no_show" : "planned",
        viaPromo: !!a.promotionId,
        ...(toAppointmentCoupon(a.coupon)
          ? { viaCoupon: true, couponName: toAppointmentCoupon(a.coupon)!.promotionName, couponDiscount: `−${money(Math.max(0, a.priceOriginal - a.priceFinal))}` }
          : {}),
        ...(a.status === "cancelled"
          ? {
              ...(a.cancelledAt ? { cancelledAtLabel: dateTimeLabel(a.cancelledAt, a.timezone) } : {}),
              ...(toCancelledBy(a) ? { cancelledBy: toCancelledBy(a) } : {}),
              ...(a.cancelReason?.trim() ? { cancelReason: a.cancelReason.trim() } : {}),
            }
          : {}),
      })),
      promos: d.history
        .filter((a) => a.promotionId && a.status === "completed")
        .map((a) => ({ id: a.id, name: a.serviceName, when: dayLabel(a.startsAt), saved: money(a.priceOriginal - a.priceFinal) })),
      notes: d.notes.map((n) => ({ id: n.id, text: n.body, meta: dayLabel(n.createdAt) })),
      preferences: [],
      warning: null,
      loyalty: { balance: "—", nextLevel: "Програма лояльності ще не підключена", progressPct: 0 },
      // §25.7: поля можуть бути у `client` або на верхньому рівні відповіді (контракт не уточнює) - беремо обидва.
      phoneInfo: toPhoneInfo(d.client.phone, { ...d, ...d.client } as ClientPhoneDto),
    };
    return profile;
  },

  addClientNote: async (id, text) => {
    await send("POST", `/clients/${id}/notes`, { body: text });
  },

  getStaff: async () => (await get<SpecialistDto[]>("/specialists")).map(toStaff),

  getStaffProfile: async (id) => {
    try {
      return toStaff(await get<SpecialistDto>(`/specialists/${id}`));
    } catch (e) {
      if (e instanceof BeautyApiError && e.status === 404) return null;
      throw e;
    }
  },

  getServices: () => loadServices(),

  createStaff: async (input) =>
    toStaff(
      await send<SpecialistDto>("POST", "/specialists", {
        name: input.name.trim(),
        phone: input.phone.trim() || null,
        position: input.position.trim() || null,
        locationIds: input.locationIds,
        serviceIds: input.serviceIds,
        workingHours: input.workingHours,
      }),
    ),

  updateStaff: async (id, input) => {
    await send<SpecialistDto>("PUT", `/specialists/${id}`, {
      name: input.name.trim(),
      phone: input.phone.trim() || null,
      position: input.position.trim() || null,
      isActive: input.isActive,
    });
  },

  setStaffServices: async (id, serviceIds) => {
    await send<SpecialistDto>("PUT", `/specialists/${id}/services`, { serviceIds });
  },

  setStaffSchedule: async (id, locationId, workingHours) => {
    await send<SpecialistDto>("PUT", `/specialists/${id}/schedule`, { locationId, workingHours });
  },

  getStaffInvites: async (staffId) => {
    const rows = await apiJson<InviteDto[]>("/api/invites");
    return rows
      .filter((i) => i.specialistId === staffId && i.status === "pending")
      .map((i) => ({ id: i.id, email: i.email, expiresAt: i.expiresAt }));
  },

  revokeInvite: async (inviteId) => {
    await apiJson<void>(`/api/invites/${encodeURIComponent(inviteId)}`, { method: "DELETE" });
  },

  inviteStaff: async (id, email) => {
    const r = await send<{ token: string }>("POST", `/specialists/${id}/invite`, { email: email.trim() });
    return { token: r.token };
  },

  getAbsences: async ({ from, to, specialistId }) => {
    const q = `from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}${specialistId ? `&specialistId=${encodeURIComponent(specialistId)}` : ""}`;
    return (await get<AbsenceDto[]>(`/absences?${q}`)).map(toAbsence);
  },

  createAbsence: async (specialistId, input) =>
    toAbsenceResult(
      await send<AbsenceDto>("POST", `/specialists/${specialistId}/absences`, {
        type: input.type,
        dateFrom: input.dateFrom,
        dateTo: input.dateTo,
        note: input.note?.trim() ? input.note.trim() : null,
      }),
    ),

  approveAbsence: async (id) => toAbsenceResult(await send<AbsenceDto>("POST", `/absences/${id}/approve`)),

  rejectAbsence: async (id) => {
    await send<AbsenceDto>("POST", `/absences/${id}/reject`);
  },

  cancelAbsence: async (id) => {
    await send<AbsenceDto>("POST", `/absences/${id}/cancel`);
  },

  getPriceList: async () => {
    const rows = await get<ServiceDto[]>("/services");
    return rows.map<PriceListRow>((s) => ({ serviceId: s.id, name: s.name, durationMinutes: s.durationMinutes, price: s.networkPrice ?? 0 }));
  },

  previewPromotion: async (draft: PromoDraft) => {
    // Бекенд рахує прев'ю лише для збереженої акції, тому чернетку рахуємо локально за мережевими цінами.
    const [prices, locations] = await Promise.all([httpBeautyApi.getPriceList(), httpBeautyApi.getLocations()]);
    const factor = (100 - draft.percent) / 100;
    const all = draft.locationIds.length === 0 || draft.locationIds.length === locations.length;
    const where = all
      ? "Усі заклади"
      : draft.locationIds.map((id) => locations.find((l) => l.id === id)?.name ?? id).join(", ");
    const base = prices[0]?.price ?? 0;
    return {
      rows: prices.map((s) => {
        const on = draft.serviceIds.includes(s.serviceId);
        return { ...s, promoPrice: on ? Math.round(s.price * factor) : null, where: on ? where : "Усі заклади" };
      }),
      locationSummary: where,
      samplePrice: Math.round(base * factor),
      sampleOriginal: base,
    };
  },

  createPromotion: async (draft) => {
    const r = await send<{ id: string }>("POST", "/promotions", {
      name: draft.name,
      description: null,
      discountType: "percent",
      discountValue: draft.percent,
      access: draft.access ?? "public",
      startsAt: parseDraftDate(draft.from, false),
      endsAt: parseDraftDate(draft.to, true),
      isActive: true,
      locationIds: draft.locationIds,
      serviceIds: draft.serviceIds,
    });
    return { id: r.id };
  },

  getNetworkAnalytics: async (q) => {
    const d = await get<NetworkDto>(`/analytics/network?${rangeQuery(q)}`);
    const totals = (t: NetworkTotalsDto): NetworkTotals => ({
      appointments: t.appointments,
      completed: t.completed,
      cancelled: t.cancelled,
      noShow: t.noShow,
      revenue: t.revenue,
      averageCheck: t.averageCheck,
      cancellationRate: t.cancellationRate,
      newClients: t.newClients,
      ...pickFinance(t),
    });
    return {
      fromDate: d.fromDate ?? q.from,
      toDate: d.toDate ?? q.to,
      timezone: d.timezone ?? "UTC",
      totals: totals(d),
      previous: q.compare && d.previous ? { ...totals(d.previous), fromDate: d.previous.fromDate ?? "", toDate: d.previous.toDate ?? "" } : null,
    };
  },

  getNetworkSeries: async (q) => {
    const d = await get<NetworkSeries>(`/analytics/network/series?${rangeQuery(q)}`);
    return {
      fromDate: d.fromDate,
      toDate: d.toDate,
      timezone: d.timezone,
      granularity: d.granularity,
      points: d.points.map((p) => ({ from: p.from, to: p.to, appointments: p.appointments, completed: p.completed, cancelled: p.cancelled, noShow: p.noShow, revenue: p.revenue, ...pickFinance(p, false) })),
    };
  },

  getNetworkDetails: async (q: AnalyticsDetailsQuery) => {
    const p = new URLSearchParams();
    if (q.date) p.set("date", q.date);
    if (q.from) p.set("from", q.from);
    if (q.to) p.set("to", q.to);
    if (q.locationId) p.set("locationId", q.locationId);
    if (q.cursor) p.set("cursor", q.cursor);
    if (q.take) p.set("take", String(Math.min(200, Math.max(1, Math.trunc(q.take)))));
    return toDetails(await apiJson<AnalyticsDetails>(`${B}/analytics/network/details?${p}`, NO_STORE));
  },

  getServiceCosts: async (includeInactive = false) => {
    const d = await apiJson<{ items: (ServiceCostRow)[] }>(`${B}/service-costs?includeInactive=${includeInactive ? "true" : "false"}`, NO_STORE);
    return (d.items ?? []).map<ServiceCostRow>((r) => ({ ...toCostDetail(r), serviceName: r.serviceName, isActive: r.isActive !== false }));
  },
  getServiceCostDetail: async (serviceId) => toCostDetail(await apiJson<ServiceCostDetail>(`${B}/services/${encodeURIComponent(serviceId)}/costs`, NO_STORE)),
  setServiceCost: async (serviceId, input) => {
    const r = await send<ServiceCost & { locationId: string | null }>("PUT", `/services/${encodeURIComponent(serviceId)}/costs`, {
      locationId: input.locationId,
      costAmount: input.costAmount,
      masterPercent: input.masterPercent ?? null,
    });
    return { ...toCost(r), locationId: r.locationId ?? null };
  },
  deleteServiceCost: async (serviceId, locationId) => {
    const q = locationId ? `?locationId=${encodeURIComponent(locationId)}` : "";
    await apiJson<void>(`${B}/services/${encodeURIComponent(serviceId)}/costs${q}`, { method: "DELETE" });
  },

  resolvePhonePending: async (clientId, action) => {
    const r = await send<PhonePendingResult>("POST", `/clients/${encodeURIComponent(clientId)}/phone-pending`, { action });
    const src = SOURCES.find((x) => x === r.phoneSource);
    return { phone: r.phone, ...(src ? { phoneSource: src } : {}), ...(r.callConsentAt ? { callConsentAt: r.callConsentAt } : {}), phonePending: null };
  },

  getLocationsAnalytics: async (q) => (await get<LocationStatDto[]>(`/analytics/locations?${rangeQuery(q)}`)).map((l) => toLocationStat(l, q.compare)),

  getLocationBreakdown: async (locationId, q) => {
    const d = await get<LocationBreakdown>(`/analytics/locations/${encodeURIComponent(locationId)}/breakdown?${rangeQuery(q)}`);
    return {
      ...d,
      load: d.load ?? null,
      specialists: d.specialists.map((s) => ({ ...s, load: s.load ?? null })),
      services: d.services,
    };
  },

  getPromotionAnalytics: async (q) => {
    const [stats, promos, locs] = await Promise.all([
      get<PromoStatDto[]>(`/analytics/promotions?${rangeQuery(q)}`),
      get<PromotionDto[]>("/promotions"),
      httpBeautyApi.getLocations(),
    ]);
    const short = (iso: string) => new Date(iso).toLocaleDateString("uk-UA", { day: "numeric", month: "short" });
    return {
      rows: stats.map((s) => {
        const p = promos.find((x) => x.id === s.promotionId);
        return {
          id: s.promotionId,
          name: s.name,
          period: p?.startsAt ? `${short(p.startsAt)}${p.endsAt ? `–${short(p.endsAt)}` : ""}` : "постійна",
          where: p && p.locationIds.length ? p.locationIds.map((id) => locs.find((l) => l.id === id)?.name ?? "—").join(", ") : "Усі заклади",
          uses: s.uses,
          discountTotal: s.discountTotal,
          revenue: s.revenue,
          previous: q.compare ? (s.previous ?? null) : null,
        };
      }),
    };
  },

  getAiSettings: async () => toAiSettings(await apiJson<AiSettings>(`${B}/ai/settings`, NO_STORE)),

  saveAiSettings: async (input) => {
    const r = await send<AiSettings & { warnings?: { code: string; message: string }[] }>("PUT", "/ai/settings", input);
    return { settings: toAiSettings(r), warnings: (r.warnings ?? []).map((w) => ({ code: w.code, message: w.message })) };
  },

  setAiKeySource: async (keySource) => toAiSettings(await send<AiSettings>("PUT", "/ai/settings/key-source", { keySource })),

  getAiModels: async () => {
    const d = await apiJson<AiCatalog>(`${B}/ai/models`, NO_STORE);
    return { roles: d.roles, providers: d.providers, models: d.models };
  },

  // Ключ - лише в тілі PUT. У шляху/query: провайдер і прапорці, без секретів.
  saveAiKey: async ({ provider, apiKey, forRole, acknowledgeAffectedRoles }) => {
    const r = await send<AiKeySaved>("PUT", aiKeyPath(provider), {
      apiKey,
      forRole,
      verify: true,
      ...(acknowledgeAffectedRoles ? { acknowledgeAffectedRoles: true } : {}),
    });
    return {
      provider: r.provider,
      hasKey: !!r.hasKey,
      last4: r.last4 ?? null,
      updatedAt: r.updatedAt ?? null,
      keySource: r.keySource,
      usedByRoles: toRoles(r.usedByRoles),
    };
  },

  deleteAiKey: async ({ provider, forRole, acknowledgeAffectedRoles }) => {
    const q = `forRole=${encodeURIComponent(forRole)}${acknowledgeAffectedRoles ? "&acknowledgeAffectedRoles=true" : ""}`;
    await apiJson<void>(`${B}${aiKeyPath(provider)}?${q}`, { method: "DELETE" });
  },

  testAiKey: async (provider, apiKey) => {
    const r = await send<{ ok: boolean; reason?: string; provider: string; model: string; checkedAt: string }>(
      "POST",
      `${aiKeyPath(provider)}/test`,
      apiKey ? { apiKey } : {},
    );
    return { ok: !!r.ok, ...(r.reason ? { reason: r.reason } : {}), provider: r.provider, model: r.model, checkedAt: r.checkedAt };
  },

  getAiUsage: async (months = 1) => apiJson<AiUsage>(`${B}/ai/usage?months=${Math.min(12, Math.max(1, Math.trunc(months)))}`, NO_STORE),

  getChannels: async () => (await loadChannels()).list,

  updateChannel: (id, patch) => {
    const { handoff, buttons, ...rest } = patch;
    return saveChannel(id, {
      settings: { ...rest, ...(handoff ? { handoff: handoff as ChannelConfig["handoff"] } : {}) },
      ...(buttons !== undefined ? { buttons } : {}),
    });
  },
  syncTelegramCommands: async (id) => {
    const rows = await get<ChannelDto[]>("/channels");
    const row = rows.find((r) => r.type === TYPE_BY_ID[id]);
    if (!row) throw new BeautyApiError(404, "channel_not_found", "channel not found");
    await send("POST", `/channels/${encodeURIComponent(row.id)}/telegram/sync-commands`);
  },
  connectChannel: ({ id, secret }) => saveChannel(id, { isActive: true, token: secret }),
  disconnectChannel: (id) => saveChannel(id, { isActive: false }),

  getAiRequests: async () => {
    const rows = await get<AiActionDto[]>("/ai/actions?take=50");
    return rows.map<AiRequest>((a) => ({
      id: a.id,
      name: a.target,
      channel: "AI",
      time: a.createdAt.slice(11, 16),
      text: a.action,
      intent: a.action,
      reply: `Статус: ${a.status}`,
      replied: a.status !== "pending",
    }));
  },
  approveAiRequest: async (id) => {
    await send("POST", `/ai/actions/${id}/approve`);
  },
  // ---------- §22: нагадування, вподобання, задачі дзвінків, inbox (відповіді з ПД - no-store) ----------

  getContactPreferences: (clientId) => apiJson(`${B}/clients/${encodeURIComponent(clientId)}/contact-preferences`, NO_STORE),
  setContactPreferences: (clientId, input) =>
    send("PUT", `/clients/${encodeURIComponent(clientId)}/contact-preferences`, {
      method: input.method,
      offsetsMinutes: input.offsetsMinutes,
      applyToUpcoming: input.applyToUpcoming ?? true,
    }),

  getCallTasks: (q) => {
    const p = new URLSearchParams({ day: q.day, status: q.status });
    if (q.locationId) p.set("locationId", q.locationId);
    if (q.take) p.set("take", String(Math.min(200, q.take)));
    return apiJson<CallTaskList>(`${B}/call-tasks?${p}`, NO_STORE).then(toCallTasks);
  },
  updateCallTask: (id, patch) => send("PATCH", `/call-tasks/${encodeURIComponent(id)}`, patch),

  getReminderSettings: () => apiJson(`${B}/settings/reminders`, NO_STORE),
  saveReminderSettings: (s) => send("PUT", "/settings/reminders", s),

  getConversations: (q) => {
    const p = new URLSearchParams({ filter: q.filter });
    if (q.locationId) p.set("locationId", q.locationId);
    if (q.channelType) p.set("channelType", q.channelType);
    if (q.cursor) p.set("cursor", q.cursor);
    if (q.take) p.set("take", String(Math.min(100, q.take)));
    return apiJson<ConversationList>(`${B}/conversations?${p}`, NO_STORE).then((d) => (d && Array.isArray(d.items) ? { ...d, items: d.items.map(toSummary) } : d));
  },
  getConversation: (id, q) => {
    const p = new URLSearchParams();
    if (q?.before) p.set("before", q.before);
    if (q?.take) p.set("take", String(Math.min(100, q.take)));
    const qs = p.size > 0 ? `?${p}` : "";
    return apiJson<ConversationDetail>(`${B}/conversations/${encodeURIComponent(id)}${qs}`, NO_STORE).then(toConversationDetail);
  },
  replyToConversation: (id, text, idempotencyKey) =>
    apiJson(`${B}/conversations/${encodeURIComponent(id)}/reply`, {
      method: "POST",
      headers: { "Idempotency-Key": idempotencyKey },
      body: JSON.stringify({ text }),
    }),
  resumeConversationAi: async (id) => {
    await send("POST", `/conversations/${encodeURIComponent(id)}/resume-ai`);
  },
  markConversationRead: async (id, upToMessageId) => {
    await send("POST", `/conversations/${encodeURIComponent(id)}/read`, upToMessageId ? { upToMessageId } : {});
  },

  getAiChat: () => Promise.resolve(unsupported("AI chat")),
  sendAiChatMessage: () => Promise.resolve(unsupported("AI chat")),
  getAiPromoPlan: () => Promise.resolve(unsupported("AI promo plan")),
  launchAiCampaign: () => Promise.resolve(unsupported("AI campaign")),
};
