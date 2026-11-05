import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { BeautyApiError, humanizeResolveError } from "@/features/beauty-auth/errors";
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

function setup(role = "owner") {
  h.auth.user.role = role;
  const channels = structuredClone(INITIAL_CHANNELS);
  const appts = appointmentsSeed();
  real = createMockInbox({
    role: () => role,
    wait: false,
    locations: () => DEMO_LOCATIONS,
    appointments: () => appts,
    channels: () => channels,
    staffName: (id) => STAFF.find((m) => m.id === id)?.name ?? "",
  });
  const names = ["getConversations", "getConversation", "replyToConversation", "resumeConversationAi", "resolveConversation", "markConversationRead"] as const;
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

const tab = () => screen.getByRole("button", { name: /Потрібна відповідь/ });
const needsCount = () => Number(/\((\d+)\)/.exec(tab().textContent ?? "")?.[1]);
const menuBtn = () => screen.getByRole("button", { name: /^Дії/ });
const openMenu = async () => {
  await userEvent.click(await screen.findByRole("button", { name: /^Дії/ }));
  return screen.getByRole("menu");
};

beforeEach(() => setup());
afterEach(() => vi.restoreAllMocks());

describe("«Опрацьовано» у меню «Дії»", () => {
  it("меню є в розмові, що чекає (handoff/needsReply), і немає в активній без очікування", async () => {
    renderInbox();
    await userEvent.click(await screen.findByRole("button", { name: /Софія Мартин/ }));
    const btn = await screen.findByRole("button", { name: /^Дії/ });
    expect(btn).toHaveAttribute("aria-haspopup", "menu");
    expect(btn).toHaveAttribute("aria-expanded", "false");
    expect(screen.queryByRole("menu")).not.toBeInTheDocument();
    await userEvent.click(btn);
    expect(btn).toHaveAttribute("aria-expanded", "true");
    const menu = screen.getByRole("menu");
    expect(within(menu).getByRole("menuitem", { name: /^Опрацьовано/ })).toHaveAccessibleDescription(/Закрити без відповіді/);
    expect(within(menu).getByRole("menuitem", { name: /^Повернути ШІ/ })).toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: "Усі" }));
    await userEvent.click(await screen.findByRole("button", { name: /Олена Кравченко/ }));
    await screen.findByRole("heading", { level: 2, name: "Олена Кравченко" });
    expect(screen.queryByRole("button", { name: /^Дії/ })).not.toBeInTheDocument();
  });

  it("виклик, зникнення з вкладки, зменшення лічильника, повідомлення aria-live і фокус на заголовок", async () => {
    renderInbox();
    const list = await screen.findByRole("list", { name: "Список розмов" });
    await waitFor(() => expect(needsCount()).toBeGreaterThan(0));
    const before = needsCount();
    await userEvent.click(within(list).getByRole("button", { name: /Софія Мартин/ }));
    await openMenu();
    await userEvent.click(screen.getByRole("menuitem", { name: /^Опрацьовано/ }));
    await waitFor(() => expect(spies.resolveConversation).toHaveBeenCalledWith("cv2"));
    expect(spies.resolveConversation.mock.calls[0]).toHaveLength(1);
    expect(await within(screen.getByTestId("resolve-status")).findByText("Розмову позначено опрацьованою")).toBeInTheDocument();
    expect(screen.getByTestId("resolve-status")).toHaveAttribute("role", "status");
    expect(screen.getByRole("heading", { level: 2, name: "Софія Мартин" })).toHaveFocus();
    await waitFor(() => expect(needsCount()).toBe(before - 1));
    await waitFor(() =>
      expect(within(screen.getByRole("list", { name: "Список розмов" })).queryByRole("button", { name: /Софія Мартин/ })).not.toBeInTheDocument(),
    );
    expect(screen.queryByRole("button", { name: /^Дії/ })).not.toBeInTheDocument();
    await waitFor(() => expect(screen.getByTestId("ai-state")).toHaveTextContent("ШІ відповідає"));
  });

  it.each([
    [403, "forbidden_role", "немає прав"],
    [404, "conversation_not_found", "не знайдено"],
    [409, "conflict", "Стан розмови вже змінився"],
    [429, "rate_limited", "Забагато запитів"],
  ])("помилка %i показується українським текстом без серверного message", async (status, code, text) => {
    spies.resolveConversation.mockRejectedValueOnce(new BeautyApiError(status, code, "English server message"));
    renderInbox();
    await userEvent.click(await screen.findByRole("button", { name: /Софія Мартин/ }));
    await openMenu();
    await userEvent.click(screen.getByRole("menuitem", { name: /^Опрацьовано/ }));
    const alert = await screen.findByTestId("resolve-error");
    expect(alert).toHaveTextContent(text);
    expect(alert).not.toHaveTextContent("English");
    expect(menuBtn()).toBeEnabled();
    expect(screen.queryByText("Розмову позначено опрацьованою")).not.toBeInTheDocument();
  });

  it("specialist не бачить розділ і не викликає API", async () => {
    setup("specialist");
    renderInbox();
    expect(screen.getByRole("note")).toHaveTextContent("лише власнику й адміністратору");
    expect(screen.queryByRole("button", { name: /^Дії/ })).not.toBeInTheDocument();
    expect(spies.resolveConversation).not.toHaveBeenCalled();
  });

  it("admin бачить меню", async () => {
    setup("admin");
    renderInbox();
    await userEvent.click(await screen.findByRole("button", { name: /Софія Мартин/ }));
    expect(await screen.findByRole("button", { name: /^Дії/ })).toBeInTheDocument();
  });
});

