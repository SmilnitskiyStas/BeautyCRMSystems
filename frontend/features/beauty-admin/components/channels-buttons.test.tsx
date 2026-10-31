import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { BeautyApiError } from "@/features/beauty-auth/errors";
import { INITIAL_CHANNELS } from "../api/mock-data";
import type { ChannelConfig, ChannelPatch } from "../types";

vi.mock("next/link", () => ({ default: ({ children, href }: { children: React.ReactNode; href: string }) => <a href={href}>{children}</a> }));

const getChannels = vi.fn<() => Promise<ChannelConfig[]>>();
const updateChannel = vi.fn<(id: string, patch: ChannelPatch) => Promise<ChannelConfig>>();
const syncTelegramCommands = vi.fn<(id: string) => Promise<void>>();
vi.mock("../api", () => ({
  beautyApi: {
    getChannels: () => getChannels(),
    updateChannel: (id: string, patch: ChannelPatch) => updateChannel(id, patch),
    syncTelegramCommands: (id: string) => syncTelegramCommands(id),
  },
}));

import { ChannelsScreen } from "./channels-screen";

let channels: ChannelConfig[];
function renderScreen() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <ChannelsScreen />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  channels = structuredClone(INITIAL_CHANNELS);
  getChannels.mockReset().mockImplementation(async () => structuredClone(channels));
  updateChannel.mockReset().mockImplementation(async (id, patch) => {
    const c = channels.find((x) => x.id === id)!;
    Object.assign(c, patch);
    return structuredClone(c);
  });
  syncTelegramCommands.mockReset().mockResolvedValue(undefined);
});

describe("канал Telegram: кнопки в чаті (§23.10)", () => {
  it("перемикач «Кнопки в чаті» увімкнений за замовчуванням і пише buttons=false", async () => {
    const user = userEvent.setup();
    renderScreen();
    const sw = await screen.findByRole("switch", { name: "Кнопки в чаті" });
    expect(sw).toHaveAttribute("aria-checked", "true");
    await user.click(sw);
    await waitFor(() => expect(updateChannel).toHaveBeenCalledWith("tg", { buttons: false }));
    await waitFor(() => expect(screen.getByRole("switch", { name: "Кнопки в чаті" })).toHaveAttribute("aria-checked", "false"));
  });

  it("без assistant.enabled перемикач вимкнений і пояснює чому", async () => {
    channels[0].assistantEnabled = false;
    renderScreen();
    const sw = await screen.findByRole("switch", { name: "Кнопки в чаті" });
    expect(sw).toBeDisabled();
    expect(sw).toHaveAttribute("aria-checked", "false");
    expect(screen.getByTestId("telegram-buttons")).toHaveTextContent("чат-асистента");
  });

  it("блоку кнопок немає в інших каналах", async () => {
    const user = userEvent.setup();
    renderScreen();
    await screen.findByTestId("telegram-buttons");
    await user.click(screen.getByRole("button", { name: /Instagram/ }));
    expect(screen.queryByTestId("telegram-buttons")).not.toBeInTheDocument();
  });

  it("«Оновити меню команд бота»: запит для tg, статус успіху; токена в UI немає", async () => {
    const user = userEvent.setup();
    renderScreen();
    await user.click(await screen.findByRole("button", { name: "Оновити меню команд бота" }));
    await waitFor(() => expect(syncTelegramCommands).toHaveBeenCalledWith("tg"));
    expect(await screen.findByRole("status")).toHaveTextContent("Меню команд бота оновлено.");
    expect(document.body.textContent).not.toMatch(/\d{6,}:[A-Za-z0-9_-]{20,}/);
  });

  it.each([
    [404, "channel_not_found", "Канал не знайдено"],
    [409, "channel_unsupported", "лише для Telegram-бота"],
    [409, "channel_inactive", "Канал вимкнено"],
    [409, "token_missing", "не вказано токен"],
    [502, "telegram_unavailable", "Telegram зараз недоступний"],
  ])("помилка %i %s - зрозумілий текст українською без серверного message", async (status, code, expected) => {
    syncTelegramCommands.mockRejectedValue(new BeautyApiError(status, code, "raw english message bot123456:ABCDEFGHIJKLMNOPQRSTUVWXYZ"));
    const user = userEvent.setup();
    renderScreen();
    await user.click(await screen.findByRole("button", { name: "Оновити меню команд бота" }));
    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent(expected);
    expect(alert).not.toHaveTextContent(/raw english|ABCDEFGHIJ/);
  });
});
