import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
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

import { ContactPreferencesCard } from "./contact-preferences-card";
import { ReminderPoints } from "./reminder-points";
import { ReminderSettingsSection } from "./reminder-settings-section";

type Spies = Record<string, ReturnType<typeof vi.fn>>;
let spies: Spies;
let real: ReturnType<typeof createMockInbox>;

function setup(role = "owner") {
  h.auth.user.role = role;
  const appts = appointmentsSeed();
  real = createMockInbox({
    role: () => role,
    wait: false,
    locations: () => DEMO_LOCATIONS,
    appointments: () => appts,
    channels: () => structuredClone(INITIAL_CHANNELS),
    staffName: (id) => STAFF.find((m) => m.id === id)?.name ?? "",
  });
  const names = ["getContactPreferences", "setContactPreferences", "getReminderSettings", "saveReminderSettings"] as const;
  spies = Object.fromEntries(names.map((k) => [k, vi.fn(real[k] as (...a: unknown[]) => unknown)]));
  h.api = spies as unknown as typeof h.api;
}

function wrap(node: React.ReactNode) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(<QueryClientProvider client={qc}>{node}</QueryClientProvider>);
}

beforeEach(() => setup());

describe("картка «Нагадування й зв’язок» у профілі клієнта", () => {
  it("«Не обрано»: спосіб не вибрано, Зберегти вимкнено", async () => {
    wrap(<ContactPreferencesCard clientId="sm" />);
    expect(await screen.findByTestId("prefs-summary")).toHaveTextContent("Не обрано");
    expect(screen.getByRole("button", { name: "Зберегти" })).toBeDisabled();
    expect(screen.getByLabelText("Застосувати до майбутніх записів")).toBeChecked();
  });

  it("поточне значення: спосіб, точки, джерело; зміна адміністратором показує автора", async () => {
    wrap(<ContactPreferencesCard clientId="oc" />);
    const summary = await screen.findByTestId("prefs-summary");
    expect(summary).toHaveTextContent("Telegram: за 2 год і за 24 год");
    expect(summary).toHaveTextContent("вибір клієнта в чаті");
    expect(screen.getByLabelText("за 2 год до візиту")).toBeChecked();
    expect(screen.getByLabelText("за 1 год до візиту")).not.toBeChecked();
    await userEvent.click(screen.getByLabelText("за 1 год до візиту"));
    await userEvent.click(screen.getByRole("button", { name: "Зберегти" }));
    expect(await screen.findByText(/^Збережено\./)).toBeInTheDocument();
    expect(spies.setContactPreferences).toHaveBeenCalledWith("oc", { method: "telegram", offsetsMinutes: [60, 120, 1440], applyToUpcoming: true });
    await waitFor(() => expect(screen.getByTestId("prefs-summary")).toHaveTextContent("адміністратор, Світлана Коваленко"));
    expect(screen.getByTestId("prefs-summary")).toHaveTextContent("Telegram: за 1 год, за 2 год і за 24 год");
  });

  it("дзвінок - одна точка; «не застосовувати до майбутніх» передається в API", async () => {
    wrap(<ContactPreferencesCard clientId="sm" />);
    await screen.findByTestId("prefs-summary");
    await userEvent.click(screen.getByLabelText("Дзвінок адміністратора"));
    expect(screen.getAllByRole("radio", { name: /до візиту/ })).toHaveLength(3);
    await userEvent.click(screen.getByLabelText("за 1 год до візиту"));
    await userEvent.click(screen.getByLabelText("Застосувати до майбутніх записів"));
    await userEvent.click(screen.getByRole("button", { name: "Зберегти" }));
    await waitFor(() => expect(spies.setContactPreferences).toHaveBeenCalledWith("sm", { method: "call", offsetsMinutes: [60], applyToUpcoming: false }));
  });

  it("Telegram недоступний без Telegram-розмови; без телефону / за вимкненого налаштування - дзвінок вимкнено з підказкою", async () => {
    wrap(<ContactPreferencesCard clientId="mp" />);
    await screen.findByTestId("prefs-summary");
    expect(screen.getByLabelText("Telegram")).toBeDisabled();
    expect(screen.getByText(/Немає розмови з клієнтом у Telegram/)).toBeInTheDocument();
    expect(screen.getByLabelText("Дзвінок адміністратора")).toBeEnabled();
  });

  it("налаштування закладу вимикають способи: підказка «вимкнено в налаштуваннях»", async () => {
    const s = await real.getReminderSettings();
    await real.saveReminderSettings({ ...s, callEnabled: false, telegramEnabled: false });
    wrap(<ContactPreferencesCard clientId="oc" />);
    await screen.findByTestId("prefs-summary");
    expect(screen.getByLabelText("Telegram")).toBeDisabled();
    expect(screen.getByLabelText("Дзвінок адміністратора")).toBeDisabled();
    expect(screen.getByText("Нагадування в Telegram вимкнено в налаштуваннях.")).toBeInTheDocument();
    expect(screen.getByText("Дзвінки-нагадування вимкнено в налаштуваннях.")).toBeInTheDocument();
  });

  it.each([
    ["telegram_unavailable", /немає розмови в підключеному Telegram-каналі/],
    ["phone_required", /немає телефону/],
    ["invalid_offsets", /Некоректні точки нагадування/],
    ["client_not_found", /Клієнта не знайдено/],
  ])("помилка %s показується українською", async (errCode, expected) => {
    wrap(<ContactPreferencesCard clientId="oc" />);
    await screen.findByTestId("prefs-summary");
    spies.setContactPreferences.mockRejectedValueOnce(new BeautyApiError(409, errCode, "english +380"));
    await userEvent.click(screen.getByLabelText("за 1 год до візиту"));
    await userEvent.click(screen.getByRole("button", { name: "Зберегти" }));
    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent(expected);
    expect(alert).not.toHaveTextContent("english");
  });

  it("specialist: картка не показується й API не викликається", async () => {
    setup("specialist");
    const { container } = wrap(<ContactPreferencesCard clientId="oc" />);
    await act(async () => {
      await new Promise((r) => setTimeout(r, 30));
    });
    expect(container).toBeEmptyDOMElement();
    expect(spies.getContactPreferences).not.toHaveBeenCalled();
  });
});

