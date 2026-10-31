import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { BeautyApiError } from "@/features/beauty-auth/errors";
import type { CallTaskList, ClientPhoneInfo, ClientProfile, ConversationDetail, ConversationList } from "../types";

const role = { current: "owner" };
let phoneInfo: ClientPhoneInfo | undefined;
const resolvePhonePending = vi.fn();
const getClient = vi.fn<(id: string) => Promise<ClientProfile | null>>();
const getContactPreferences = vi.fn();
const getConversation = vi.fn<(id: string) => Promise<ConversationDetail>>();
const getConversations = vi.fn<() => Promise<ConversationList>>();
const markConversationRead = vi.fn();
const getCallTasks = vi.fn<() => Promise<CallTaskList>>();
const getLocations = vi.fn();
vi.mock("../api", () => ({
  beautyApi: {
    getClient: (id: string) => getClient(id),
    getContactPreferences: () => getContactPreferences(),
    resolvePhonePending: (...a: unknown[]) => resolvePhonePending(...a),
    getConversation: (id: string) => getConversation(id),
    getConversations: () => getConversations(),
    markConversationRead: (...a: unknown[]) => markConversationRead(...a),
    getCallTasks: () => getCallTasks(),
    getLocations: () => getLocations(),
  },
}));
vi.mock("@/features/beauty-auth/components/auth-provider", () => ({ useAuth: () => ({ user: { id: "u", email: "e", fullName: "n", role: role.current } }) }));

import { CallsScreen } from "./calls-screen";
import { ClientProfileScreen } from "./clients-screens";
import { ConversationThread } from "./inbox-thread";
import { InboxScreen } from "./inbox-screen";

const profile = (): ClientProfile => ({
  id: "c1", name: "Катерина Ляшко", tag: "sleep", contactLine: "+380 50 000 00 03", kpis: [], visits: [], promos: [], notes: [], preferences: [], warning: null,
  loyalty: { balance: "—", nextLevel: "", progressPct: 0 }, cancelledCount: 0, cancelledByClientCount: 0, phoneInfo,
});

function wrap(ui: React.ReactNode) {
  return render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>{ui}</QueryClientProvider>);
}

beforeEach(() => {
  role.current = "owner";
  phoneInfo = {
    phone: "+380 50 000 00 03",
    phoneSource: "telegram_contact",
    callConsentAt: "2026-10-23T10:00:00",
    phonePending: { phone: "+380 67 111 22 33", source: "manual_chat", at: "2026-10-26T09:00:00" },
    possibleDuplicate: { id: "c2", name: "Софія Мартин" },
  };
  getClient.mockReset().mockImplementation(async () => profile());
  getContactPreferences.mockReset().mockResolvedValue({ state: "unset", offsetsMinutes: [], telegramAvailable: true, phoneAvailable: true, telegramEnabled: true, callEnabled: true });
  resolvePhonePending.mockReset().mockResolvedValue({ phone: "+380 67 111 22 33", phonePending: null });
  getLocations.mockReset().mockResolvedValue([{ id: "p", name: "Поділ", timezone: "Europe/Kyiv" }]);
  markConversationRead.mockReset().mockResolvedValue(undefined);
});

async function card() {
  wrap(<ClientProfileScreen id="c1" />);
  return await screen.findByTestId("phone-status").then(() => screen.getByRole("heading", { name: "Телефон" }).closest("div")!);
}

