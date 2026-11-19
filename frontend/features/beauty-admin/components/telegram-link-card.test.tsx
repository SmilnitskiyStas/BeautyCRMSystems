import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("next/navigation", () => ({ useRouter: () => ({ push: vi.fn(), replace: vi.fn() }), usePathname: () => "/beauty/settings/notifications" }));

import { ROLE_PRESETS } from "@/features/beauty-auth/access";
import { createMockInventoryV3 } from "../api/mock-inventory-v3";

const auth = vi.hoisted(() => ({ user: { id: "u", email: "e", fullName: "n", role: "owner", specialistId: null as string | null, permissions: undefined as Record<string, string> | undefined, modules: undefined as string[] | undefined } }));
vi.mock("@/features/beauty-auth/components/auth-provider", () => ({ useAuth: () => auth }));

const state = vi.hoisted(() => ({ api: null as unknown }));
vi.mock("../api", () => ({ beautyApi: new Proxy({}, { get: (_t, k) => (state.api as Record<string | symbol, unknown>)[k] }) }));

import { TelegramLinkCard } from "./telegram-link-card";
import { TelegramNotificationsScreen } from "./telegram-notifications-screen";

const ONE = [{ id: "c1", name: "Бот салону" }];
const TWO = [...ONE, { id: "c2", name: "Бот барбершопу" }];

const env = { enabled: true, channels: ONE as { id: string; name: string }[] };
let mock: ReturnType<typeof createMockInventoryV3>;
let getSpy: ReturnType<typeof vi.fn>;
let qc: QueryClient;

function setRole(role: "owner" | "admin" | "deputy" | "specialist", modules?: string[]) {
  auth.user.role = role;
  auth.user.permissions = { ...ROLE_PRESETS[role] };
  auth.user.modules = modules;
}
function renderUi(ui = <TelegramLinkCard />) {
  qc = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(<QueryClientProvider client={qc}>{ui}</QueryClientProvider>);
}
const refresh = () => act(async () => void (await qc.invalidateQueries()));

beforeEach(() => {
  setRole("owner");
  env.enabled = true;
  env.channels = ONE;
  mock = createMockInventoryV3({ role: () => auth.user.role, channels: () => env.channels, featureEnabled: () => env.enabled });
  getSpy = vi.fn(mock.getTelegramLink);
  state.api = { ...mock, getTelegramLink: getSpy };
  localStorage.clear();
  sessionStorage.clear();
});
afterEach(() => cleanup());

async function connect(user: ReturnType<typeof userEvent.setup>) {
  await user.click(await screen.findByRole("checkbox", { name: /Я прочитав/ }));
  await user.click(screen.getByRole("button", { name: "Підключити" }));
}

