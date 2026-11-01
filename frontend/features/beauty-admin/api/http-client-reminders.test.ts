import { beforeEach, describe, expect, it, vi } from "vitest";
import { BeautyApiError, humanizeError, readApiError } from "@/features/beauty-auth/errors";

type Init = { method?: string; body?: string; cache?: string; headers?: Record<string, string> };
const apiJson = vi.fn<(path: string, init?: Init) => Promise<unknown>>();
vi.mock("@/features/beauty-auth/session", () => ({
  apiJson: (path: string, init?: Init) => apiJson(path, init),
  getUser: () => ({ id: "u", role: "owner", specialistId: null }),
}));

import { httpBeautyApi } from "./http-client";

beforeEach(() => {
  apiJson.mockReset();
  apiJson.mockResolvedValue(undefined);
});

const last = () => apiJson.mock.calls[apiJson.mock.calls.length - 1];

describe("http-клієнт §22.6: шляхи, методи, тіла", () => {
  it("вподобання клієнта: GET без кешу; PUT з applyToUpcoming=true за замовчуванням", async () => {
    await httpBeautyApi.getContactPreferences("c 1");
    expect(last()[0]).toBe("/api/beauty/clients/c%201/contact-preferences");
    expect(last()[1]).toMatchObject({ cache: "no-store" });
    await httpBeautyApi.setContactPreferences("c1", { method: "telegram", offsetsMinutes: [120, 1440] });
    expect(last()[0]).toBe("/api/beauty/clients/c1/contact-preferences");
    expect(last()[1]).toMatchObject({ method: "PUT" });
    expect(JSON.parse(last()[1]!.body!)).toEqual({ method: "telegram", offsetsMinutes: [120, 1440], applyToUpcoming: true });
    await httpBeautyApi.setContactPreferences("c1", { method: "none", offsetsMinutes: [], applyToUpcoming: false });
    expect(JSON.parse(last()[1]!.body!).applyToUpcoming).toBe(false);
  });

  it("задачі дзвінків: query day/status/locationId/take (take ≤ 200); PATCH", async () => {
    await httpBeautyApi.getCallTasks({ day: "tomorrow", status: "open", locationId: "L1", take: 500 });
    expect(last()[0]).toBe("/api/beauty/call-tasks?day=tomorrow&status=open&locationId=L1&take=200");
    expect(last()[1]).toMatchObject({ cache: "no-store" });
    await httpBeautyApi.getCallTasks({ day: "2026-10-25", status: "all" });
    expect(last()[0]).toBe("/api/beauty/call-tasks?day=2026-10-25&status=all");
    await httpBeautyApi.updateCallTask("t1", { status: "done", note: "ok" });
    expect(last()[0]).toBe("/api/beauty/call-tasks/t1");
    expect(last()[1]).toMatchObject({ method: "PATCH" });
    expect(JSON.parse(last()[1]!.body!)).toEqual({ status: "done", note: "ok" });
  });

  it("налаштування нагадувань: GET без кешу, PUT з усіма чотирма полями", async () => {
    await httpBeautyApi.getReminderSettings();
    expect(last()[0]).toBe("/api/beauty/settings/reminders");
    const s = { telegramEnabled: true, callEnabled: false, quietHoursStart: "22:00", quietHoursEnd: "08:00" };
    await httpBeautyApi.saveReminderSettings(s);
    expect(last()[1]).toMatchObject({ method: "PUT" });
    expect(JSON.parse(last()[1]!.body!)).toEqual(s);
  });

  it("inbox: список, деталі, reply з Idempotency-Key, resume-ai, read", async () => {
    await httpBeautyApi.getConversations({ filter: "needs_reply", locationId: "L1", channelType: "telegram", cursor: "50", take: 500 });
    expect(last()[0]).toBe("/api/beauty/conversations?filter=needs_reply&locationId=L1&channelType=telegram&cursor=50&take=100");
    expect(last()[1]).toMatchObject({ cache: "no-store" });
    await httpBeautyApi.getConversation("cv1");
    expect(last()[0]).toBe("/api/beauty/conversations/cv1");
    await httpBeautyApi.getConversation("cv1", { before: "m9", take: 20 });
    expect(last()[0]).toBe("/api/beauty/conversations/cv1?before=m9&take=20");

    await httpBeautyApi.replyToConversation("cv1", "Добрий день", "key-12345678");
    expect(last()[0]).toBe("/api/beauty/conversations/cv1/reply");
    expect(last()[1]).toMatchObject({ method: "POST", headers: { "Idempotency-Key": "key-12345678" } });
    expect(JSON.parse(last()[1]!.body!)).toEqual({ text: "Добрий день" });

    await httpBeautyApi.resumeConversationAi("cv1");
    expect(last()[0]).toBe("/api/beauty/conversations/cv1/resume-ai");
    expect(last()[1]).toMatchObject({ method: "POST" });
    await httpBeautyApi.resolveConversation("cv1");
    expect(last()[0]).toBe("/api/beauty/conversations/cv1/resolve");
    expect(last()[1]).toMatchObject({ method: "POST" });
    expect(last()[1]!.body).toBeUndefined();
    await httpBeautyApi.markConversationRead("cv1", "m5");
    expect(last()[0]).toBe("/api/beauty/conversations/cv1/read");
    expect(JSON.parse(last()[1]!.body!)).toEqual({ upToMessageId: "m5" });
    await httpBeautyApi.markConversationRead("cv1");
    expect(last()[1]!.body).toBe("{}");
  });
});

describe("помилки §22 (readApiError / humanizeError)", () => {
  it("409 task_already_closed віддає поточний стан задачі", async () => {
    const res = new Response(JSON.stringify({ code: "task_already_closed", message: "x", task: { id: "t1", status: "done", completedBy: { name: "Ірина" } } }), { status: 409 });
    const e = await readApiError(res);
    expect(e).toBeInstanceOf(BeautyApiError);
    expect(e.code).toBe("task_already_closed");
    expect(e.task).toMatchObject({ status: "done" });
  });

  it("429 зберігає Retry-After", async () => {
    const res = new Response(JSON.stringify({ code: "rate_limited", message: "x" }), { status: 429, headers: { "Retry-After": "12" } });
    expect((await readApiError(res)).retryAfterSeconds).toBe(12);
  });

  it("усі нові коди контракту мають український текст, англійського message сервера немає", () => {
    const codes = [
      "conversation_not_found", "channel_inactive", "channel_unsupported", "idempotency_conflict", "idempotency_key_required", "invalid_text",
      "client_not_found", "invalid_method", "invalid_offsets", "invalid_reminder_method", "invalid_reminder_offsets", "reminder_conflict",
      "telegram_unavailable", "phone_required", "call_reminders_disabled", "telegram_reminders_disabled", "call_task_not_found", "invalid_day",
      "invalid_status", "invalid_note", "task_already_closed", "task_cancelled", "invalid_quiet_hours", "settings_incomplete",
    ];
    for (const code of codes) {
      const text = humanizeError(new BeautyApiError(409, code, "SERVER ENGLISH 380501112233"));
      expect(text, code).toMatch(/[А-Яа-яІіЇїЄє]/);
      expect(text, code).not.toMatch(/SERVER|380/);
    }
  });
});
