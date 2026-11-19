import { describe, expect, it } from "vitest";
import { BeautyApiError } from "@/features/beauty-auth/errors";
import { DEMO_LOCATIONS, STAFF, appointmentsSeed } from "../../beauty-shared/demo-seed";
import { INITIAL_CHANNELS } from "./mock-data";
import { createMockInbox } from "./mock-inbox";

// Середа 14.10.2026, 12:00 за Києвом (літо, UTC+3).
const NOW = Date.parse("2026-10-14T09:00:00Z");

function make(opts: { role?: string; now?: () => number } = {}) {
  const state = { role: opts.role ?? "owner", now: NOW };
  const channels = structuredClone(INITIAL_CHANNELS);
  const appts = appointmentsSeed("2026-10-14", new Date(NOW));
  const api = createMockInbox({
    role: () => state.role,
    wait: false,
    now: opts.now ?? (() => state.now),
    locations: () => DEMO_LOCATIONS,
    appointments: () => appts,
    channels: () => channels,
    staffName: (id) => STAFF.find((m) => m.id === id)?.name ?? "",
  });
  return { api, state, channels, appts };
}

async function code(p: Promise<unknown>): Promise<{ status: number; code: string }> {
  try {
    await p;
  } catch (e) {
    const err = e as BeautyApiError;
    return { status: err.status, code: err.code };
  }
  throw new Error("очікувалась помилка");
}

const KEY = "key-12345678";

describe("mock inbox: ролі (§22.10)", () => {
  it("specialist отримує 403 forbidden_role на будь-яке читання й запис", async () => {
    const { api } = make({ role: "specialist" });
    for (const call of [
      () => api.getConversations({ filter: "all" }),
      () => api.getConversation("cv1"),
      () => api.replyToConversation("cv1", "Добрий день", KEY),
      () => api.resumeConversationAi("cv1"),
      () => api.markConversationRead("cv1"),
      () => api.getCallTasks({ day: "today", status: "open" }),
      () => api.updateCallTask("x", { status: "done" }),
      () => api.getContactPreferences("oc"),
      () => api.setContactPreferences("oc", { method: "none", offsetsMinutes: [] }),
      () => api.getReminderSettings(),
    ]) {
      expect(await code(call())).toEqual({ status: 403, code: "forbidden_role" });
    }
  });

  it("налаштування нагадувань: owner змінює, admin лише читає", async () => {
    const { api, state } = make({ role: "admin" });
    const s = await api.getReminderSettings();
    expect(s).toEqual({ telegramEnabled: true, callEnabled: true, quietHoursStart: "22:00", quietHoursEnd: "08:00" });
    expect(await code(api.saveReminderSettings(s))).toEqual({ status: 403, code: "forbidden_role" });
    state.role = "owner";
    expect((await api.saveReminderSettings({ ...s, quietHoursStart: "23:00" })).quietHoursStart).toBe("23:00");
  });
});