describe("меню «Дії»", () => {
  const openCv2 = async () => {
    renderInbox();
    await userEvent.click(await screen.findByRole("button", { name: /Софія Мартин/ }));
    await screen.findByRole("button", { name: /^Дії/ });
  };

  it("клавіатура: ArrowDown відкриває на першому пункті, стрілки/Home/End, Esc повертає фокус на кнопку", async () => {
    await openCv2();
    menuBtn().focus();
    await userEvent.keyboard("{ArrowDown}");
    const items = await screen.findAllByRole("menuitem");
    expect(items).toHaveLength(2);
    await waitFor(() => expect(items[0]).toHaveFocus());
    await userEvent.keyboard("{ArrowDown}");
    expect(items[1]).toHaveFocus();
    await userEvent.keyboard("{ArrowDown}");
    expect(items[0]).toHaveFocus();
    await userEvent.keyboard("{ArrowUp}");
    expect(items[1]).toHaveFocus();
    await userEvent.keyboard("{Home}");
    expect(items[0]).toHaveFocus();
    await userEvent.keyboard("{End}");
    expect(items[1]).toHaveFocus();
    await userEvent.keyboard("{Escape}");
    expect(screen.queryByRole("menu")).not.toBeInTheDocument();
    expect(menuBtn()).toHaveFocus();
    expect(menuBtn()).toHaveAttribute("aria-expanded", "false");
  });

  it("Enter на кнопці відкриває меню; клік поза меню закриває", async () => {
    await openCv2();
    menuBtn().focus();
    await userEvent.keyboard("{Enter}");
    expect(screen.getByRole("menu")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("heading", { level: 2, name: "Софія Мартин" }));
    expect(screen.queryByRole("menu")).not.toBeInTheDocument();
  });

  it("пункти мають точні підказки як опис", async () => {
    await openCv2();
    const menu = await openMenu();
    expect(within(menu).getByRole("menuitem", { name: /^Повернути ШІ/ })).toHaveAccessibleDescription(/ШІ знову відповідає клієнту/);
    expect(within(menu).getByRole("menuitem", { name: /^Опрацьовано/ })).toHaveAccessibleDescription(/прибрати з «Потрібна відповідь»/);
  });

  it("«Повернути ШІ» з меню відкриває підтвердження й викликає resume-ai", async () => {
    await openCv2();
    await openMenu();
    await userEvent.click(screen.getByRole("menuitem", { name: /^Повернути ШІ/ }));
    await userEvent.click(within(screen.getByRole("alertdialog")).getByRole("button", { name: "Повернути ШІ" }));
    await waitFor(() => expect(spies.resumeConversationAi).toHaveBeenCalledWith("cv2"));
  });

  it("під час запиту кнопка показує «…», заблокована й не дублює виклик", async () => {
    let release: () => void = () => {};
    spies.resolveConversation.mockImplementationOnce(() => new Promise<void>((r) => (release = r)));
    await openCv2();
    await openMenu();
    await userEvent.click(screen.getByRole("menuitem", { name: /^Опрацьовано/ }));
    const btn = await screen.findByRole("button", { name: /…/ });
    expect(btn).toBeDisabled();
    await userEvent.click(btn);
    expect(spies.resolveConversation).toHaveBeenCalledTimes(1);
    release();
    await waitFor(() => expect(screen.getByTestId("resolve-status")).toHaveTextContent("опрацьованою"));
  });
});

describe("humanizeResolveError", () => {
  it("мережа й невідомі помилки", () => {
    expect(humanizeResolveError(new TypeError("x"))).toMatch(/Сервер недоступний/);
    expect(humanizeResolveError(new BeautyApiError(400, "x", "m"))).toMatch(/Не вдалося позначити/);
  });
});

describe("mock resolveConversation", () => {
  it("handoff+очікування -> ШІ active, needsReply=false, прочитано, лічильник зменшується", async () => {
    const before = (await real.getConversations({ filter: "needs_reply" })).meta.needsReplyCount;
    await real.resolveConversation("cv2");
    const d = await real.getConversation("cv2");
    expect(d.conversation.aiState).toBe("active");
    expect(d.conversation.awaitingStaffSince).toBeUndefined();
    expect(d.conversation.unreadCount).toBe(0);
    expect(d.messages.some((m) => m.status === "held")).toBe(false);
    const after = await real.getConversations({ filter: "needs_reply" });
    expect(after.meta.needsReplyCount).toBe(before - 1);
    expect(after.items.some((c) => c.id === "cv2")).toBe(false);
  });

  it("specialist -> 403, невідома розмова -> 404", async () => {
    setup("specialist");
    await expect(real.resolveConversation("cv2")).rejects.toMatchObject({ status: 403 });
    setup("owner");
    await expect(real.resolveConversation("nope")).rejects.toMatchObject({ status: 404, code: "conversation_not_found" });
  });
});
