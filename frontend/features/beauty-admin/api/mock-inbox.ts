import { BeautyApiError } from "@/features/beauty-auth/errors";
import { CLIENT_SEED, CONTACT_PREFS_SEED, CONVERSATIONS_SEED, DEMO_TZ } from "../../beauty-shared/demo-seed";
import { addDaysYmd, instantToLocal, localToInstant, todayInZone } from "../../beauty-shared/zone-time";
import { normalizeOffsets, validateOffsets, validateQuietHours } from "../reminders";
import { createMockPhones, type MockPhones } from "./mock-phone";
import type {
  Appointment,
  AppointmentReminders,
  BeautyLocation,
  CallTask,
  CallTaskList,
  CallTaskPatch,
  CallTaskQuery,
  ChannelConfig,
  ContactPreferences,
  ContactPreferencesInput,
  ContactPreferencesSaved,
  ContactSource,
  ContactVerdict,
  ConversationDetail,
  ConversationList,
  ConversationListQuery,
  ConversationMessage,
  ConversationSummary,
  MessageSenderType,
  ReminderMethod,
  ReminderPoint,
  ReminderSettings,
  ReplyResult,
} from "../types";

/**
 * In-memory mock §22: inbox розмов, задачі «зателефонувати», вподобання клієнтів, налаштування нагадувань.
 * Дотримується правил і кодів помилок контракту (ролі, ліміти, ідемпотентність, 409/422/429). Вимоги ПД: тексти розмов
 * віддаються лише inbox-методами; телефон - лише в `getCallTasks`; specialist отримує 403 до будь-якого читання.
 */

const LATENCY_MS = 120;
export const MAX_REPLY_LENGTH = 3500;
const REPLY_PER_CONVERSATION_PER_MINUTE = 10;
const REPLY_PER_USER_PER_MINUTE = 20;
const MAX_UPCOMING_REPLAN = 50;
const CONTROL = /[\x00-\x09\x0b-\x1f\x7f]/;
const MIN = 60_000;

const err = (status: number, code: string, retry?: number) => new BeautyApiError(status, code, code, retry);

interface Deps {
  role: () => string;
  /** `false` - без затримки (тести). */
  wait?: boolean;
  now?: () => number;
  locations: () => BeautyLocation[];
  /** Поточні записи (з урахуванням перенесень і скасувань). */
  appointments: () => Appointment[];
  channels: () => ChannelConfig[];
  staffName: (id: string) => string;
  /** Через ~25 с після відповіді менеджера «клієнт» дописує повідомлення (демо polling і `held`). */
  simulateClient?: boolean;
  userName?: string;
  /** Телефони клієнтів (§25); за замовчуванням створюються зі сіду. Спільний екземпляр з mock-клієнтом - щоб «Прийняти» одразу бачили «Дзвінки». */
  phones?: MockPhones;
}

interface Conv {
  id: string;
  channelId: string;
  locationId?: string;
  clientId?: string;
  title: string;
  aiState: string;
  handoffReason?: string;
  aiPausedUntil?: number;
  awaitingSince?: number;
  readAt: number;
  messages: Msg[];
  proposal?: { status: string; expiresAt: number; summaryText: string };
}
interface Msg {
  id: string;
  from: MessageSenderType;
  text: string;
  at: number;
  status: ConversationMessage["status"];
  staffName?: string;
  failureCode?: string;
  buttons?: string[];
  /** Натиснута клієнтом кнопка (`kind=callback`). */
  pressed?: string;
  /** §25.4: контакт (`kind=contact`, body `[контакт]`). */
  contact?: ContactVerdict;
  /** §25.5.2: номер написано текстом - тіло `[номер телефону]`. */
  numberText?: boolean;
  /** §26.9.3: код купона текстом - тіло `[код купона]`. */
  couponText?: boolean;
}
interface Pref {
  method: ReminderMethod;
  offsets: number[];
  source: ContactSource;
  updatedAt: number;
  updatedBy?: { id: string; name: string };
}
interface TaskState {
  status: "open" | "done" | "skipped";
  by?: { id: string; name: string };
  at?: number;
  note?: string;
}

const CHANNEL_TYPE: Record<string, string> = { tg: "telegram", ig: "instagram", fb: "facebook", wa: "whatsapp", vb: "viber", web: "widget" };