describe("mock inbox: список і сценарій передачі від ШІ", () => {
  it("демо-розмови: ШІ відповідає, передано людині, пауза з held, Instagram, не доставлено", async () => {
    const { api } = make();
    const all = await api.getConversations({ filter: "all" });
    expect(all.items.map((c) => c.id).sort()).toEqual(["cv1", "cv2", "cv3", "cv4", "cv5", "cv6"]);
    expect(all.meta.needsReplyCount).toBe(2);
    const by = Object.fromEntries(all.items.map((c) => [c.id, c]));
    expect(by.cv1.aiState).toBe("active");
    expect(by.cv2).toMatchObject({ aiState: "handoff", handoffReason: "policy_unknown", unreadCount: 1 });
    expect(by.cv3).toMatchObject({ aiState: "handoff", unreadCount: 3 });
    expect(by.cv3.lastMessagePreview.length).toBeLessThanOrEqual(120);
  });

  it("needs_reply: лише з awaitingStaffSince, найдовше очікувана першою; фільтр закладу", async () => {
    const { api } = make();
    const r = await api.getConversations({ filter: "needs_reply" });
    expect(r.items.map((c) => c.id)).toEqual(["cv3", "cv2"]);
    expect((await api.getConversations({ filter: "all", locationId: "p" })).items.map((c) => c.id).sort()).toEqual(["cv3", "cv5"]);
    expect((await api.getConversations({ filter: "all", channelType: "instagram" })).items.map((c) => c.id)).toEqual(["cv4"]);
  });

  it("деталі: held-повідомлення, failureCode, canReply для Instagram = channel_unsupported; чужий id = 404", async () => {
    const { api } = make();
    const d3 = await api.getConversation("cv3");
    expect(d3.messages.filter((m) => m.status === "held")).toHaveLength(3);
    expect(d3.conversation.canReply).toBe(true);
    expect(d3.messages.map((m) => m.direction)).toContain("outbound");
    expect((await api.getConversation("cv5")).messages.find((m) => m.status === "failed")?.failureCode).toBe("bot_blocked");
    expect((await api.getConversation("cv4")).conversation).toMatchObject({ canReply: false, replyBlockedReason: "channel_unsupported" });
    expect(await code(api.getConversation("nope"))).toEqual({ status: 404, code: "conversation_not_found" });
  });

  it("відповідь менеджера: повідомлення додається, пауза безстроково, очікування знято, held -> processed", async () => {
    const { api } = make();
    const r = await api.replyToConversation("cv3", "  Добрий день! Можу запропонувати вівторок о 15:00.  ", KEY);
    expect(r.message.status).toBe("sent");
    const d = await api.getConversation("cv3");
    const last = d.messages[d.messages.length - 1];
    expect(last).toMatchObject({ senderType: "staff", direction: "outbound", body: "Добрий день! Можу запропонувати вівторок о 15:00.", senderUser: { name: expect.any(String) } });
    expect(d.messages.some((m) => m.status === "held")).toBe(false);
    expect(d.conversation).toMatchObject({ aiState: "handoff", unreadCount: 0 });
    expect(d.conversation.aiPausedUntil).toBeUndefined();
    expect(d.conversation.awaitingStaffSince).toBeUndefined();
    expect((await api.getConversations({ filter: "needs_reply" })).meta.needsReplyCount).toBe(1);
  });

  it("«Повернути ШІ»: стан active, причина й очікування очищені, ідемпотентно", async () => {
    const { api } = make();
    await api.resumeConversationAi("cv2");
    await api.resumeConversationAi("cv2");
    const c = (await api.getConversation("cv2")).conversation;
    expect(c).toMatchObject({ aiState: "active" });
    expect(c.handoffReason).toBeUndefined();
    expect(c.awaitingStaffSince).toBeUndefined();
  });

  it("таймер паузи минув без відповіді: ШІ знову активний, але очікування лишається (§22.8)", async () => {
    const { api, state } = make();
    state.now = NOW + 12 * 3_600_000;
    const c = (await api.getConversation("cv2")).conversation;
    expect(c.aiState).toBe("active");
    expect(c.awaitingStaffSince).toBeDefined();
  });

  it("read: unreadCount обнуляється", async () => {
    const { api } = make();
    expect((await api.getConversation("cv3")).conversation.unreadCount).toBe(3);
    await api.markConversationRead("cv3", "cv3-m7");
    expect((await api.getConversation("cv3")).conversation.unreadCount).toBe(0);
  });
});

describe("mock inbox: reply, помилки й ліміти (§22.6.5)", () => {
  it("без ключа ідемпотентності -> 400; порожній / задовгий / керівні символи -> 422", async () => {
    const { api } = make();
    expect(await code(api.replyToConversation("cv1", "Привіт", ""))).toEqual({ status: 400, code: "idempotency_key_required" });
    expect(await code(api.replyToConversation("cv1", "Привіт", "short"))).toEqual({ status: 400, code: "idempotency_key_required" });
    expect(await code(api.replyToConversation("cv1", "   ", KEY))).toEqual({ status: 422, code: "invalid_text" });
    expect(await code(api.replyToConversation("cv1", "x".repeat(3501), KEY))).toEqual({ status: 422, code: "invalid_text" });
    expect(await code(api.replyToConversation("cv1", "a\u0000b", KEY))).toEqual({ status: 422, code: "invalid_text" });
    await expect(api.replyToConversation("cv1", "x".repeat(3500), KEY)).resolves.toBeTruthy();
  });

  it("повтор того самого ключа й тексту = те саме повідомлення; інший текст = 409 idempotency_conflict", async () => {
    const { api } = make();
    const a = await api.replyToConversation("cv1", "Текст", KEY);
    const b = await api.replyToConversation("cv1", "Текст", KEY);
    expect(b.message.id).toBe(a.message.id);
    expect((await api.getConversation("cv1")).messages.filter((m) => m.senderType === "staff")).toHaveLength(1);
    expect(await code(api.replyToConversation("cv1", "Інший текст", KEY))).toEqual({ status: 409, code: "idempotency_conflict" });
  });

  it("Instagram -> 409 channel_unsupported; вимкнений Telegram -> 409 channel_inactive", async () => {
    const { api, channels } = make();
    expect(await code(api.replyToConversation("cv4", "Привіт", KEY))).toEqual({ status: 409, code: "channel_unsupported" });
    channels.find((c) => c.id === "tg")!.connected = false;
    expect(await code(api.replyToConversation("cv1", "Привіт", KEY))).toEqual({ status: 409, code: "channel_inactive" });
    expect((await api.getConversation("cv1")).conversation).toMatchObject({ canReply: false, replyBlockedReason: "channel_inactive" });
  });

  it("11-та відповідь за хвилину в розмові -> 429 rate_limited", async () => {
    const { api } = make();
    for (let i = 0; i < 10; i++) await api.replyToConversation("cv1", `Відповідь ${i}`, `key-number-${i}`);
    const e = await code(api.replyToConversation("cv1", "Ще одна", "key-number-11"));
    expect(e).toEqual({ status: 429, code: "rate_limited" });
  });
});

