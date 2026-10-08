import { BeautyApiError } from "@/features/beauty-auth/errors";
import { apiJson, getUser } from "@/features/beauty-auth/session";
import { mastersForCalendar } from "../calendar-masters";
import { dateTimeLabel, money } from "../format";
import type {
  AiRequest,
  Appointment,
  AppointmentStatus,
  BeautyLocation,
  CalendarKind,
  CancelledBy,
  CancellationSettings,
  ChannelConfig,
  ChannelId,
  ClientProfile,
  ClientSummary,
  ClientTag,
  LocationId,
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
}
interface LocationDto {
  id: string;
  name: string;
  address?: string | null;
  phone?: string | null;
  timezone: string;
  isActive: boolean;
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
interface ClientDetailDto {
  client: ClientDto;
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
interface NetworkDto {
  appointments: number;
  completed: number;
  cancelled: number;
  noShow: number;
  revenue: number;
  averageCheck: number;
  cancellationRate: number;
  newClients: number;
}
interface LocationStatDto {
  locationId: string;
  name: string;
  appointments: number;
  completed: number;
  cancelled: number;
  revenue: number;
}
interface PromoStatDto {
  promotionId: string;
  name: string;
  uses: number;
  discountTotal: number;
  revenue: number;
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

// ---------- дати ----------

const pad = (n: number) => String(n).padStart(2, "0");
const ymd = (d: Date) => `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
const localOf = (iso: string) => iso.slice(0, 16);
const offsetOf = (iso: string) => /(Z|[+-]\d{2}:\d{2})$/.exec(iso)?.[1];
const dayLabel = (iso: string) =>
  new Date(`${iso.slice(0, 10)}T00:00:00`).toLocaleDateString("uk-UA", { day: "numeric", month: "long" });
const startOfDay = (d: Date) => new Date(d.getFullYear(), d.getMonth(), d.getDate());
const addDays = (d: Date, n: number) => new Date(d.getFullYear(), d.getMonth(), d.getDate() + n);
const mondayOf = (d: Date) => addDays(startOfDay(d), -((d.getDay() + 6) % 7));
const range = (from: Date, to: Date) => `from=${encodeURIComponent(from.toISOString())}&to=${encodeURIComponent(to.toISOString())}`;

/** Зсуви часової зони записів, щоб перенос зберігав зону закладу. */
const offsets = new Map<string, string>();

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

const toLocation = (l: LocationDto): BeautyLocation => ({
  id: l.id,
  name: l.name,
  address: l.address ?? null,
  phone: l.phone ?? null,
  timezone: l.timezone,
  isActive: l.isActive,
});

function toAppointment(a: AppointmentDto): Appointment {
  const off = offsetOf(a.startsAt);
  if (off) offsets.set(a.id, off);
  return {
    id: a.id,
    locationId: a.locationId,
    specialistId: a.specialistId,
    serviceId: a.serviceId,
    clientId: a.clientId,
    clientName: a.clientName,
    serviceName: a.serviceName,
    startsAt: localOf(a.startsAt),
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

const TYPE_BY_ID: Record<ChannelId, string> = {
  tg: "telegram",
  ig: "instagram",
  fb: "facebook",
  wa: "whatsapp",
  vb: "viber",
  web: "widget",
};

type ChannelSettings = Partial<Pick<ChannelConfig, "inbox" | "booking" | "ai" | "mode" | "greeting" | "handoff">>;

function parseSettings(json: string | null | undefined): ChannelSettings {
  if (!json) return {};
  try {
    const v: unknown = JSON.parse(json);
    return v && typeof v === "object" ? (v as ChannelSettings) : {};
  } catch {
    return {};
  }
}

function toChannel(id: ChannelId, row?: ChannelDto): ChannelConfig {
  const base = INITIAL_CHANNELS.find((c) => c.id === id)!;
  const s = parseSettings(row?.settingsJson);
  const connected = !!row && row.isActive && row.maskedToken !== "";
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
  };
}

async function loadChannels(): Promise<{ rows: ChannelDto[]; list: ChannelConfig[] }> {
  const rows = await get<ChannelDto[]>("/channels");
  const list = INITIAL_CHANNELS.map((c) => toChannel(c.id, rows.find((r) => r.type === TYPE_BY_ID[c.id])));
  return { rows, list };
}

async function saveChannel(
  id: ChannelId,
  change: { settings?: ChannelSettings; isActive?: boolean; token?: string },
): Promise<ChannelConfig> {
  const { rows } = await loadChannels();
  const row = rows.find((r) => r.type === TYPE_BY_ID[id]);
  const current = toChannel(id, row);
  const settings: ChannelSettings = {
    inbox: current.inbox,
    booking: current.booking,
    ai: current.ai,
    mode: current.mode,
    greeting: current.greeting,
    handoff: current.handoff,
    ...change.settings,
  };
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

/** Спеціалісти виводяться із записів (окремого `GET /specialists` у контракті ще немає). */
function specialistsFrom(list: AppointmentDto[]) {
  const map = new Map<string, { id: string; name: string; locations: Set<string> }>();
  for (const a of list) {
    const e = map.get(a.specialistId) ?? { id: a.specialistId, name: a.specialistName, locations: new Set<string>() };
    e.locations.add(a.locationName);
    map.set(a.specialistId, e);
  }
  return [...map.values()];
}

function groupSum<T>(items: T[], key: (t: T) => string, val: (t: T) => number) {
  const m = new Map<string, number>();
  for (const i of items) m.set(key(i), (m.get(key(i)) ?? 0) + val(i));
  return [...m.entries()].sort((a, b) => b[1] - a[1]);
}

const pctOf = (v: number, max: number) => (max > 0 ? Math.round((v / max) * 100) : 0);

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

export const httpBeautyApi: BeautyAdminApi = {
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

  getOverview: async (locationId: LocationId | null) => {
    const today = startOfDay(new Date());
    const q = locationId ? `&locationId=${locationId}` : "";
    const [appts, clients, net] = await Promise.all([
      weeklyAppointments(today, addDays(today, 1), q),
      get<ClientDto[]>("/clients?pageSize=6"),
      get<NetworkDto>(`/analytics/network?${range(today, addDays(today, 1))}`),
    ]);
    const live = appts.filter((a) => a.status !== "cancelled");
    const overview: Overview = {
      dateLabel: today.toLocaleDateString("uk-UA", { day: "numeric", month: "long" }),
      kpis: [
        { label: "Записів сьогодні", value: String(live.length), note: `завершено: ${net.completed}` },
        { label: "Виручка за день", value: money(net.revenue), note: "завершені візити" },
        { label: "Нові клієнти", value: String(net.newClients), note: "за добу" },
        { label: "Скасовано", value: String(net.cancelled), note: `не прийшли: ${net.noShow}` },
      ],
      rows: live
        .sort((a, b) => a.startsAt.localeCompare(b.startsAt))
        .map((a) => ({
          id: a.id,
          time: a.startsAt.slice(11, 16),
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

  getCalendarWeek: async (specialistId, includeCancelled = false) => {
    const monday = mondayOf(new Date());
    const sunday = addDays(monday, 6);
    // Без скасованих (дефолт API): за ними будуємо селектор майстрів; неактивним потрібні перенесення.
    const [live, roster] = await Promise.all([weeklyAppointments(monday, addDays(monday, 7)), get<SpecialistDto[]>("/specialists")]);
    const me = getUser();
    const masters = mastersForCalendar(
      roster.map((r) => ({ id: r.id, name: r.name, isActive: r.isActive, locationNames: r.locations.map((l) => l.locationName) })),
      live.map((a) => ({ specialistId: a.specialistId, status: statusOf(a.status) })),
      me?.role === "specialist" ? me.specialistId : null,
    );
    const effective = masters.find((m) => m.id === specialistId)?.id ?? masters[0]?.id ?? specialistId;
    const shown = includeCancelled ? await weeklyAppointments(monday, addDays(monday, 7), "&includeCancelled=true") : live;
    const fmt = (d: Date) => d.toLocaleDateString("uk-UA", { day: "numeric", month: "long" });
    return {
      weekLabel: `Тиждень ${fmt(monday)} – ${fmt(sunday)}`,
      weekStart: ymd(monday),
      days: Array.from({ length: 7 }, (_, i) => {
        const d = addDays(monday, i);
        return `${d.toLocaleDateString("uk-UA", { weekday: "short" }).replace(/^./, (c) => c.toUpperCase())} ${d.getDate()}`;
      }),
      masters,
      specialistId: effective,
      appointments: shown
        .filter((a) => a.specialistId === effective && (includeCancelled || a.status !== "cancelled"))
        .map(toAppointment),
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
    const local = startsAt.length === 16 ? `${startsAt}:00` : startsAt;
    const offset = offsets.get(id) ?? offsetFromBrowser(startsAt);
    await send("PATCH", `/appointments/${id}`, { startsAt: `${local}${offset}` });
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
        dateLabel: `${dayLabel(a.startsAt)}, ${a.startsAt.slice(11, 16)}`,
        serviceName: a.serviceName,
        specialistId: a.specialistId,
        specialistName: a.specialistName,
        locationName: a.locationName,
        sum: money(a.priceFinal),
        status: a.status === "completed" ? "completed" : a.status === "cancelled" ? "cancelled" : a.status === "no_show" ? "no_show" : "planned",
        viaPromo: !!a.promotionId,
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
      startsAt: parseDraftDate(draft.from, false),
      endsAt: parseDraftDate(draft.to, true),
      isActive: true,
      locationIds: draft.locationIds,
      serviceIds: draft.serviceIds,
    });
    return { id: r.id };
  },

  getNetworkAnalytics: async () => {
    const today = startOfDay(new Date());
    const weekStarts = Array.from({ length: 8 }, (_, i) => addDays(mondayOf(today), (i - 7) * 7));
    const [net, locs, weeks] = await Promise.all([
      get<NetworkDto>(`/analytics/network?${range(addDays(today, -30), addDays(today, 1))}`),
      get<LocationStatDto[]>(`/analytics/locations?${range(addDays(today, -30), addDays(today, 1))}`),
      Promise.all(weekStarts.map((w) => get<NetworkDto>(`/analytics/network?${range(w, addDays(w, 7))}`))),
    ]);
    const maxRev = Math.max(0, ...locs.map((l) => l.revenue));
    return {
      kpis: [
        { label: "Виручка мережі", value: money(net.revenue), note: `за 30 днів, закладів: ${locs.length}` },
        { label: "Записів", value: String(net.appointments), note: `завершено: ${net.completed}` },
        { label: "Середній чек", value: money(Math.round(net.averageCheck)), note: "по мережі" },
        { label: "Скасувань", value: `${Math.round(net.cancellationRate * 100)}%`, note: `нових клієнтів: ${net.newClients}` },
      ],
      weeks: weeks.map((w, i) => ({ label: `Т${i + 1}`, value: Math.round(w.revenue / 1000) })),
      locations: locs.map((l) => ({
        id: l.locationId,
        label: l.name,
        value: money(l.revenue),
        pct: pctOf(l.revenue, maxRev),
        note: `${l.appointments} записів · скасовано ${l.cancelled}`,
      })),
    };
  },

  getLocationAnalytics: async (locationId) => {
    const today = startOfDay(new Date());
    const [locs, appts] = await Promise.all([
      get<LocationStatDto[]>(`/analytics/locations?${range(addDays(today, -30), addDays(today, 1))}`),
      weeklyAppointments(addDays(today, -30), addDays(today, 1), `&locationId=${locationId}`),
    ]);
    const l = locs.find((x) => x.locationId === locationId);
    const done = appts.filter((a) => a.status === "completed");
    const svc = groupSum(done, (a) => a.serviceName, (a) => a.priceFinal);
    return {
      locationId,
      kpis: [
        { label: "Виручка", value: money(l?.revenue ?? 0), note: "за 30 днів" },
        { label: "Записів", value: String(l?.appointments ?? 0), note: `завершено: ${l?.completed ?? 0}` },
        { label: "Скасовано", value: String(l?.cancelled ?? 0), note: "" },
      ],
      specialists: specialistsFrom(appts).map((s) => {
        const mine = done.filter((a) => a.specialistId === s.id);
        return { id: s.id, name: s.name, count: mine.length, revenue: money(mine.reduce((sum, a) => sum + a.priceFinal, 0)), load: 0 };
      }),
      topServices: svc.slice(0, 5).map(([label, v]) => ({ label, value: money(v), pct: pctOf(v, svc[0]?.[1] ?? 0) })),
    };
  },

  getPromotionAnalytics: async () => {
    const today = startOfDay(new Date());
    const [stats, promos, locs] = await Promise.all([
      get<PromoStatDto[]>(`/analytics/promotions?${range(addDays(today, -30), addDays(today, 1))}`),
      get<PromotionDto[]>("/promotions"),
      httpBeautyApi.getLocations(),
    ]);
    const short = (iso: string) => new Date(iso).toLocaleDateString("uk-UA", { day: "numeric", month: "short" });
    const maxRev = Math.max(0, ...stats.map((s) => s.revenue));
    return {
      kpis: [
        { label: "Використань акцій", value: String(stats.reduce((s, x) => s + x.uses, 0)), note: "за 30 днів" },
        { label: "Виручка по акціях", value: money(stats.reduce((s, x) => s + x.revenue, 0)), note: "" },
        { label: "Витрати на знижки", value: money(stats.reduce((s, x) => s + x.discountTotal, 0)), note: "" },
      ],
      rows: stats.map((s) => {
        const p = promos.find((x) => x.id === s.promotionId);
        return {
          id: s.promotionId,
          name: s.name,
          period: p?.startsAt ? `${short(p.startsAt)}${p.endsAt ? `–${short(p.endsAt)}` : ""}` : "постійна",
          where: p && p.locationIds.length ? p.locationIds.map((id) => locs.find((l) => l.id === id)?.name ?? "—").join(", ") : "Усі заклади",
          count: s.uses,
          fresh: 0,
          revenue: money(s.revenue),
          cost: money(s.discountTotal),
        };
      }),
      bars: stats.map((s) => ({ label: s.name, value: money(s.revenue), pct: pctOf(s.revenue, maxRev) })),
    };
  },

  getChannels: async () => (await loadChannels()).list,

  updateChannel: (id, patch) => {
    const { handoff, ...rest } = patch;
    return saveChannel(id, { settings: { ...rest, ...(handoff ? { handoff: handoff as ChannelConfig["handoff"] } : {}) } });
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
  getAiChat: () => Promise.resolve(unsupported("AI chat")),
  sendAiChatMessage: () => Promise.resolve(unsupported("AI chat")),
  getAiPromoPlan: () => Promise.resolve(unsupported("AI promo plan")),
  launchAiCampaign: () => Promise.resolve(unsupported("AI campaign")),
};

/** Зсув браузера як запасний варіант (`+03:00`), якщо запис не завантажувався в цій сесії. */
function offsetFromBrowser(local: string): string {
  const off = -new Date(local).getTimezoneOffset();
  const sign = off >= 0 ? "+" : "-";
  return `${sign}${pad(Math.floor(Math.abs(off) / 60))}:${pad(Math.abs(off) % 60)}`;
}