describe("§25.8: картка клієнта - телефон", () => {
  it("номер, бейдж джерела, згода на дзвінок, можливий дубль", async () => {
    await card();
    const status = screen.getByTestId("phone-status");
    expect(within(status).getByRole("link", { name: "+380 50 000 00 03" })).toHaveAttribute("href", "tel:+380500000003");
    expect(within(status).getByText("з Telegram-контакту")).toBeInTheDocument();
    expect(status).toHaveTextContent(/Згода на дзвінок: 23 жовт\.?, 10:00/);
    const dup = screen.getByTestId("possible-duplicate");
    expect(within(dup).getByRole("link", { name: "Софія Мартин →" })).toHaveAttribute("href", "/beauty/clients/c2");
  });

  it("джерела: введено в чаті / з онлайн-запису / внесено адміністратором", async () => {
    for (const [source, label] of [["manual_chat", "введено в чаті"], ["web_form", "з онлайн-запису"], ["staff", "внесено адміністратором"]] as const) {
      phoneInfo = { phone: "+380 50 000 00 03", phoneSource: source };
      const { unmount } = wrap(<ClientProfileScreen id="c1" />);
      expect(await screen.findByText(label)).toBeInTheDocument();
      unmount();
    }
  });

  it("немає телефону: «Телефону немає», без згоди й pending", async () => {
    phoneInfo = { phone: null };
    wrap(<ClientProfileScreen id="c1" />);
    const status = await screen.findByTestId("phone-status");
    expect(status).toHaveTextContent("Телефону немає");
    expect(screen.queryByTestId("phone-pending")).toBeNull();
    expect(screen.queryByTestId("possible-duplicate")).toBeNull();
  });

  it("спадковий номер без джерела й згоди: «Згоди на дзвінок немає», бейджа джерела немає", async () => {
    phoneInfo = { phone: "+380 50 000 00 03" };
    wrap(<ClientProfileScreen id="c1" />);
    const status = await screen.findByTestId("phone-status");
    expect(status).toHaveTextContent("Згоди на дзвінок немає");
    expect(within(status).queryByText(/з Telegram|введено|онлайн|адміністратором/)).toBeNull();
  });

  it("pending: номер, дата, поточний не змінюється до рішення", async () => {
    await card();
    const pending = screen.getByTestId("phone-pending");
    expect(pending).toHaveTextContent("Клієнт надав інший номер у чаті: +380 67 111 22 33 (26 жовт");
    expect(pending).toHaveTextContent("Поточний номер не змінено");
    expect(within(pending).getByRole("button", { name: "Замінити" })).toBeInTheDocument();
    expect(within(pending).getByRole("button", { name: "Залишити поточний" })).toBeInTheDocument();
    expect(resolvePhonePending).not.toHaveBeenCalled();
  });

  it("«Замінити»: спершу підтвердження, потім accept; повідомлення про результат", async () => {
    const user = userEvent.setup();
    await card();
    const pending = screen.getByTestId("phone-pending");
    await user.click(within(pending).getByRole("button", { name: "Замінити" }));
    const dlg = within(pending).getByRole("alertdialog", { name: "Замінити номер?" });
    expect(dlg).toHaveTextContent("+380 50 000 00 03 буде замінено на +380 67 111 22 33");
    expect(resolvePhonePending).not.toHaveBeenCalled();
    await user.click(within(dlg).getByRole("button", { name: "Замінити" }));
    await waitFor(() => expect(resolvePhonePending).toHaveBeenCalledWith("c1", "accept"));
    expect(await screen.findByText("Номер замінено на новий.")).toBeInTheDocument();
  });

  it("«Залишити поточний»: підтвердження, reject; «Скасувати» нічого не надсилає", async () => {
    const user = userEvent.setup();
    await card();
    const pending = screen.getByTestId("phone-pending");
    await user.click(within(pending).getByRole("button", { name: "Залишити поточний" }));
    let dlg = within(pending).getByRole("alertdialog", { name: "Залишити поточний номер?" });
    await user.click(within(dlg).getByRole("button", { name: "Скасувати" }));
    expect(resolvePhonePending).not.toHaveBeenCalled();
    await user.click(within(pending).getByRole("button", { name: "Залишити поточний" }));
    dlg = within(pending).getByRole("alertdialog", { name: "Залишити поточний номер?" });
    await user.click(within(dlg).getByRole("button", { name: "Залишити поточний" }));
    await waitFor(() => expect(resolvePhonePending).toHaveBeenCalledWith("c1", "reject"));
    expect(await screen.findByText("Залишено поточний номер, новий видалено.")).toBeInTheDocument();
  });

  it("409 no_pending_phone: зрозуміле повідомлення", async () => {
    resolvePhonePending.mockRejectedValueOnce(new BeautyApiError(409, "no_pending_phone", "x"));
    const user = userEvent.setup();
    await card();
    const pending = screen.getByTestId("phone-pending");
    await user.click(within(pending).getByRole("button", { name: "Замінити" }));
    await user.click(within(within(pending).getByRole("alertdialog")).getByRole("button", { name: "Замінити" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("Нового номера вже немає");
  });

  it("specialist: картка телефону не показується (повний номер лише owner/admin)", async () => {
    role.current = "specialist";
    wrap(<ClientProfileScreen id="c1" />);
    await screen.findByRole("heading", { name: "Катерина Ляшко" });
    expect(screen.queryByRole("heading", { name: "Телефон" })).toBeNull();
    expect(screen.queryByTestId("phone-pending")).toBeNull();
  });

  it("без phoneInfo (backend ще не віддає) картка не показується", async () => {
    phoneInfo = undefined;
    wrap(<ClientProfileScreen id="c1" />);
    await screen.findByRole("heading", { name: "Катерина Ляшко" });
    expect(screen.queryByRole("heading", { name: "Телефон" })).toBeNull();
  });
});

const summaryOf = (client: NonNullable<ConversationDetail["conversation"]["client"]>): ConversationDetail["conversation"] => ({
  id: "cv3", channel: { id: "tg", type: "telegram", name: "Telegram" }, client, title: "Катерина Ляшко", aiState: "active", lastMessageAt: "2026-10-26T09:00:00Z",
  lastMessagePreview: "[контакт]", lastMessageSenderType: "client", unreadCount: 0, canReply: true,
});
const msg = (id: string, over: Partial<ConversationDetail["messages"][number]>): ConversationDetail["messages"][number] => ({
  id, direction: "inbound", senderType: "client", kind: "text", body: "", status: "processed", createdAt: "2026-10-26T09:00:00Z", ...over,
});

describe("§25.8: inbox - телефон замаскований, події контакту", () => {
  async function thread(messages: ConversationDetail["messages"], client = { id: "c1", name: "Катерина Ляшко", phoneMasked: "+••••••••••03", phonePending: true }) {
    getConversation.mockReset().mockResolvedValue({ conversation: summaryOf(client), messages, upcomingAppointments: [] });
    wrap(<ConversationThread id="cv3" onBack={() => {}} />);
    return await screen.findByTestId("phone-masked");
  }

  it("маска в заголовку (для зчитувача - «закінчується на 03»), бейдж «Надано інший номер» з посиланням на картку", async () => {
    const masked = await thread([msg("m1", { body: "привіт" })]);
    expect(masked).toHaveTextContent("Телефон: +••••••••••03");
    expect(masked).toHaveAccessibleName("Телефон приховано, закінчується на 03");
    const badge = screen.getByTestId("phone-pending-badge");
    expect(badge).toHaveTextContent("Надано інший номер");
    expect(within(badge).getByRole("link", { name: "Вирішити в картці клієнта" })).toHaveAttribute("href", "/beauty/clients/c1");
  });

  it("повний номер не показується, навіть якщо сервер помилково віддав його замість маски (перемаскування)", async () => {
    const masked = await thread([msg("m1", { body: "привіт" })], { id: "c1", name: "К", phoneMasked: "+380500000003", phonePending: false });
    expect(masked).toHaveTextContent("Телефон: +••••••••••03");
    expect(document.body.textContent).not.toContain("380500000003");
    expect(screen.queryByTestId("phone-pending-badge")).toBeNull();
  });

  it("чипи: свій контакт, чужий, некоректний, «Клієнт написав номер»; тіло контакту не показується", async () => {
    await thread([
      msg("m1", { kind: "contact", body: "[контакт]", contactVerdict: "own" }),
      msg("m2", { kind: "contact", body: "[контакт]", contactVerdict: "foreign" }),
      msg("m3", { kind: "contact", body: "[контакт]", contactVerdict: "invalid" }),
      msg("m4", { kind: "text", body: "[номер телефону]" }),
      msg("m5", { kind: "contact", body: "+380501112233", contactVerdict: "own" }),
    ]);
    const log = screen.getByRole("log");
    const items = within(log).getAllByTestId("message");
    expect(items[0]).toHaveTextContent("Клієнт поділився своїм контактом");
    expect(items[1]).toHaveTextContent("Надіслано чужий контакт — відхилено");
    expect(items[2]).toHaveTextContent("Некоректний номер");
    expect(items[3]).toHaveTextContent("Клієнт написав номер");
    expect(items[3]).not.toHaveTextContent("[номер телефону]");
    expect(log.textContent).not.toMatch(/\[контакт\]|380501112233/);
    expect(items[0]).toHaveAttribute("data-kind", "contact");
  });

  it("список розмов: превʼю події без номера й без префікса відправника", async () => {
    getConversations.mockReset().mockResolvedValue({
      items: [{ ...summaryOf({ id: "c1", name: "К", phoneMasked: "+••••••••••03" }), canReply: undefined } as never],
      meta: { needsReplyCount: 0 },
    });
    wrap(<InboxScreen />);
    const row = await screen.findByRole("button", { name: /Катерина Ляшко/ });
    expect(row).toHaveTextContent("Клієнт поділився контактом");
    expect(row).not.toHaveTextContent("[контакт]");
    expect(row.textContent).not.toMatch(/Клієнт: Клієнт поділився/);
  });
});

describe("§25.8: «Дзвінки» - новий номер", () => {
  const task = (pending?: string): CallTaskList["items"][number] => ({
    id: "t1", dueAt: "2026-10-26T08:00:00Z", localDueTime: "11:00", status: "open", reason: "reminder", offsetMinutes: 1440,
    client: { id: "c1", name: "Катерина Ляшко", phone: "+380 50 000 00 03", ...(pending ? { phonePending: pending } : {}) },
    appointment: { id: "a1", startsAt: "2026-10-27T08:00:00Z", localStartsAt: "2026-10-27T11:00", serviceName: "Манікюр", specialistName: "Марина", status: "confirmed" },
    location: { id: "p", name: "Поділ", timezone: "Europe/Kyiv" },
  });

  it("рядок «Новий номер (не підтверджено)» з tel: і посиланням на картку", async () => {
    getCallTasks.mockReset().mockResolvedValue({ items: [task("+380 67 111 22 33")], counts: { open: 1, done: 0, skipped: 0 } });
    wrap(<CallsScreen />);
    const row = await screen.findByTestId("phone-pending-row");
    expect(row).toHaveTextContent("Новий номер (не підтверджено):");
    expect(within(row).getByRole("link", { name: "+380 67 111 22 33" })).toHaveAttribute("href", "tel:+380671112233");
    expect(within(row).getByRole("link", { name: "Вирішити в картці клієнта" })).toHaveAttribute("href", "/beauty/clients/c1");
    // основний номер лишається
    expect(screen.getByRole("link", { name: "+380 50 000 00 03" })).toHaveAttribute("href", "tel:+380500000003");
  });

  it("без pending рядка немає", async () => {
    getCallTasks.mockReset().mockResolvedValue({ items: [task()], counts: { open: 1, done: 0, skipped: 0 } });
    wrap(<CallsScreen />);
    await screen.findByTestId("call-task");
    expect(screen.queryByTestId("phone-pending-row")).toBeNull();
  });
});
