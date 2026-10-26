import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { BeautyApiError } from "@/features/beauty-auth/errors";
import { DEMO_LOCATIONS, STAFF, appointmentsSeed } from "../../beauty-shared/demo-seed";
import { INITIAL_CHANNELS } from "../api/mock-data";
import { createMockInbox } from "../api/mock-inbox";

const h = vi.hoisted(() => ({
  api: {} as Record<string, (...a: unknown[]) => unknown>,
  auth: { user: { id: "u", email: "e", fullName: "n", role: "owner" } },
}));

vi.mock("@/features/beauty-auth/components/auth-provider", () => ({ useAuth: () => h.auth }));
vi.mock("../api", () => ({
  beautyApi: new Proxy({}, { get: (_t, k: string) => (...a: unknown[]) => h.api[k](...a) }),
}));

import { InboxScreen } from "./inbox-screen";

type Spies = Record<string, ReturnType<typeof vi.fn>>;
let spies: Spies;
let real: ReturnType<typeof createMockInbox>;
let channels: typeof INITIAL_CHANNELS;

function setup(role = "owner") {
  h.auth.user.role = role;
  channels = structuredClone(INITIAL_CHANNELS);
  const appts = appointmentsSeed();
  real = createMockInbox({
    role: () => role,
    wait: false,
    locations: () => DEMO_LOCATIONS,
    appointments: () => appts,
    channels: () => channels,
    staffName: (id) => STAFF.find((m) => m.id === id)?.name ?? "",
  });
  const names = ["getConversations", "getConversation", "replyToConversation", "resumeConversationAi", "markConversationRead"] as const;
  spies = Object.fromEntries(names.map((k) => [k, vi.fn(real[k] as (...a: unknown[]) => unknown)]));
  spies.getLocations = vi.fn(async () => structuredClone(DEMO_LOCATIONS));
  h.api = spies as unknown as typeof h.api;
}

function renderInbox() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <InboxScreen />
    </QueryClientProvider>,
  );
}

const open = async (name: RegExp) => {
  await userEvent.click(await screen.findByRole("button", { name }));
  return screen.findByRole("heading", { level: 2, name: name.source.replace(/\\/g, "") });
};

beforeEach(() => setup());
afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
  Object.defineProperty(document, "visibilityState", { value: "visible", configurable: true });
});

describe("список розмов", () => {
  it("за замовчуванням «Потрібна відповідь» з лічильником, найдовше очікувана першою; «Усі» показує всі", async () => {
    renderInbox();
    const list = await screen.findByRole("list", { name: "Список розмов" });
    const titles = within(list).getAllByRole("button").map((b) => b.textContent ?? "");
    expect(titles).toHaveLength(2);
    expect(titles[0]).toContain("Катерина Ляшко");
    expect(titles[1]).toContain("Софія Мартин");
    expect(screen.getByRole("button", { name: "Потрібна відповідь (2)" })).toHaveAttribute("aria-pressed", "true");
    await userEvent.click(screen.getByRole("button", { name: "Усі" }));
    await waitFor(() => expect(within(screen.getByRole("list", { name: "Список розмов" })).getAllByRole("button")).toHaveLength(6));
    // Стан ШІ у рядку: активний / передано людині.
    const all = within(screen.getByRole("list", { name: "Список розмов" }));
    expect(all.getByRole("button", { name: /Олена Кравченко/ })).toHaveTextContent("ШІ відповідає");
    expect(all.getByRole("button", { name: /Софія Мартин/ })).toHaveTextContent("Передано адміністратору");
  });

  it("фільтр закладу передається в API; порожній стан має пояснення", async () => {
    renderInbox();
    await screen.findByRole("list", { name: "Список розмов" });
    await userEvent.selectOptions(screen.getByLabelText("Заклад"), "k");
    expect(await screen.findByText(/Немає розмов, що чекають на відповідь/)).toBeInTheDocument();
    expect(spies.getConversations).toHaveBeenLastCalledWith(expect.objectContaining({ filter: "needs_reply", locationId: "k" }));
  });
});