describe("mock: задачі дзвінків (§22.6.3)", () => {
  it("список на сьогодні: клієнт, телефон (керівнику), візит, заклад; рахунки; сортування за часом", async () => {
    const { api } = make();
    const r = await api.getCallTasks({ day: "today", status: "open" });
    expect(r.items.length).toBeGreaterThan(0);
    const failed = r.items.find((t) => t.reason === "delivery_failed");
    expect(failed).toMatchObject({ client: { id: "tg", phone: expect.stringMatching(/^\+380/) }, status: "open", location: { timezone: "Europe/Kyiv" } });
    expect(r.items.map((t) => t.dueAt)).toEqual([...r.items.map((t) => t.dueAt)].sort());
    expect(r.counts.open).toBe(r.items.length);
  });

  it("done -> хто/коли; повторне done -> 409 task_already_closed з поточним станом; open очищає", async () => {
    const { api, state } = make();
    const id = (await api.getCallTasks({ day: "today", status: "open" })).items[0].id;
    const done = await api.updateCallTask(id, { status: "done", note: "Не відповіла, наберу ще" });
    expect(done).toMatchObject({ status: "done", completedBy: { name: expect.any(String) }, note: "Не відповіла, наберу ще" });
    expect(done.completedAt).toBeTruthy();
    try {
      await api.updateCallTask(id, { status: "skipped" });
      throw new Error("очікувався 409");
    } catch (e) {
      expect((e as BeautyApiError).code).toBe("task_already_closed");
      expect((e as BeautyApiError).task).toMatchObject({ id, status: "done" });
    }
    expect((await api.getCallTasks({ day: "today", status: "done" })).items.map((t) => t.id)).toContain(id);
    const reopened = await api.updateCallTask(id, { status: "open" });
    expect(reopened.status).toBe("open");
    expect(reopened.completedBy).toBeUndefined();
    state.role = "specialist";
    expect((await code(api.getCallTasks({ day: "today", status: "open" }))).status).toBe(403);
  });

  it("валідація: invalid_day, invalid_status, invalid_note, call_task_not_found", async () => {
    const { api } = make();
    expect(await code(api.getCallTasks({ day: "завтра-ввечері", status: "open" }))).toEqual({ status: 422, code: "invalid_day" });
    expect(await code(api.getCallTasks({ day: "today", status: "weird" as never }))).toEqual({ status: 422, code: "invalid_status" });
    const id = (await api.getCallTasks({ day: "today", status: "open" })).items[0].id;
    expect(await code(api.updateCallTask(id, { status: "done", note: "x".repeat(301) }))).toEqual({ status: 422, code: "invalid_note" });
    expect(await code(api.updateCallTask(id, { status: "weird" as never }))).toEqual({ status: 422, code: "invalid_status" });
    expect(await code(api.updateCallTask("nope", { status: "done" }))).toEqual({ status: 404, code: "call_task_not_found" });
  });
});

