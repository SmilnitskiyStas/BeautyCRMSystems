import { beforeEach, describe, expect, it, vi } from "vitest";

type Init = { method?: string; body?: string; cache?: string };
const apiJson = vi.fn<(path: string, init?: Init) => Promise<unknown>>();
vi.mock("@/features/beauty-auth/session", () => ({
  apiJson: (path: string, init?: Init) => apiJson(path, init),
  getUser: () => ({ id: "u", role: "owner", specialistId: null }),
}));

import { httpBeautyApi } from "./http-client";

const TG_ROW = { id: "ch-1", type: "telegram", name: "Telegram", locationId: null, isActive: true, maskedToken: "••••1234" };
beforeEach(() => {
  apiJson.mockReset();
  apiJson.mockResolvedValue(undefined);
});
const last = () => apiJson.mock.calls[apiJson.mock.calls.length - 1];

describe("http-клієнт §23: кнопки", () => {
  it("getConversation: беруться лише відомі поля; buttons - рядки; callback_data і токени відкидаються", async () => {
    apiJson.mockResolvedValueOnce({
      conversation: { id: "c1" },
      messages: [
        { id: "m1", direction: "outbound", senderType: "ai", kind: "text", body: "Підтверджуєте?", status: "sent", createdAt: "2026-10-25T10:00:00Z", buttons: ["Так", "", 5, "Ні"], callback_data: "by:AAAA:0", token: "AAAA" },
        { id: "m2", direction: "inbound", senderType: "client", kind: "callback", body: "[кнопка] Так", pressedLabel: "Так", status: "processed", createdAt: "2026-10-25T10:01:00Z", actions: { rows: [] } },
      ],
      upcomingAppointments: [{ id: "a1", startsAt: "2026-10-26T09:00:00Z", serviceName: "S", locationName: "L", reminders: { method: "none", offsetsMinutes: [] }, clientConfirmedAt: "2026-10-25T08:00:00Z" }],
    });
    const d = await httpBeautyApi.getConversation("c1");
    expect(d.messages[0].buttons).toEqual(["Так", "Ні"]);
    expect(d.messages[1]).toMatchObject({ kind: "callback", pressedLabel: "Так" });
    expect(JSON.stringify(d)).not.toMatch(/callback_data|AAAA|actions/);
    expect(d.upcomingAppointments[0].clientConfirmedAt).toBe("2026-10-25T08:00:00Z");
  });

  it("перемикач «Кнопки в чаті» пише settingsJson.assistant.buttons і зберігає assistant.enabled та чужі ключі", async () => {
    const row = { ...TG_ROW, settingsJson: JSON.stringify({ inbox: true, assistant: { enabled: true }, custom: { x: 1 } }) };
    apiJson.mockResolvedValueOnce([row]).mockResolvedValueOnce({ ...row, settingsJson: "{}" });
    await httpBeautyApi.updateChannel("tg", { buttons: false });
    const [path, init] = last();
    expect(path).toBe("/api/beauty/channels/ch-1");
    expect(init).toMatchObject({ method: "PUT" });
    const body = JSON.parse(init!.body!);
    const settings = JSON.parse(body.settingsJson);
    expect(settings.assistant).toEqual({ enabled: true, buttons: false });
    expect(settings.custom).toEqual({ x: 1 });
    expect(body.token).toBeNull();
  });

  it("звичайна зміна налаштування не губить assistant", async () => {
    const row = { ...TG_ROW, settingsJson: JSON.stringify({ assistant: { enabled: true, buttons: false } }) };
    apiJson.mockResolvedValueOnce([row]).mockResolvedValueOnce(row);
    await httpBeautyApi.updateChannel("tg", { greeting: "Привіт" });
    expect(JSON.parse(JSON.parse(last()[1]!.body!).settingsJson).assistant).toEqual({ enabled: true, buttons: false });
  });

  it("getChannels: buttons за замовчуванням true, assistantEnabled лише при enabled=true", async () => {
    apiJson.mockResolvedValueOnce([{ ...TG_ROW, settingsJson: JSON.stringify({ assistant: { enabled: true } }) }]);
    expect((await httpBeautyApi.getChannels()).find((c) => c.id === "tg")).toMatchObject({ assistantEnabled: true, buttons: true });
    apiJson.mockResolvedValueOnce([{ ...TG_ROW, settingsJson: JSON.stringify({ assistant: { enabled: true, buttons: false } }) }]);
    expect((await httpBeautyApi.getChannels()).find((c) => c.id === "tg")).toMatchObject({ buttons: false });
    apiJson.mockResolvedValueOnce([{ ...TG_ROW, settingsJson: null }]);
    expect((await httpBeautyApi.getChannels()).find((c) => c.id === "tg")).toMatchObject({ assistantEnabled: false });
  });

  it("syncTelegramCommands: POST /channels/{id}/telegram/sync-commands без тіла; без каналу - 404 channel_not_found", async () => {
    apiJson.mockResolvedValueOnce([TG_ROW]).mockResolvedValueOnce(undefined);
    await httpBeautyApi.syncTelegramCommands("tg");
    expect(last()[0]).toBe("/api/beauty/channels/ch-1/telegram/sync-commands");
    expect(last()[1]).toMatchObject({ method: "POST" });
    expect(last()[1]!.body).toBeUndefined();
    apiJson.mockResolvedValueOnce([]);
    await expect(httpBeautyApi.syncTelegramCommands("tg")).rejects.toMatchObject({ status: 404, code: "channel_not_found" });
  });
});