describe("передача від ШІ адміністратору: повний сценарій", () => {
  it("пауза з held: позначки, роздільник непрочитаних, «прочитано», відповідь менеджера додає повідомлення й знімає очікування", async () => {
    renderInbox();
    const heading = await open(/Катерина Ляшко/);
    expect(heading).toHaveFocus();
    expect(screen.getAllByText("очікує відповіді людини")).toHaveLength(3);
    expect(screen.getByTestId("unread-divider")).toBeInTheDocument();
    const state = screen.getByTestId("ai-state");
    expect(state).toHaveTextContent("Передано адміністратору");
    expect(state).toHaveTextContent("клієнт попросив людину");
    expect(state).toHaveTextContent("Пауза до");
    await waitFor(() => expect(spies.markConversationRead).toHaveBeenCalledWith("cv3", "cv3-m5"));

    await userEvent.type(screen.getByLabelText("Відповідь клієнту в Telegram"), "Добрий день! Вівторок о 15:00 підійде.");
    await userEvent.click(screen.getByRole("button", { name: "Надіслати" }));
    expect(await screen.findByText("Добрий день! Вівторок о 15:00 підійде.")).toBeInTheDocument();
    const [text, key] = [spies.replyToConversation.mock.calls[0][1], spies.replyToConversation.mock.calls[0][2]];
    expect(text).toBe("Добрий день! Вівторок о 15:00 підійде.");
    expect(String(key).length).toBeGreaterThanOrEqual(8);
    await waitFor(() => expect(screen.queryAllByText("очікує відповіді людини")).toHaveLength(0));
    expect(screen.getByLabelText("Відповідь клієнту в Telegram")).toHaveValue("");
    expect(screen.getByTestId("ai-state")).toHaveTextContent("ШІ не втручається до «Повернути ШІ»");
    // Розмова пішла з вкладки «Потрібна відповідь»; бейдж-лічильник оновився.
    await waitFor(() => expect(screen.getByRole("button", { name: "Потрібна відповідь (1)" })).toBeInTheDocument());
  });

  it("Ctrl+Enter надсилає відповідь", async () => {
    renderInbox();
    await open(/Софія Мартин/);
    const area = screen.getByLabelText("Відповідь клієнту в Telegram");
    await userEvent.type(area, "Так, нагадаємо вам у Telegram.{Control>}{Enter}{/Control}");
    await waitFor(() => expect(spies.replyToConversation).toHaveBeenCalledTimes(1));
    expect(await screen.findByText("Так, нагадаємо вам у Telegram.")).toBeInTheDocument();
  });

  it("«Повернути ШІ»: підтвердження, скасування нічого не змінює, підтвердження повертає стан «ШІ відповідає»", async () => {
    renderInbox();
    await open(/Софія Мартин/);
    await userEvent.click(screen.getByRole("button", { name: "Повернути ШІ" }));
    const dialog = screen.getByRole("alertdialog");
    expect(within(dialog).getByRole("heading", { name: "Повернути розмову ШІ?" })).toHaveFocus();
    await userEvent.click(within(dialog).getByRole("button", { name: "Скасувати" }));
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
    expect(spies.resumeConversationAi).not.toHaveBeenCalled();
    expect(screen.getByRole("button", { name: "Повернути ШІ" })).toHaveFocus();

    await userEvent.click(screen.getByRole("button", { name: "Повернути ШІ" }));
    await userEvent.click(within(screen.getByRole("alertdialog")).getByRole("button", { name: "Повернути ШІ" }));
    await waitFor(() => expect(spies.resumeConversationAi).toHaveBeenCalledWith("cv2"));
    expect(await screen.findByText("ШІ знову відповідає в цій розмові.")).toBeInTheDocument();
    await waitFor(() => expect(screen.getByTestId("ai-state")).toHaveTextContent("ШІ відповідає"));
    expect(screen.queryByRole("button", { name: "Повернути ШІ" })).not.toBeInTheDocument();
  });

  it("повідомлення ШІ-чернетка, системне, не доставлено (бот заблоковано) мають свої позначки", async () => {
    renderInbox();
    await userEvent.click(await screen.findByRole("button", { name: "Усі" }));
    await userEvent.click(await screen.findByRole("button", { name: /Тетяна Гончар/ }));
    expect(await screen.findByText("не доставлено: клієнт заблокував бота")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: /← До списку розмов/ }));
    await userEvent.click(await screen.findByRole("button", { name: /Ірина Л\./ }));
    expect(await screen.findByText("чернетка, не надіслано клієнту")).toBeInTheDocument();
  });
});