/** Тиша: чи потрапляє `HH:mm` у [start, end) з переходом через північ. */
function inQuiet(hhmm: string, start: string, end: string): boolean {
  return start < end ? hhmm >= start && hhmm < end : hhmm >= start || hhmm < end;
}

export function createMockInbox(deps: Deps) {
  const now = deps.now ?? (() => Date.now());
  const wait = async <T>(v: T): Promise<T> => {
    if (deps.wait !== false) await new Promise((r) => setTimeout(r, LATENCY_MS));
    return structuredClone(v);
  };
  const isManager = () => deps.role() === "owner" || deps.role() === "admin";
  const guard = () => {
    if (!isManager()) throw err(403, "forbidden_role");
  };
  const me = { id: "mock-user", name: deps.userName ?? "Світлана Коваленко" };
  const phones = deps.phones ?? createMockPhones({ role: deps.role, now, wait: deps.wait });

  // ---------- налаштування нагадувань ----------
  let settings: ReminderSettings = { telegramEnabled: true, callEnabled: true, quietHoursStart: "22:00", quietHoursEnd: "08:00" };

  // ---------- вподобання клієнтів ----------
  const t0 = now();
  const prefs = new Map<string, Pref>();
  const telegramClients = new Set<string>();
  for (const p of CONTACT_PREFS_SEED) {
    if (p.telegram) telegramClients.add(p.clientId);
    if (p.pref) prefs.set(p.clientId, { method: p.pref.method, offsets: p.pref.offsets, source: p.pref.source, updatedAt: t0 - 3 * 86_400_000, updatedBy: p.pref.updatedBy });
  }
  /** Плани записів, що лишилися за старим вподобанням (`applyToUpcoming=false`). */
  const frozen = new Map<string, { method: ReminderMethod; offsets: number[] }>();

  const clientOf = (id: string) => CLIENT_SEED.find((c) => c.id === id);
  const locOf = (id: string) => deps.locations().find((l) => l.id === id);
  const isUpcoming = (a: Appointment) => (a.status === "pending" || a.status === "confirmed") && a.startsAt > instantToLocal(new Date(now()).toISOString(), DEMO_TZ);

  const prefView = (clientId: string): ContactPreferences => {
    const p = prefs.get(clientId);
    return {
      state: p ? "set" : "unset",
      ...(p ? { method: p.method, source: p.source, updatedAt: new Date(p.updatedAt).toISOString(), ...(p.updatedBy ? { updatedBy: p.updatedBy } : {}) } : {}),
      offsetsMinutes: p ? p.offsets : [],
      telegramAvailable: telegramClients.has(clientId),
      phoneAvailable: !!phones.phoneOf(clientId),
      telegramEnabled: settings.telegramEnabled,
      callEnabled: settings.callEnabled,
    };
  };

  /** Точки плану запису (§22.4 у спрощеному вигляді): минулі не створюються, тиша зсуває, близькі зливаються, дзвінок - одна точка. */
  function planPoints(startsAtLocal: string, tz: string, method: ReminderMethod, offsets: number[]): ReminderPoint[] {
    if (method === "none" || offsets.length === 0) return [];
    const nowMs = now();
    const startMs = localToInstant(startsAtLocal, tz).getTime();
    const iso = (ms: number) => new Date(ms).toISOString();
    if (method === "call") {
      const off = offsets[0];
      let due = startMs - off * MIN;
      if (!settings.callEnabled) return [{ kind: "call", offsetMinutes: off, dueAt: iso(due), status: "skipped", skipReason: "call_disabled" }];
      if (due < nowMs) {
        if (startMs - nowMs < 60 * MIN) return [];
        due = nowMs;
      }
      return [{ kind: "call", offsetMinutes: off, dueAt: iso(due), status: "open" }];
    }
    const points: ReminderPoint[] = [];
    for (const off of normalizeOffsets(offsets)) {
      let due = startMs - off * MIN;
      if (due <= nowMs) continue;
      if (!settings.telegramEnabled) {
        points.push({ kind: "message", offsetMinutes: off, dueAt: iso(due), status: "skipped", skipReason: "telegram_disabled" });
        continue;
      }
      const wall = instantToLocal(iso(due), tz);
      if (inQuiet(wall.slice(11, 16), settings.quietHoursStart, settings.quietHoursEnd)) {
        const wrapNextDay = settings.quietHoursStart > settings.quietHoursEnd && wall.slice(11, 16) >= settings.quietHoursStart;
        const date = wrapNextDay ? addDaysYmd(wall.slice(0, 10), 1) : wall.slice(0, 10);
        const shifted = localToInstant(`${date}T${settings.quietHoursEnd}`, tz).getTime();
        if (startMs - shifted >= 60 * MIN) due = shifted;
        else {
          points.push({ kind: "message", offsetMinutes: off, dueAt: iso(due), status: "skipped", skipReason: "quiet_hours" });
          continue;
        }
      }
      points.push({ kind: "message", offsetMinutes: off, dueAt: iso(due), status: "scheduled" });
    }
    points.sort((a, b) => a.dueAt.localeCompare(b.dueAt));
    for (let i = 0; i < points.length - 1; i++) {
      const a = points[i];
      const b = points[i + 1];
      if (a.status === "scheduled" && b.status === "scheduled" && Date.parse(b.dueAt) - Date.parse(a.dueAt) < 60 * MIN) {
        points[i] = { ...a, status: "skipped", skipReason: "merged" };
      }
    }
    return points;
  }

  const planOf = (a: Appointment): { method: ReminderMethod; offsets: number[] } => {
    const f = frozen.get(a.id);
    if (f) return f;
    const p = a.clientId ? prefs.get(a.clientId) : undefined;
    return p ? { method: p.method, offsets: p.offsets } : { method: "none", offsets: [] };
  };

  /** Нагадування запису для `AppointmentDto.reminders`; лише для майбутніх активних записів керівників. */
  function remindersFor(a: Appointment): AppointmentReminders | undefined {
    if (!isManager() || !isUpcoming(a)) return undefined;
    const plan = planOf(a);
    const tz = locOf(a.locationId)?.timezone ?? DEMO_TZ;
    return { method: plan.method, offsetsMinutes: plan.offsets, points: planPoints(a.startsAt, tz, plan.method, plan.offsets) };
  }

  // ---------- задачі «зателефонувати» ----------
  const taskState = new Map<string, TaskState>();
  const startedAt = t0;
  /** Бот клієнта «tg» заблоковано: для найближчого запису створено задачу `delivery_failed`. */
  const deliveryFailedClient = "tg";

  function allTasks(): CallTask[] {
    const out: CallTask[] = [];
    const upcoming = deps.appointments().filter(isUpcoming).sort((a, b) => a.startsAt.localeCompare(b.startsAt));
    const failedFor = upcoming.find((a) => a.clientId === deliveryFailedClient);
    const make = (a: Appointment, id: string, reason: CallTask["reason"], dueMs: number, offset?: number): CallTask | null => {
      const loc = locOf(a.locationId);
      const client = a.clientId ? clientOf(a.clientId) : undefined;
      if (!loc || !client) return null;
      const tz = loc.timezone ?? DEMO_TZ;
      const st = taskState.get(id);
      const dueAt = new Date(dueMs).toISOString();
      return {
        id,
        dueAt,
        localDueTime: instantToLocal(dueAt, tz).slice(11, 16),
        status: st ? st.status : "open",
        reason,
        ...(offset !== undefined ? { offsetMinutes: offset } : {}),
        client: {
          id: client.id,
          name: client.name,
          ...(phones.phoneOf(client.id) ? { phone: phones.phoneOf(client.id)! } : {}),
          // §25.7: інший номер від клієнта в чаті (повний) - лише в «Дзвінках» і картці клієнта.
          ...(phones.pendingOf(client.id) ? { phonePending: phones.pendingOf(client.id)! } : {}),
        },
        appointment: {
          id: a.id,
          startsAt: localToInstant(a.startsAt, tz).toISOString(),
          localStartsAt: a.startsAt,
          serviceName: a.serviceName,
          specialistName: deps.staffName(a.specialistId),
          status: a.status,
        },
        location: { id: loc.id, name: loc.name, timezone: tz },
        ...(st && st.status !== "open" && st.at && st.by ? { completedAt: new Date(st.at).toISOString(), completedBy: st.by } : {}),
        ...(st?.note ? { note: st.note } : {}),
      };
    };
    for (const a of upcoming) {
      const plan = planOf(a);
      if (plan.method !== "call") continue;
      const tz = locOf(a.locationId)?.timezone ?? DEMO_TZ;
      const p = planPoints(a.startsAt, tz, "call", plan.offsets).find((x) => x.kind === "call" && x.status === "open");
      const t = p ? make(a, `ct-${a.id}`, "reminder", Date.parse(p.dueAt), p.offsetMinutes) : null;
      if (t) out.push(t);
    }
    if (failedFor) {
      const t = make(failedFor, `cf-${failedFor.id}`, "delivery_failed", startedAt - 20 * MIN);
      if (t) out.push(t);
    }
    return out.sort((a, b) => a.dueAt.localeCompare(b.dueAt));
  }

  const localDate = (t: CallTask) => instantToLocal(t.dueAt, t.location.timezone).slice(0, 10);

  function dayFilter(day: string): (t: CallTask) => boolean {
    if (day === "today" || day === "tomorrow") {
      return (t) => localDate(t) === addDaysYmd(todayInZone(t.location.timezone, new Date(now())), day === "tomorrow" ? 1 : 0);
    }
    if (!/^\d{4}-\d{2}-\d{2}$/.test(day) || Number.isNaN(Date.parse(day))) throw err(422, "invalid_day");
    return (t) => localDate(t) === day;
  }

  // ---------- розмови ----------
  const convs: Conv[] = CONVERSATIONS_SEED.map((s) => ({
    id: s.id,
    channelId: s.channelId,
    locationId: s.locationId,
    clientId: s.clientId,
    title: s.title,
    aiState: s.aiState,
    handoffReason: s.handoffReason,
    aiPausedUntil: s.aiState === "handoff" && s.pausedForMinutes ? t0 + s.pausedForMinutes * MIN : undefined,
    awaitingSince: s.awaitingMinutes ? t0 - s.awaitingMinutes * MIN : undefined,
    readAt: t0 - s.readMinutesAgo * MIN,
    messages: s.messages.map((m, i) => ({ id: `${s.id}-m${i + 1}`, from: m.from, text: m.text, at: t0 - m.minutesAgo * MIN, status: m.status, staffName: m.staffName, failureCode: m.failureCode, buttons: m.buttons, pressed: m.pressed, contact: m.contact, numberText: m.numberText, couponText: m.couponText })),
  }));
  let seq = 1;
  const nextId = (c: Conv) => `${c.id}-m${c.messages.length + 1}-${++seq}`;
  const replies = new Map<string, { text: string; messageId: string }>();
  const rate: { conv: Record<string, number[]>; user: number[] } = { conv: {}, user: [] };

  /** Таймер паузи минув без відповіді: ШІ знову активний, `awaiting_staff_since` лишається (§22.8). */
  function tick(c: Conv) {
    if (c.aiState === "handoff" && c.aiPausedUntil !== undefined && c.aiPausedUntil <= now()) {
      c.aiState = "active";
      c.aiPausedUntil = undefined;
    }
  }
  const find = (id: string) => {
    const c = convs.find((x) => x.id === id);
    if (!c) throw err(404, "conversation_not_found");
    tick(c);
    return c;
  };
  const unread = (c: Conv) => c.messages.filter((m) => m.from === "client" && m.at > c.readAt).length;
  const iso = (ms: number) => new Date(ms).toISOString();

  function summaryOf(c: Conv): ConversationSummary {
    const ch = deps.channels().find((x) => x.id === c.channelId);
    const loc = c.locationId ? locOf(c.locationId) : undefined;
    const last = c.messages[c.messages.length - 1];
    return {
      id: c.id,
      channel: { id: c.channelId, type: CHANNEL_TYPE[c.channelId] ?? c.channelId, name: ch?.name ?? c.channelId },
      ...(loc ? { location: { id: loc.id, name: loc.name } } : {}),
      // §25.7: у inbox лише маска телефону й прапорець pending; повний номер - тільки в картці клієнта й «Дзвінках».
      ...(c.clientId && clientOf(c.clientId)
        ? {
            client: {
              id: c.clientId,
              name: clientOf(c.clientId)!.name,
              ...(phones.maskedOf(c.clientId) ? { phoneMasked: phones.maskedOf(c.clientId)! } : {}),
              phonePending: !!phones.pendingOf(c.clientId),
            },
          }
        : {}),
      title: c.title,
      aiState: c.aiState,
      ...(c.handoffReason ? { handoffReason: c.handoffReason } : {}),
      ...(c.aiPausedUntil ? { aiPausedUntil: iso(c.aiPausedUntil) } : {}),
      ...(c.awaitingSince ? { awaitingStaffSince: iso(c.awaitingSince) } : {}),
      lastMessageAt: iso(last.at),
      lastMessagePreview: [...bodyOf(last)].slice(0, 120).join(""),
      lastMessageSenderType: last.from,
      unreadCount: unread(c),
    };
  }

  const canReplyOf = (c: Conv): { ok: true } | { ok: false; reason: "channel_inactive" | "channel_unsupported" } => {
    if (c.channelId !== "tg") return { ok: false, reason: "channel_unsupported" };
    return deps.channels().find((x) => x.id === c.channelId)?.connected ? { ok: true } : { ok: false, reason: "channel_inactive" };
  };

  /** `body` за контрактом §23.10: для натискання - `"[кнопка] {підпис}"`. */
  const bodyOf = (m: Msg) => (m.pressed !== undefined ? `[кнопка] ${m.pressed}` : m.contact ? "[контакт]" : m.numberText ? "[номер телефону]" : m.couponText ? "[код купона]" : m.text);

  function toMessage(m: Msg, direction: "inbound" | "outbound"): ConversationMessage {
    return {
      id: m.id,
      direction,
      senderType: m.from,
      ...(m.from === "staff" && m.staffName ? { senderUser: { id: me.id, name: m.staffName } } : {}),
      kind: m.pressed !== undefined ? "callback" : m.contact ? "contact" : "text",
      body: bodyOf(m),
      ...(m.contact ? { contactVerdict: m.contact } : {}),
      ...(m.pressed !== undefined ? { pressedLabel: m.pressed } : {}),
      ...(m.buttons && m.buttons.length > 0 ? { buttons: [...m.buttons] } : {}),
      status: m.status,
      ...(m.failureCode ? { failureCode: m.failureCode } : {}),
      ...(m.status === "sent" ? { sentAt: iso(m.at) } : {}),
      createdAt: iso(m.at),
    };
  }

  function simulateClientMessage(c: Conv) {
    setTimeout(() => {
      tick(c);
      if (c.aiState !== "handoff") return;
      c.messages.push({ id: nextId(c), from: "client", text: "Дякую! А можна ще уточнити час?", at: now(), status: "held" });
      c.awaitingSince ??= now();
    }, 25_000);
  }

  return {
    // ----- налаштування -----
    getReminderSettings: async (): Promise<ReminderSettings> => {
      guard();
      return wait(settings);
    },
    saveReminderSettings: async (s: ReminderSettings): Promise<ReminderSettings> => {
      if (deps.role() !== "owner") throw err(403, "forbidden_role");
      const complete = typeof s?.telegramEnabled === "boolean" && typeof s?.callEnabled === "boolean" && !!s?.quietHoursStart && !!s?.quietHoursEnd;
      if (!complete) throw err(422, "settings_incomplete");
      if (!validateQuietHours(s.quietHoursStart, s.quietHoursEnd)) throw err(422, "invalid_quiet_hours");
      settings = { ...s };
      return wait(settings);
    },

    // ----- вподобання -----
    getContactPreferences: async (clientId: string): Promise<ContactPreferences> => {
      guard();
      if (!clientOf(clientId)) throw err(404, "client_not_found");
      return wait(prefView(clientId));
    },
    setContactPreferences: async (clientId: string, input: ContactPreferencesInput): Promise<ContactPreferencesSaved> => {
      guard();
      if (!clientOf(clientId)) throw err(404, "client_not_found");
      if (!["none", "telegram", "call"].includes(input.method)) throw err(422, "invalid_method");
      if (validateOffsets(input.method, input.offsetsMinutes)) throw err(422, "invalid_offsets");
      if (input.method === "telegram" && !settings.telegramEnabled) throw err(409, "telegram_reminders_disabled");
      if (input.method === "call" && !settings.callEnabled) throw err(409, "call_reminders_disabled");
      if (input.method === "telegram" && !telegramClients.has(clientId)) throw err(409, "telegram_unavailable");
      if (input.method === "call" && !phones.phoneOf(clientId)) throw err(409, "phone_required");
      const old = prefs.get(clientId);
      const upcoming = deps.appointments().filter((a) => a.clientId === clientId && isUpcoming(a)).slice(0, MAX_UPCOMING_REPLAN);
      const apply = input.applyToUpcoming !== false;
      if (apply) for (const a of upcoming) frozen.delete(a.id);
      else for (const a of upcoming) if (!frozen.has(a.id)) frozen.set(a.id, old ? { method: old.method, offsets: old.offsets } : { method: "none", offsets: [] });
      prefs.set(clientId, { method: input.method, offsets: normalizeOffsets(input.offsetsMinutes), source: "admin", updatedAt: now(), updatedBy: me });
      return wait({ ...prefView(clientId), replannedAppointments: apply ? upcoming.length : 0 });
    },
    remindersFor,

    // ----- задачі дзвінків -----
    getCallTasks: async (q: CallTaskQuery): Promise<CallTaskList> => {
      guard();
      const status = q.status ?? "open";
      if (!["open", "done", "skipped", "cancelled", "all"].includes(status)) throw err(422, "invalid_status");
      const inDay = dayFilter(q.day ?? "today");
      const base = allTasks().filter((t) => inDay(t) && (!q.locationId || t.location.id === q.locationId));
      const counts = {
        open: base.filter((t) => t.status === "open").length,
        done: base.filter((t) => t.status === "done").length,
        skipped: base.filter((t) => t.status === "skipped").length,
      };
      const take = Math.min(Math.max(q.take ?? 100, 1), 200);
      return wait({ items: base.filter((t) => status === "all" || t.status === status).slice(0, take), counts });
    },
    updateCallTask: async (id: string, patch: CallTaskPatch): Promise<CallTask> => {
      guard();
      const task = allTasks().find((t) => t.id === id);
      if (!task) throw new BeautyApiError(404, "call_task_not_found", "not found");
      if (!["done", "skipped", "open"].includes(patch.status)) throw err(422, "invalid_status");
      if ((patch.note ?? "").length > 300) throw err(422, "invalid_note");
      if (task.status === "cancelled") throw err(409, "task_cancelled");
      if (patch.status !== "open" && task.status !== "open") {
        throw new BeautyApiError(409, "task_already_closed", "closed", undefined, undefined, undefined, structuredClone(task));
      }
      if (patch.status === "open") taskState.delete(id);
      else taskState.set(id, { status: patch.status, by: me, at: now(), ...(patch.note?.trim() ? { note: patch.note.trim() } : {}) });
      return wait(allTasks().find((t) => t.id === id)!);
    },

    // ----- inbox -----
    getConversations: async (q: ConversationListQuery): Promise<ConversationList> => {
      guard();
      convs.forEach(tick);
      const list = convs.filter((c) => {
        if (q.filter === "needs_reply" && !c.awaitingSince) return false;
        if (q.locationId && c.locationId !== q.locationId) return false;
        if (q.channelType && (CHANNEL_TYPE[c.channelId] ?? c.channelId) !== q.channelType) return false;
        return true;
      });
      list.sort((a, b) =>
        q.filter === "needs_reply" ? (a.awaitingSince ?? 0) - (b.awaitingSince ?? 0) : b.messages[b.messages.length - 1].at - a.messages[a.messages.length - 1].at,
      );
      const take = Math.min(Math.max(q.take ?? 50, 1), 100);
      const start = Math.max(Number(q.cursor ?? 0) || 0, 0);
      const page = list.slice(start, start + take);
      return wait({
        items: page.map(summaryOf),
        ...(start + take < list.length ? { nextCursor: String(start + take) } : {}),
        meta: { needsReplyCount: convs.filter((c) => c.awaitingSince).length },
      });
    },

    getConversation: async (id: string, q?: { before?: string; take?: number }): Promise<ConversationDetail> => {
      guard();
      const c = find(id);
      const take = Math.min(Math.max(q?.take ?? 100, 1), 100);
      const upto = q?.before ? c.messages.findIndex((m) => m.id === q.before) : c.messages.length;
      const slice = c.messages.slice(Math.max(0, (upto < 0 ? c.messages.length : upto) - take), upto < 0 ? c.messages.length : upto);
      const can = canReplyOf(c);
      const upcoming = deps
        .appointments()
        .filter((a) => c.clientId && a.clientId === c.clientId && isUpcoming(a))
        .sort((a, b) => a.startsAt.localeCompare(b.startsAt))
        .slice(0, 3);
      return wait({
        conversation: { ...summaryOf(c), canReply: can.ok, ...(can.ok ? {} : { replyBlockedReason: can.reason }) },
        messages: slice.map((m) => toMessage(m, m.from === "client" ? "inbound" : "outbound")),
        ...(c.proposal ? { activeProposal: { status: c.proposal.status, expiresAt: iso(c.proposal.expiresAt), summaryText: c.proposal.summaryText } } : {}),
        upcomingAppointments: upcoming.map((a) => {
          const plan = planOf(a);
          return { id: a.id, startsAt: localToInstant(a.startsAt, locOf(a.locationId)?.timezone ?? DEMO_TZ).toISOString(), serviceName: a.serviceName, locationName: locOf(a.locationId)?.name ?? "", reminders: { method: plan.method, offsetsMinutes: plan.offsets }, ...(a.clientConfirmedAt ? { clientConfirmedAt: a.clientConfirmedAt } : {}) };
        }),
      });
    },

    replyToConversation: async (id: string, text: string, key: string): Promise<ReplyResult> => {
      guard();
      if (typeof key !== "string" || key.length < 8 || key.length > 128) throw err(400, "idempotency_key_required");
      const c = find(id);
      const can = canReplyOf(c);
      if (!can.ok) throw err(409, can.reason);
      const body = (text ?? "").trim();
      if (body.length < 1 || [...body].length > MAX_REPLY_LENGTH || CONTROL.test(body)) throw err(422, "invalid_text");
      const prev = replies.get(`${id}:${key}`);
      if (prev) {
        if (prev.text !== body) throw err(409, "idempotency_conflict");
        return wait({ message: { id: prev.messageId, status: "sent" } });
      }
      const t = now();
      const hit = (arr: number[], limit: number) => {
        const fresh = arr.filter((x) => t - x < MIN);
        arr.length = 0;
        arr.push(...fresh);
        return arr.length >= limit;
      };
      const convRate = (rate.conv[id] ??= []);
      if (hit(convRate, REPLY_PER_CONVERSATION_PER_MINUTE) || hit(rate.user, REPLY_PER_USER_PER_MINUTE)) throw err(429, "rate_limited", 30);
      convRate.push(t);
      rate.user.push(t);
      const m: Msg = { id: nextId(c), from: "staff", text: body, at: t, status: "sent", staffName: me.name };
      c.messages.push(m);
      replies.set(`${id}:${key}`, { text: body, messageId: m.id });
      // Транзакція відповіді (ADR-012 §2.6): пауза безстроково, очікування знято, held -> processed, пропозиція superseded.
      if (c.aiState === "active") c.handoffReason = "staff_reply";
      c.aiState = "handoff";
      c.aiPausedUntil = undefined;
      c.awaitingSince = undefined;
      c.readAt = t;
      c.messages.forEach((x) => {
        if (x.status === "held") x.status = "processed";
      });
      if (c.proposal) c.proposal.status = "superseded";
      if (deps.simulateClient) simulateClientMessage(c);
      return wait({ message: { id: m.id, status: "sent" } });
    },

    resumeConversationAi: async (id: string): Promise<void> => {
      guard();
      const c = find(id);
      c.aiState = "active";
      c.handoffReason = undefined;
      c.aiPausedUntil = undefined;
      c.awaitingSince = undefined;
      c.messages.forEach((x) => {
        if (x.status === "held") x.status = "processed";
      });
      await wait(null);
    },

    /** «Опрацьовано» (TASK-793): без відповіді клієнту; ШІ активний, очікування знято, held -> processed, прочитано. */
    resolveConversation: async (id: string): Promise<void> => {
      guard();
      const c = find(id);
      c.aiState = "active";
      c.handoffReason = undefined;
      c.aiPausedUntil = undefined;
      c.awaitingSince = undefined;
      c.readAt = now();
      c.messages.forEach((x) => {
        if (x.status === "held") x.status = "processed";
      });
      if (c.proposal) c.proposal.status = "superseded";
      await wait(null);
    },

    markConversationRead: async (id: string, upToMessageId?: string): Promise<void> => {
      guard();
      const c = find(id);
      const upTo = upToMessageId ? c.messages.find((m) => m.id === upToMessageId)?.at : undefined;
      c.readAt = Math.max(c.readAt, upTo ?? now());
      await wait(null);
    },
  };
}

export type MockInbox = ReturnType<typeof createMockInbox>;