describe("точки нагадувань запису (лише читання)", () => {
  it("показує спосіб, час у зоні закладу, статус і причину пропуску", () => {
    render(
      <ReminderPoints
        timezone="Europe/Kyiv"
        reminders={{
          method: "telegram",
          offsetsMinutes: [60, 120],
          points: [
            { kind: "message", offsetMinutes: 120, dueAt: "2026-10-16T05:00:00Z", status: "skipped", skipReason: "merged" },
            { kind: "message", offsetMinutes: 60, dueAt: "2026-10-16T05:30:00Z", status: "scheduled" },
          ],
        }}
      />,
    );
    const box = screen.getByTestId("reminder-points");
    expect(box).toHaveTextContent("Telegram: за 1 год і за 2 год");
    expect(box).toHaveTextContent("Повідомлення за 1 год: 16 жовт., 08:30 · заплановано");
    expect(box).toHaveTextContent("пропущено (об'єднано з сусідньою точкою)");
  });

  it("без точок: пояснення, що час минув; «не нагадувати» - без зайвого", () => {
    const { rerender } = render(<ReminderPoints reminders={{ method: "call", offsetsMinutes: [60], points: [] }} />);
    expect(screen.getByText(/час нагадування вже минув/)).toBeInTheDocument();
    rerender(<ReminderPoints reminders={{ method: "none", offsetsMinutes: [], points: [] }} />);
    expect(screen.getByTestId("reminder-points")).toHaveTextContent("Не нагадувати");
    expect(screen.queryByText(/вже минув/)).not.toBeInTheDocument();
  });
});

describe("налаштування нагадувань у «Налаштуваннях»", () => {
  it("owner редагує: нічні години й способи зберігаються; некоректні години - помилка без запиту", async () => {
    wrap(<ReminderSettingsSection />);
    const form = await screen.findByTestId("reminder-settings-form");
    const from = within(form).getByLabelText("З");
    expect(from).toHaveValue("22:00");
    expect(within(form).getByLabelText("До")).toHaveValue("08:00");
    await userEvent.click(within(form).getByLabelText("Дзвінки адміністратора (розділ «Дзвінки»)"));
    await userEvent.clear(from);
    await userEvent.type(from, "19:00");
    await userEvent.click(within(form).getByRole("button", { name: "Зберегти" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("не довшою за 12 годин");
    expect(spies.saveReminderSettings).not.toHaveBeenCalled();
    await userEvent.clear(from);
    await userEvent.type(from, "23:00");
    await userEvent.click(within(form).getByRole("button", { name: "Зберегти" }));
    expect(await screen.findByRole("status")).toHaveTextContent("Налаштування збережено.");
    expect(spies.saveReminderSettings).toHaveBeenCalledWith({ telegramEnabled: true, callEnabled: false, quietHoursStart: "23:00", quietHoursEnd: "08:00" });
  });

  it("admin лише читає: поля вимкнені, кнопки «Зберегти» немає", async () => {
    setup("admin");
    wrap(<ReminderSettingsSection />);
    const form = await screen.findByTestId("reminder-settings-form");
    expect(within(form).getByRole("note")).toHaveTextContent("Лише власник");
    expect(within(form).getByLabelText("З")).toBeDisabled();
    expect(within(form).queryByRole("button", { name: "Зберегти" })).not.toBeInTheDocument();
  });

  it("specialist секції не бачить", async () => {
    setup("specialist");
    const { container } = wrap(<ReminderSettingsSection />);
    await act(async () => {
      await new Promise((r) => setTimeout(r, 30));
    });
    expect(container).toBeEmptyDOMElement();
    expect(spies.getReminderSettings).not.toHaveBeenCalled();
  });

  it("422 invalid_quiet_hours від сервера - український текст", async () => {
    wrap(<ReminderSettingsSection />);
    const form = await screen.findByTestId("reminder-settings-form");
    spies.saveReminderSettings.mockRejectedValueOnce(new BeautyApiError(422, "invalid_quiet_hours", "bad"));
    await userEvent.click(within(form).getByLabelText("Нагадування в Telegram"));
    await userEvent.click(within(form).getByRole("button", { name: "Зберегти" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("Некоректні години тиші");
  });
});