describe("TelegramLinkCard", () => {
  it("none: повний текст згоди, чекбокс не відмічений, «Підключити» неактивна з поясненням", async () => {
    const user = userEvent.setup();
    renderUi();
    const box = await screen.findByRole("checkbox", { name: /Я прочитав/ });
    expect((box as HTMLInputElement).checked).toBe(false);
    const btn = screen.getByRole("button", { name: "Підключити" }) as HTMLButtonElement;
    expect(btn.disabled).toBe(true);
    expect(screen.getByText(/поставте позначку згоди/)).toBeTruthy();
    expect(screen.getByText(/Дані клієнтів у сповіщеннях не надсилаються/)).toBeTruthy();
    // aria-describedby чекбокса веде на текст згоди
    const desc = box.getAttribute("aria-describedby")!;
    expect(document.getElementById(desc)?.textContent).toMatch(/зашифрованому вигляді/);
    expect(screen.getByRole("heading", { name: /Стан: Не підключено/ })).toBeTruthy();
    await user.click(box);
    expect(btn.disabled).toBe(false);
  });

  it("підключення: посилання з rel/target, відлік, токен не потрапляє в storage/URL/console", async () => {
    const user = userEvent.setup();
    const logs = [vi.spyOn(console, "log"), vi.spyOn(console, "info"), vi.spyOn(console, "warn"), vi.spyOn(console, "error"), vi.spyOn(console, "debug")];
    const href0 = location.href;
    renderUi();
    await connect(user);
    const link = await screen.findByRole("link", { name: /Відкрити Telegram/ });
    expect(link.getAttribute("target")).toBe("_blank");
    expect(link.getAttribute("rel")).toBe("noopener noreferrer");
    const url = link.getAttribute("href")!;
    expect(url).toMatch(/^https:\/\/t\.me\/.+\?start=inv_/);
    expect(screen.getByRole("heading", { name: /Очікує підтвердження в боті/ })).toBeTruthy();
    expect(screen.getByText(/Діє до/)).toBeTruthy();
    expect(screen.getByText(/залишилось 1[45]:/)).toBeTruthy();
    expect(screen.getByRole("button", { name: "Створити нове посилання" })).toBeTruthy();
    expect(location.href).toBe(href0);
    expect(JSON.stringify({ ...localStorage })).not.toContain("inv_");
    expect(JSON.stringify({ ...sessionStorage })).not.toContain("inv_");
    expect(document.cookie).not.toContain("inv_");
    for (const l of logs) expect(JSON.stringify(l.mock.calls)).not.toContain("inv_");
    logs.forEach((l) => l.mockRestore());
  });

  it("копіювання посилання повідомляє про результат", async () => {
    const user = userEvent.setup();
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, "clipboard", { value: { writeText }, configurable: true });
    renderUi();
    await connect(user);
    await user.click(await screen.findByRole("button", { name: "Копіювати посилання" }));
    expect(writeText).toHaveBeenCalledWith(expect.stringContaining("t.me"));
    expect(await screen.findByText("Посилання скопійовано.")).toBeTruthy();
  });

  it("зміна стану: фокус на заголовок статусу, одне озвучення (live), без дублювання; посилання зникає в active", async () => {
    const user = userEvent.setup();
    renderUi();
    await connect(user);
    await screen.findByRole("link", { name: /Відкрити Telegram/ });
    mock.telegramScenario.simulateClaim();
    await refresh();
    const h = await screen.findByRole("heading", { name: /Підтвердьте в Telegram/ });
    await waitFor(() => expect(document.activeElement).toBe(h));
    const live = document.querySelector('[aria-live="polite"].sr-only')!;
    expect(live.textContent).toMatch(/Підтвердьте підключення в Telegram/);
    expect(screen.getAllByText(/Чат отримано/)).toHaveLength(2); // видимий опис + озвучення: різний текст, не однаковий рядок
    mock.telegramScenario.simulateConfirm();
    await refresh();
    expect(await screen.findByRole("heading", { name: /Стан: Підключено/ })).toBeTruthy();
    expect(screen.queryByRole("link", { name: /Відкрити Telegram/ })).toBeNull();
    expect(screen.getByText(/Підключено \d{2}\.\d{2}\.\d{4}/)).toBeTruthy();
    expect(screen.getByText(/Сповіщень ще не надсилали/)).toBeTruthy();
    expect(live.textContent).toBe("Telegram-сповіщення підключено.");
    // відлік не в live-області
    expect(live.closest("[aria-live]")).toBe(live);
  });

  it("відключення: діалог з підтвердженням, Esc скасовує й повертає фокус, підтвердження -> none", async () => {
    const user = userEvent.setup();
    renderUi();
    await connect(user);
    mock.telegramScenario.simulateClaim();
    mock.telegramScenario.simulateConfirm();
    await refresh();
    const open = await screen.findByRole("button", { name: "Відключити" });
    await user.click(open);
    const dialog = await screen.findByRole("dialog", { name: "Відключити Telegram-сповіщення?" });
    expect(dialog.getAttribute("aria-modal")).toBe("true");
    await user.keyboard("{Escape}");
    expect(screen.queryByRole("dialog")).toBeNull();
    await waitFor(() => expect(document.activeElement).toBe(open));
    await user.click(open);
    await user.click(within(await screen.findByRole("dialog")).getByRole("button", { name: "Відключити" }));
    expect(await screen.findByRole("heading", { name: /Стан: Не підключено/ })).toBeTruthy();
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("кілька ботів: telegram_channel_required -> вибір каналу -> підключення", async () => {
    env.channels = TWO;
    const user = userEvent.setup();
    renderUi();
    await connect(user);
    expect((await screen.findByRole("alert")).textContent).toBe("Оберіть бота для сповіщень");
    expect(screen.getByRole("group", { name: "Оберіть бота для сповіщень" })).toBeTruthy();
    expect((screen.getByRole("button", { name: "Підключити" }) as HTMLButtonElement).disabled).toBe(true);
    expect(screen.getByText("Оберіть бота зі списку.")).toBeTruthy();
    await user.click(screen.getByRole("radio", { name: "Бот барбершопу" }));
    await user.click(screen.getByRole("button", { name: "Підключити" }));
    expect(await screen.findByRole("link", { name: /Відкрити Telegram/ })).toBeTruthy();
    expect(screen.getByText(/«Бот барбершопу»/)).toBeTruthy();
  });

  it("немає бота: людський текст помилки", async () => {
    env.channels = [];
    const user = userEvent.setup();
    renderUi();
    await connect(user);
    expect((await screen.findByRole("alert")).textContent).toMatch(/немає активного Telegram-бота/);
  });

  it("прапор вимкнено: «Поки недоступно», підключення немає; активна прив'язка ще має «Відключити»", async () => {
    const user = userEvent.setup();
    renderUi();
    await connect(user);
    mock.telegramScenario.simulateClaim();
    mock.telegramScenario.simulateConfirm();
    cleanup();
    env.enabled = false;
    renderUi();
    expect(await screen.findByText(/Поки недоступно/)).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Підключити" })).toBeNull();
    expect(screen.queryByRole("checkbox")).toBeNull();
    expect(screen.getByRole("button", { name: "Відключити" })).toBeTruthy();
    cleanup();
    await mock.deleteTelegramLink();
    renderUi();
    expect(await screen.findByText(/Поки недоступно/)).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Відключити" })).toBeNull();
  });

  it("відкликано з причиною: банер текстом (bot_blocked), стан none", async () => {
    const user = userEvent.setup();
    renderUi();
    await connect(user);
    mock.telegramScenario.simulateClaim();
    mock.telegramScenario.simulateConfirm();
    mock.telegramScenario.simulateRevoke("bot_blocked");
    cleanup();
    renderUi();
    expect((await screen.findByTestId("tg-revoke-banner")).textContent).toBe("Увага: Бот заблоковано у Telegram — підключіть знову.");
    expect(screen.getByRole("heading", { name: /Стан: Не підключено/ })).toBeTruthy();
  });

  it("після перезавантаження сторінки у стані pending посилання недоступне (токен не відновлюється)", async () => {
    const user = userEvent.setup();
    renderUi();
    await connect(user);
    await screen.findByRole("link", { name: /Відкрити Telegram/ });
    cleanup();
    renderUi();
    expect(await screen.findByText(/більше недоступне/)).toBeTruthy();
    expect(screen.queryByRole("link", { name: /Відкрити Telegram/ })).toBeNull();
  });

  it("I4: deepLink не з https://t.me/ не потрапляє в href (посилання не показується)", async () => {
    const user = userEvent.setup();
    state.api = { ...mock, getTelegramLink: getSpy, createTelegramLink: async (r: Parameters<typeof mock.createTelegramLink>[0]) => ({ ...(await mock.createTelegramLink(r)), deepLink: "javascript:alert(1)" }) };
    renderUi();
    await connect(user);
    await screen.findByText(/Посилання показується один раз/);
    expect(screen.queryByRole("link", { name: /Відкрити Telegram/ })).toBeNull();
  });

  it("L1: показує час підтвердження в боті й нагадування про «Відключити» (awaitingConfirmation, active)", async () => {
    const user = userEvent.setup();
    renderUi();
    await connect(user);
    await screen.findByRole("link", { name: /Відкрити Telegram/ });
    mock.telegramScenario.simulateClaim();
    await refresh();
    await screen.findByRole("heading", { name: /Підтвердьте в Telegram/ });
    expect(screen.getByText(/Підтверджено в боті: \d{2}\.\d{2}\.\d{4}/)).toBeTruthy();
    expect(screen.getByText(/Якщо це були не ви — натисніть «Відключити»/)).toBeTruthy();
    mock.telegramScenario.simulateConfirm();
    await refresh();
    await screen.findByRole("heading", { name: /Стан: Підключено/ });
    expect(screen.getByText(/Підтверджено в боті: /)).toBeTruthy();
    expect(screen.getByText(/Якщо це були не ви — натисніть «Відключити»/)).toBeTruthy();
  });

  it("права: не owner / модуль вимкнено -> картка не рендериться і запитів немає", async () => {
    for (const role of ["admin", "deputy", "specialist"] as const) {
      setRole(role);
      const { container } = renderUi();
      expect(container.textContent).toBe("");
      cleanup();
    }
    setRole("owner", ["beauty_clients"]);
    const { container } = renderUi();
    expect(container.textContent).toBe("");
    expect(getSpy).not.toHaveBeenCalled();
  });

  it("екран: для не-owner пояснення без картки", async () => {
    setRole("admin");
    renderUi(<TelegramNotificationsScreen />);
    expect(screen.getByText(/доступні лише власнику/)).toBeTruthy();
    expect(screen.queryByRole("heading", { name: "Telegram-сповіщення складу" })).toBeNull();
    cleanup();
    setRole("owner");
    renderUi(<TelegramNotificationsScreen />);
    expect(await screen.findByRole("heading", { name: "Telegram-сповіщення складу" })).toBeTruthy();
  });
});