describe("mock: вподобання клієнта (§22.6.2) і нагадування запису", () => {
  it("стани: oc - Telegram 2+24 год; sm - не обрано; mp - без Telegram-розмови", async () => {
    const { api } = make();
    expect(await api.getContactPreferences("oc")).toMatchObject({ state: "set", method: "telegram", offsetsMinutes: [120, 1440], source: "chat", telegramAvailable: true });
    expect(await api.getContactPreferences("sm")).toMatchObject({ state: "unset", offsetsMinutes: [] });
    expect(await api.getContactPreferences("mp")).toMatchObject({ telegramAvailable: false, phoneAvailable: true });
    expect(await code(api.getContactPreferences("zzz"))).toEqual({ status: 404, code: "client_not_found" });
  });

  it("PUT: джерело admin + автор, переплан майбутніх записів; applyToUpcoming=false не чіпає наявні", async () => {
    const { api, appts } = make();
    const saved = await api.setContactPreferences("sm", { method: "telegram", offsetsMinutes: [1440, 120, 120] });
    expect(saved).toMatchObject({ state: "set", method: "telegram", offsetsMinutes: [120, 1440], source: "admin", updatedBy: { name: expect.any(String) } });
    expect(saved.replannedAppointments).toBeGreaterThan(0);
    const mine = appts.filter((a) => a.clientId === "sm" && (a.status === "pending" || a.status === "confirmed") && a.startsAt > "2026-10-14T12:00");
    expect(mine.length).toBe(saved.replannedAppointments);
    const before = api.remindersFor(mine[0]);
    expect(before?.method).toBe("telegram");
    const keep = await api.setContactPreferences("sm", { method: "none", offsetsMinutes: [], applyToUpcoming: false });
    expect(keep.replannedAppointments).toBe(0);
    expect(api.remindersFor(mine[0])?.method).toBe("telegram");
    expect(await api.getContactPreferences("sm")).toMatchObject({ method: "none" });
  });

  it("помилки: invalid_method / invalid_offsets (422); telegram_unavailable / phone_required / *_disabled (409)", async () => {
    const { api, state } = make();
    expect(await code(api.setContactPreferences("oc", { method: "sms" as never, offsetsMinutes: [60] }))).toEqual({ status: 422, code: "invalid_method" });
    for (const [method, offsets] of [
      ["telegram", []],
      ["telegram", [30]],
      ["call", [60, 120]],
      ["none", [60]],
    ] as const) {
      expect(await code(api.setContactPreferences("oc", { method, offsetsMinutes: [...offsets] }))).toEqual({ status: 422, code: "invalid_offsets" });
    }
    expect(await code(api.setContactPreferences("mp", { method: "telegram", offsetsMinutes: [60] }))).toEqual({ status: 409, code: "telegram_unavailable" });
    expect(await code(api.setContactPreferences("zzz", { method: "none", offsetsMinutes: [] }))).toEqual({ status: 404, code: "client_not_found" });
    const s = await api.getReminderSettings();
    await api.saveReminderSettings({ ...s, callEnabled: false });
    expect(await code(api.setContactPreferences("oc", { method: "call", offsetsMinutes: [60] }))).toEqual({ status: 409, code: "call_reminders_disabled" });
    await api.saveReminderSettings({ ...s, callEnabled: true, telegramEnabled: false });
    expect(await code(api.setContactPreferences("oc", { method: "telegram", offsetsMinutes: [60] }))).toEqual({ status: 409, code: "telegram_reminders_disabled" });
    state.role = "specialist";
    expect((await code(api.setContactPreferences("oc", { method: "none", offsetsMinutes: [] }))).status).toBe(403);
  });

  it("точки запису: тиша зсуває на ранок, минулі не створюються, близькі зливаються; дзвінок - одна точка", async () => {
    const { api, appts } = make();
    const base = appts.find((a) => a.clientId === "oc" && a.startsAt > "2026-10-15T00:00")!;
    // Візит о 09:30: за 2 год = 07:30 - це тиша -> зсув на 08:00 (до візиту 90 хв >= 60).
    const early = { ...base, startsAt: "2026-10-16T09:30" };
    await api.setContactPreferences("oc", { method: "telegram", offsetsMinutes: [60, 120] });
    const r = api.remindersFor(early)!;
    const live = r.points!.filter((p) => p.status === "scheduled");
    // 60 хв -> 08:30, 120 хв -> 07:30 зсунуто на 08:00: різниця 30 хв < 60 -> лишається ближча до візиту.
    expect(live).toHaveLength(1);
    expect(live[0].offsetMinutes).toBe(60);
    expect(r.points!.find((p) => p.offsetMinutes === 120)).toMatchObject({ status: "skipped", skipReason: "merged" });
    // Візит о 08:30: і 2 год, і 1 год потрапляють у тишу, а зсув на 08:00 лишає менше 60 хв -> пропуск.
    const quiet = api.remindersFor({ ...base, startsAt: "2026-10-16T08:30" })!;
    expect(quiet.points!.find((p) => p.offsetMinutes === 120)).toMatchObject({ status: "skipped", skipReason: "quiet_hours" });
    await api.setContactPreferences("mp", { method: "call", offsetsMinutes: [1440] });
    const call = api.remindersFor({ ...base, clientId: "mp", startsAt: "2026-10-16T11:00" })!;
    expect(call.points).toHaveLength(1);
    expect(call.points![0]).toMatchObject({ kind: "call", offsetMinutes: 1440, status: "open" });
    // Минулі точки не створюються.
    const soon = api.remindersFor({ ...base, clientId: "oc", startsAt: "2026-10-14T13:30" })!;
    expect(soon.points!.every((p) => Date.parse(p.dueAt) > NOW)).toBe(true);
  });

  it("специфікація: specialist не отримує reminders у записах", () => {
    const { api, appts, state } = make();
    state.role = "specialist";
    expect(api.remindersFor(appts.find((a) => a.startsAt > "2026-10-15T00:00")!)).toBeUndefined();
  });
});

