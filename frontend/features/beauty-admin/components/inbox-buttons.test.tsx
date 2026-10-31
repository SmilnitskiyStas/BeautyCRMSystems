import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
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

function setup(role: string) {
  h.auth.user.role = role;
  const channels = structuredClone(INITIAL_CHANNELS);
  // Марія (mp) підтвердила всі свої майбутні візити кнопкою.
  const appts = appointmentsSeed().map((a) => (a.clientId === "mp" ? { ...a, clientConfirmedAt: "2026-10-25T08:30:00Z" } : a));
  const real = createMockInbox({
    role: () => role,
    wait: false,
    locations: () => DEMO_LOCATIONS,
    appointments: () => appts,
    channels: () => channels,
    staffName: (id) => STAFF.find((m) => m.id === id)?.name ?? "",
  });
  h.api = {
    getConversations: real.getConversations as never,
    getConversation: real.getConversation as never,
    replyToConversation: real.replyToConversation as never,
    resumeConversationAi: real.resumeConversationAi as never,
    markConversationRead: real.markConversationRead as never,
    getLocations: (async () => structuredClone(DEMO_LOCATIONS)) as never,
  };
}

function renderInbox() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <InboxScreen />
    </QueryClientProvider>,
  );
}

async function openConversation(name: RegExp) {
  await userEvent.click(await screen.findByRole("button", { name: "Усі" }));
  await userEvent.click(await screen.findByRole("button", { name }));
  return screen.findByRole("heading", { level: 2, name: name.source.replace(/\\/g, "") });
}

afterEach(() => vi.restoreAllMocks());

describe.each(["owner", "admin"])("inbox: кнопки в чаті (%s)", (role) => {
  beforeEach(() => setup(role));

  it("натискання показується як «Клієнт натиснув: …», сирого callback немає ні в бульбашці, ні в списку", async () => {
    renderInbox();
    await openConversation(/Марія Пономаренко/);
    const log = screen.getByRole("log", { name: "Повідомлення розмови" });
    expect(within(log).getByText("Клієнт натиснув: ✅ Підтверджую візит")).toBeInTheDocument();
    expect(within(log).getByText("Клієнт натиснув: ❌ Скасувати візит")).toBeInTheDocument();
    expect(within(log).getByText("Клієнт натиснув: Так, скасувати")).toBeInTheDocument();
    expect(document.body.textContent).not.toContain("[кнопка]");
    // Превʼю в списку: останнє повідомлення розмови - системне; для Олени останнє - звичайний текст.
    const list = screen.getByRole("list", { name: "Список розмов" });
    expect(within(list).getByRole("button", { name: /Марія Пономаренко/ })).toBeInTheDocument();
  });

  it("кнопки вихідного повідомлення - неактивні чипи з підписами, без ролі button", async () => {
    renderInbox();
    await openConversation(/Марія Пономаренко/);
    const groups = screen.getAllByTestId("client-buttons");
    // Нагадування й підтвердження скасування + пропозиція згоди й кампанія (§26) = 4 групи кнопок.
    expect(groups).toHaveLength(4);
    expect(groups[0]).toHaveAccessibleName("Кнопки, які бачить клієнт (неактивні)");
    const labels = within(groups[0]).getAllByTestId("client-button").map((c) => c.textContent);
    expect(labels).toEqual(["✅ Підтверджую візит", "❌ Скасувати візит", "🔕 Більше не нагадувати"]);
    const log = screen.getByRole("log", { name: "Повідомлення розмови" });
    expect(within(log).queryByRole("button")).toBeNull();
    expect(within(log).queryByRole("link")).toBeNull();
  });

  it("у списку розмов превʼю натискання - «Клієнт натиснув: …»", async () => {
    renderInbox();
    await userEvent.click(await screen.findByRole("button", { name: "Усі" }));
    const list = await screen.findByRole("list", { name: "Список розмов" });
    // Олена: підсумок з кнопками + натискання «Так, записати» -> потім відповідь ШІ, тож превʼю - текст ШІ.
    expect(within(list).getByRole("button", { name: /Олена Кравченко/ })).toHaveTextContent("Готово, вас записано");
    expect(list.textContent).not.toContain("[кнопка]");
  });

  it("підтвердження візиту видно в «Найближчих записах»", async () => {
    renderInbox();
    await openConversation(/Марія Пономаренко/);
    const badge = await screen.findAllByTestId("client-confirmed");
    expect(badge.length).toBeGreaterThan(0);
    expect(badge[0]).toHaveTextContent(/Клієнт підтвердив візит/);
  });

  it("причина handoff policy_unknown - українською, без сирого коду", async () => {
    renderInbox();
    await openConversation(/Софія Мартин/);
    expect(screen.getByTestId("ai-state")).toHaveTextContent("питання про політику закладу");
    expect(document.body.textContent).not.toContain("policy_unknown");
  });

  it("у DOM немає callback_data, токенів і службових полів", async () => {
    renderInbox();
    await openConversation(/Марія Пономаренко/);
    const text = document.body.innerHTML;
    expect(text).not.toMatch(/callback_data|callbackData|inline_keyboard|"actions"|reply_markup/i);
    expect(text).not.toMatch(/\b(by|sp|rc|aa|ac|cy|cn):[A-Za-z0-9_-]{6,}/);
  });
});
