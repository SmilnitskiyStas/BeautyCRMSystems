import { beforeEach, describe, expect, it, vi } from "vitest";

type Init = { method?: string; body?: string; cache?: string };
const apiJson = vi.fn<(path: string, init?: Init) => Promise<unknown>>();
vi.mock("@/features/beauty-auth/session", () => ({
  apiJson: (path: string, init?: Init) => apiJson(path, init),
  getUser: () => ({ id: "u", role: "owner", specialistId: null }),
}));

import { httpBeautyApi } from "./http-client";

const LOC = { id: "l1", name: "Центр", address: null, phone: null, timezone: "Europe/Kyiv", isActive: true };
const base = { name: "Центр", address: "", phone: "", timezone: "Europe/Kyiv", isActive: true };
const TG = (settings: unknown) => ({ id: "ch-1", type: "telegram", name: "Telegram", locationId: null, isActive: true, maskedToken: "••••1234", settingsJson: JSON.stringify(settings) });
const last = () => apiJson.mock.calls[apiJson.mock.calls.length - 1];
const body = () => JSON.parse(last()[1]!.body!);

beforeEach(() => {
  apiJson.mockReset();
  apiJson.mockResolvedValue(LOC);
});

describe("http-клієнт §28.11: координати закладу", () => {
  it("PUT без полів координат не чіпає їх", async () => {
    await httpBeautyApi.updateLocation("l1", base);
    expect(body()).not.toHaveProperty("latitude");
    expect(body()).not.toHaveProperty("longitude");
  });

  it("PUT з парою чисел і парою null", async () => {
    await httpBeautyApi.updateLocation("l1", { ...base, latitude: 50.45, longitude: 30.52 });
    expect(body()).toMatchObject({ latitude: 50.45, longitude: 30.52 });
    await httpBeautyApi.updateLocation("l1", { ...base, latitude: null, longitude: null });
    expect(body()).toMatchObject({ latitude: null, longitude: null });
  });

  it("POST шле координати лише якщо задано", async () => {
    await httpBeautyApi.createLocation({ ...base, latitude: null, longitude: null });
    expect(body()).not.toHaveProperty("latitude");
    await httpBeautyApi.createLocation({ ...base, latitude: 1, longitude: 2 });
    expect(body()).toMatchObject({ latitude: 1, longitude: 2 });
  });

  it("відповідь: числа зберігаються, відсутнє = null", async () => {
    apiJson.mockResolvedValueOnce([{ ...LOC, latitude: 50.4, longitude: 30.5 }, LOC]);
    const list = await httpBeautyApi.getLocations();
    expect(list[0]).toMatchObject({ latitude: 50.4, longitude: 30.5 });
    expect(list[1]).toMatchObject({ latitude: null, longitude: null });
  });
});

describe("http-клієнт §28.2: assistant.enabled / buttons / bookingFlow", () => {
  it("ввімкнення assistant.enabled зберігає чужі ключі й решту assistant", async () => {
    apiJson.mockResolvedValueOnce([TG({ foo: 1, assistant: { buttons: false, extra: "x" } })]);
    await httpBeautyApi.updateChannel("tg", { assistantEnabled: true });
    const settings = JSON.parse(body().settingsJson);
    expect(settings.foo).toBe(1);
    expect(settings.assistant).toEqual({ enabled: true, buttons: false, extra: "x" });
  });

  it("bookingFlow пишеться поруч з enabled/buttons", async () => {
    apiJson.mockResolvedValueOnce([TG({ assistant: { enabled: true, buttons: true } })]);
    await httpBeautyApi.updateChannel("tg", { bookingFlow: false });
    expect(JSON.parse(body().settingsJson).assistant).toEqual({ enabled: true, buttons: true, bookingFlow: false });
  });

  it("getChannels: відсутнє bookingFlow = true", async () => {
    apiJson.mockResolvedValueOnce([TG({ assistant: { enabled: true } })]);
    expect((await httpBeautyApi.getChannels()).find((c) => c.id === "tg")).toMatchObject({ bookingFlow: true });
    apiJson.mockResolvedValueOnce([TG({ assistant: { enabled: true, bookingFlow: false } })]);
    expect((await httpBeautyApi.getChannels()).find((c) => c.id === "tg")).toMatchObject({ bookingFlow: false });
  });
});