describe("mock: налаштування нагадувань (§22.6.4)", () => {
  it("settings_incomplete та invalid_quiet_hours", async () => {
    const { api } = make();
    const s = await api.getReminderSettings();
    expect(await code(api.saveReminderSettings({ ...s, quietHoursEnd: "" }))).toEqual({ status: 422, code: "settings_incomplete" });
    for (const bad of [{ quietHoursStart: "25:00" }, { quietHoursStart: "22:00", quietHoursEnd: "22:00" }, { quietHoursStart: "20:00", quietHoursEnd: "09:00" }]) {
      expect(await code(api.saveReminderSettings({ ...s, ...bad }))).toEqual({ status: 422, code: "invalid_quiet_hours" });
    }
    await expect(api.saveReminderSettings({ ...s, quietHoursStart: "23:30", quietHoursEnd: "07:30" })).resolves.toMatchObject({ quietHoursStart: "23:30" });
  });
});

describe("mock inbox §25.7: телефон у розмовах і задачах", () => {
  it("розмова: у inbox лише маска й прапорець pending; повного номера в JSON немає", async () => {
    const { api } = make();
    const d = await api.getConversation("cv3");
    expect(d.conversation.client).toEqual({ id: "kl", name: "Катерина Ляшко", phoneMasked: "+••••••••••03", phonePending: true });
    const list = await api.getConversations({ filter: "all" });
    const json = JSON.stringify([d, list]);
    expect(json).not.toMatch(/380 ?50 ?000 ?00 ?0\d/);
    expect(json).not.toContain("111 22 33");
    expect(list.items.find((c) => c.id === "cv1")!.client).toMatchObject({ phonePending: false });
  });

  it("повідомлення-контакти: kind=contact з вердиктом, тіло «[контакт]»; номер текстом - «[номер телефону]»", async () => {
    const { api } = make();
    const cv3 = await api.getConversation("cv3");
    expect(cv3.messages[0]).toMatchObject({ kind: "contact", body: "[контакт]", contactVerdict: "own", direction: "inbound" });
    const cv2 = await api.getConversation("cv2");
    expect(cv2.messages[0]).toMatchObject({ kind: "contact", contactVerdict: "foreign" });
    const cv1 = await api.getConversation("cv1");
    expect(cv1.messages[0]).toMatchObject({ kind: "text", body: "[номер телефону]" });
  });

  it("«Дзвінки»: повний phonePending лише в call-tasks (owner/admin)", async () => {
    const { api } = make();
    const found = [];
    for (let i = 0; i < 12; i++) {
      const day = new Date(NOW + i * 86_400_000).toISOString().slice(0, 10);
      found.push(...(await api.getCallTasks({ day, status: "all" })).items);
    }
    const kl = found.find((t) => t.client.id === "kl");
    expect(kl, "задача дзвінка для kl").toBeDefined();
    expect(kl!.client).toMatchObject({ phone: "+380 50 000 00 03", phonePending: "+380 67 111 22 33" });
    expect(found.filter((t) => t.client.id !== "kl").every((t) => t.client.phonePending === undefined)).toBe(true);
  });
});
