import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { BeautyApiError } from "@/features/beauty-auth/errors";
import type { CancellationSettings } from "../types";

const auth = { user: { id: "u", email: "e", fullName: "n", role: "owner" } };
vi.mock("@/features/beauty-auth/components/auth-provider", () => ({ useAuth: () => auth }));

const getSettings = vi.fn<() => Promise<CancellationSettings>>();
const updateSettings = vi.fn<(s: CancellationSettings) => Promise<CancellationSettings>>();
vi.mock("../api", () => ({
  beautyApi: {
    getCancellationSettings: () => getSettings(),
    updateCancellationSettings: (s: CancellationSettings) => updateSettings(s),
  },
}));

import { SettingsScreen } from "./settings-screen";

const base: CancellationSettings = {
  windowHours: 12,
  refundPercentInWindow: 50,
  refundPercentOutside: 100,
  deductFee: false,
  feePercent: 0,
  showTermsToClient: true,
  customTermsText: null,
};

function renderScreen() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <SettingsScreen />
    </QueryClientProvider>,
  );
}

const preview = () => screen.getByRole("region", { name: "Так побачить клієнт" });
const switchEl = () => screen.findByRole("switch", { name: /Показувати умови скасування клієнту/ });
const textarea = () => screen.getByLabelText(/Власний текст умов/);

beforeEach(() => {
  auth.user.role = "owner";
  getSettings.mockReset().mockResolvedValue(base);
  updateSettings.mockReset().mockImplementation(async (s) => s);
});

describe("налаштування: що бачить клієнт", () => {
  it("за замовчуванням показ увімкнено, текст порожній, у перегляді автоматичні рядки", async () => {
    renderScreen();
    expect(await switchEl()).toBeChecked();
    expect(textarea()).toBeEnabled();
    expect(textarea()).toHaveValue("");
    expect(within(preview()).getByText("Безкоштовне скасування більш ніж за 12 год до візиту.")).toBeInTheDocument();
    expect(within(preview()).getByText("Якщо скасувати за 12 год і менше — повернення 50%.")).toBeInTheDocument();
    expect(within(preview()).queryByText(/комісія/)).not.toBeInTheDocument();
  });

  it("старий API без нових полів: показ увімкнено, автотекст", async () => {
    getSettings.mockResolvedValue({ windowHours: 12, refundPercentInWindow: 50, refundPercentOutside: 100, deductFee: false, feePercent: 0 });
    renderScreen();
    expect(await switchEl()).toBeChecked();
    expect(textarea()).toHaveValue("");
  });

  it("автотекст оновлюється з чисел форми; повернення поза вікном < 100% і комісія", async () => {
    renderScreen();
    await switchEl();
    fireEvent.change(screen.getByLabelText(/Повернення, якщо скасовано раніше ніж/), { target: { value: "80" } });
    fireEvent.click(screen.getByLabelText(/Утримувати комісію/));
    fireEvent.change(screen.getByLabelText("Комісія"), { target: { value: "5" } });
    const p = within(preview());
    expect(p.getByText("Якщо скасувати раніше — повернення 80%.")).toBeInTheDocument();
    expect(p.getByText("Утримується комісія 5%.")).toBeInTheDocument();
    expect(p.queryByText(/Безкоштовне/)).not.toBeInTheDocument();
  });

  it("власний текст замінює автоматичний у перегляді", async () => {
    const user = userEvent.setup();
    renderScreen();
    await switchEl();
    await user.type(textarea(), "Скасуйте за добу");
    expect(within(preview()).getByText("Скасуйте за добу")).toBeInTheDocument();
    expect(within(preview()).queryByText(/Безкоштовне/)).not.toBeInTheDocument();
    expect(screen.getByText("16 / 500")).toBeInTheDocument();
  });

  it("вимкнений показ: textarea disabled, перегляд «Блок умов приховано»", async () => {
    const user = userEvent.setup();
    renderScreen();
    await user.click(await switchEl());
    expect(textarea()).toBeDisabled();
    expect(within(preview()).getByText("Блок умов приховано")).toBeInTheDocument();
  });

  it("ліміт 500: довший текст не зберігається, показується помилка", async () => {
    renderScreen();
    await switchEl();
    fireEvent.change(textarea(), { target: { value: "а".repeat(501) } });
    expect(screen.getByText("501 / 500")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Зберегти" }));
    expect(await screen.findByText("Не більше 500 символів")).toBeInTheDocument();
    expect(updateSettings).not.toHaveBeenCalled();
  });

  it("зберігає payload з новими полями; порожній текст -> null", async () => {
    const user = userEvent.setup();
    renderScreen();
    await user.click(await switchEl());
    await user.click(screen.getByRole("button", { name: "Зберегти" }));
    await waitFor(() => expect(updateSettings).toHaveBeenCalledTimes(1));
    expect(updateSettings).toHaveBeenCalledWith({ ...base, showTermsToClient: false, customTermsText: null });
    expect(await screen.findByText("Налаштування збережено.")).toBeInTheDocument();
  });

  it("зберігає власний текст (обрізаний)", async () => {
    const user = userEvent.setup();
    renderScreen();
    await switchEl();
    await user.type(textarea(), "  Умови  ");
    await user.click(screen.getByRole("button", { name: "Зберегти" }));
    await waitFor(() => expect(updateSettings).toHaveBeenCalledWith({ ...base, customTermsText: "Умови" }));
  });

  it("422 invalid_terms_text показується біля поля", async () => {
    const user = userEvent.setup();
    updateSettings.mockRejectedValue(new BeautyApiError(422, "invalid_terms_text", "x"));
    renderScreen();
    await switchEl();
    await user.type(textarea(), "Текст");
    await user.click(screen.getByRole("button", { name: "Зберегти" }));
    expect(await screen.findByText(/не довшим за 500 символів/)).toBeInTheDocument();
  });

  it("не власник: перемикач і textarea недоступні, кнопки збереження немає", async () => {
    auth.user.role = "admin";
    renderScreen();
    expect(await switchEl()).toBeDisabled();
    expect(textarea()).toBeDisabled();
    expect(screen.queryByRole("button", { name: "Зберегти" })).not.toBeInTheDocument();
  });
});