describe("відповідь: обмеження й помилки з українськими текстами", () => {
  it("Instagram: поле відповіді замінено поясненням (channel_unsupported)", async () => {
    renderInbox();
    await userEvent.click(await screen.findByRole("button", { name: "Усі" }));
    await userEvent.click(await screen.findByRole("button", { name: /Ірина Л\./ }));
    expect(await screen.findByTestId("reply-blocked")).toHaveTextContent("лише Telegram");
    expect(screen.queryByLabelText("Відповідь клієнту в Telegram")).not.toBeInTheDocument();
  });

  it("вимкнений канал: пояснення channel_inactive", async () => {
    channels.find((c) => c.id === "tg")!.connected = false;
    renderInbox();
    await open(/Софія Мартин/);
    expect(await screen.findByTestId("reply-blocked")).toHaveTextContent("Канал вимкнено");
  });

  it("лічильник 3500: понад ліміт - кнопка вимкнена, є попередження", async () => {
    renderInbox();
    await open(/Софія Мартин/);
    const area = screen.getByLabelText("Відповідь клієнту в Telegram");
    fireEvent.change(area, { target: { value: "а".repeat(3500) } });
    expect(screen.getByText(/3500 \/ 3500/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Надіслати" })).toBeEnabled();
    fireEvent.change(area, { target: { value: "а".repeat(3501) } });
    expect(screen.getByText(/3501 \/ 3500 — задовго/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Надіслати" })).toBeDisabled();
    fireEvent.change(area, { target: { value: "   " } });
    expect(screen.getByRole("button", { name: "Надіслати" })).toBeDisabled();
  });

  it.each([
    [429, "rate_limited", /Забагато запитів.*через 30 с/],
    [409, "channel_inactive", /Канал вимкнено/],
    [409, "idempotency_conflict", /вже надсилалося з іншим текстом/],
    [422, "invalid_text", /від 1 до 3500 символів/],
    [404, "conversation_not_found", /Розмову не знайдено/],
  ])("помилка %i %s показується українською без тексту сервера", async (status, errCode, expected) => {
    renderInbox();
    await open(/Софія Мартин/);
    spies.replyToConversation.mockRejectedValueOnce(new BeautyApiError(status, errCode, "SERVER english text +380501112233", status === 429 ? 30 : undefined));
    await userEvent.type(screen.getByLabelText("Відповідь клієнту в Telegram"), "Привіт");
    await userEvent.click(screen.getByRole("button", { name: "Надіслати" }));
    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent(expected);
    expect(alert).not.toHaveTextContent("english");
    expect(alert).not.toHaveTextContent("+380");
    // Текст не втрачено: можна виправити й повторити.
    expect(screen.getByLabelText("Відповідь клієнту в Telegram")).toHaveValue("Привіт");
  });

  it("мережева помилка: повтор того самого тексту йде з тим самим Idempotency-Key, новий текст - з новим", async () => {
    renderInbox();
    await open(/Софія Мартин/);
    spies.replyToConversation.mockRejectedValueOnce(new TypeError("Failed to fetch"));
    const area = screen.getByLabelText("Відповідь клієнту в Telegram");
    await userEvent.type(area, "Перший текст");
    await userEvent.click(screen.getByRole("button", { name: "Надіслати" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("Сервер недоступний");
    await userEvent.click(screen.getByRole("button", { name: "Надіслати" }));
    await waitFor(() => expect(spies.replyToConversation).toHaveBeenCalledTimes(2));
    const [a, b] = spies.replyToConversation.mock.calls;
    expect(b[2]).toBe(a[2]);
    await screen.findByText("Перший текст");
    await userEvent.type(area, "Другий текст");
    await userEvent.click(screen.getByRole("button", { name: "Надіслати" }));
    await waitFor(() => expect(spies.replyToConversation).toHaveBeenCalledTimes(3));
    expect(spies.replyToConversation.mock.calls[2][2]).not.toBe(a[2]);
  });
});

describe("автооновлення опитуванням (без SSE)", () => {
  it("список і відкрита розмова оновлюються кожні 10 с, на прихованій вкладці опитування зупиняється", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    renderInbox();
    await screen.findByRole("list", { name: "Список розмов" });
    await userEvent.click(screen.getByRole("button", { name: /Софія Мартин/ }));
    await screen.findByRole("heading", { level: 2, name: "Софія Мартин" });
    const lists = () => spies.getConversations.mock.calls.length;
    const details = () => spies.getConversation.mock.calls.length;
    const l0 = lists();
    const d0 = details();
    await act(async () => {
      await vi.advanceTimersByTimeAsync(10_500);
    });
    expect(lists()).toBeGreaterThan(l0);
    expect(details()).toBeGreaterThan(d0);

    Object.defineProperty(document, "visibilityState", { value: "hidden", configurable: true });
    await act(async () => {
      document.dispatchEvent(new Event("visibilitychange"));
      await vi.advanceTimersByTimeAsync(100);
    });
    const l1 = lists();
    const d1 = details();
    await act(async () => {
      await vi.advanceTimersByTimeAsync(35_000);
    });
    expect(lists()).toBe(l1);
    expect(details()).toBe(d1);
  });

  it("нове повідомлення клієнта з'являється саме й потрапляє в зону aria-live", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    renderInbox();
    await screen.findByRole("list", { name: "Список розмов" });
    await userEvent.click(screen.getByRole("button", { name: /Софія Мартин/ }));
    const log = await screen.findByRole("log", { name: "Повідомлення розмови" });
    expect(log).toHaveAttribute("aria-live", "polite");
    expect(screen.queryByText("А ще підкажіть адресу")).not.toBeInTheDocument();
    // Сервер «отримав» нове повідомлення клієнта між опитуваннями.
    spies.getConversation.mockImplementation(async (id: unknown) => {
      const d = await real.getConversation(id as string);
      d.messages.push({ id: "new-1", direction: "inbound", senderType: "client", kind: "text", body: "А ще підкажіть адресу", status: "held", createdAt: new Date().toISOString() });
      return d;
    });
    await act(async () => {
      await vi.advanceTimersByTimeAsync(10_500);
    });
    expect(await within(screen.getByRole("log")).findByText("А ще підкажіть адресу")).toBeInTheDocument();
  });
});

describe("доступ", () => {
  it("specialist: пояснення замість розмов, жодного виклику API і жодних ПД у DOM", async () => {
    setup("specialist");
    const { container } = renderInbox();
    expect(screen.getByRole("note")).toHaveTextContent("лише власнику й адміністратору");
    await act(async () => {
      await new Promise((r) => setTimeout(r, 50));
    });
    expect(spies.getConversations).not.toHaveBeenCalled();
    expect(spies.getConversation).not.toHaveBeenCalled();
    for (const pd of ["Катерина", "Софія", "Олена", "Ляшко", "нагадаєте", "+380"]) expect(container.textContent).not.toContain(pd);
  });

  it("admin має доступ до розмов", async () => {
    setup("admin");
    renderInbox();
    expect(await screen.findByRole("list", { name: "Список розмов" })).toBeInTheDocument();
  });
});
