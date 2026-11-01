import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { BeautyApiError } from "@/features/beauty-auth/errors";
import { addDaysIso, todayIso } from "../format";
import { parseDraftDate } from "../api/http-client";
import { humanizePromoError, promoPeriodError, ymdToDmy } from "../marketing/logic";

const GUID_L1 = "11111111-1111-4111-8111-111111111111";
const GUID_L2 = "22222222-2222-4222-8222-222222222222";
const GUID_OFF = "33333333-3333-4333-8333-333333333333";
const GUID_S1 = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
const GUID_S2 = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";

const h = vi.hoisted(() => ({
  api: {} as Record<string, ReturnType<typeof vi.fn>>,
  auth: { user: { id: "u", email: "e", fullName: "n", role: "owner" } },
}));
vi.mock("@/features/beauty-auth/components/auth-provider", () => ({ useAuth: () => h.auth }));
vi.mock("../api", () => ({ beautyApi: new Proxy({}, { get: (_t, k: string) => (...a: unknown[]) => (h.api[k] as unknown as (...x: unknown[]) => unknown)(...a) }) }));

import { PromosScreen } from "./promos-screen";

function wrap(ui: React.ReactNode) {
  return render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })}>{ui}</QueryClientProvider>);
}

beforeEach(() => {
  h.api = {
    getLocations: vi.fn(async () => [
      { id: GUID_L1, name: "Центр" },
      { id: GUID_L2, name: "Подол", isActive: true },
      { id: GUID_OFF, name: "Закритий", isActive: false },
    ]),
    getPriceList: vi.fn(async () => [
      { serviceId: GUID_S1, name: "Манікюр", durationMinutes: 60, price: 800 },
      { serviceId: GUID_S2, name: "Педикюр", durationMinutes: 60, price: 900 },
    ]),
    previewPromotion: vi.fn(async () => ({ rows: [], locationSummary: "Усі заклади", samplePrice: 1, sampleOriginal: 2 })),
    createPromotion: vi.fn(async () => ({ id: "newp" })),
    createCoupon: vi.fn(async () => ({})),
    getCampaigns: vi.fn(async () => ({ items: [] })),
    getPromotions: vi.fn(async () => []),
    getCoupons: vi.fn(async () => []),
  };
});

const launchBtn = () => screen.getByRole("button", { name: "Запустити акцію" });

describe("«Нова акція»: початковий стан і запит", () => {
  it("без демо-значень: назва порожня, послуги не вибрані, дати - сьогодні й +7 днів, кнопка неактивна", async () => {
    wrap(<PromosScreen />);
    await screen.findByRole("button", { name: "Манікюр" });
    const t = todayIso();
    expect(screen.getByLabelText("Назва")).toHaveValue("");
    expect(screen.getByLabelText("Діє з")).toHaveValue(t);
    expect(screen.getByLabelText("Діє з")).toHaveAttribute("type", "date");
    expect(screen.getByLabelText("Діє по")).toHaveValue(addDaysIso(t, 7));
    expect(screen.getByRole("button", { name: "Манікюр" })).toHaveAttribute("aria-pressed", "false");
    await waitFor(() => expect(screen.getByRole("button", { name: "Центр" })).toHaveAttribute("aria-pressed", "true"));
    await waitFor(() => expect(screen.getByRole("button", { name: "Подол" })).toHaveAttribute("aria-pressed", "true"));
    expect(launchBtn()).toBeDisabled();
    expect(screen.getByText("Вкажіть назву акції.")).toBeInTheDocument();
  });

  it("POST: id беруться з API (активні заклади), дати - валідні ISO; демо-id не потрапляють у запит", async () => {
    wrap(<PromosScreen />);
    await userEvent.type(await screen.findByLabelText("Назва"), "Тест");
    await userEvent.click(await screen.findByRole("button", { name: "Манікюр" }));
    await userEvent.click(launchBtn());
    await screen.findByTestId("promo-created");
    const body = h.api.createPromotion.mock.calls[0][0];
    expect(body.locationIds).toEqual([GUID_L1, GUID_L2]);
    expect(body.serviceIds).toEqual([GUID_S1]);
    expect(JSON.stringify(body)).not.toMatch(/"(c|p|mn|pd)"/);
    expect(body.from).toBe(todayIso());
    expect(parseDraftDate(body.from, false)).toBe(new Date(`${todayIso()}T00:00:00`).toISOString());
    expect(parseDraftDate(body.to, true)).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/);
  });

  it("«по < з» і минуле блокують запуск з поясненням", async () => {
    wrap(<PromosScreen />);
    await userEvent.type(await screen.findByLabelText("Назва"), "Тест");
    await userEvent.click(await screen.findByRole("button", { name: "Манікюр" }));
    expect(launchBtn()).toBeEnabled();
    fireEvent.change(screen.getByLabelText("Діє по"), { target: { value: addDaysIso(todayIso(), -1) } });
    expect(await screen.findByTestId("promo-period-error")).toHaveTextContent("не раніше початку");
    expect(launchBtn()).toBeDisabled();
    fireEvent.change(screen.getByLabelText("Діє з"), { target: { value: addDaysIso(todayIso(), -3) } });
    expect(screen.getByTestId("promo-period-error")).toHaveTextContent("в минулому");
    expect(launchBtn()).toBeDisabled();
  });

  it.each([
    ["invalid_name", /Назва акції/],
    ["invalid_discount", /Знижка/],
    ["invalid_access", /доступ/],
    ["invalid_period", /період/],
    ["unknown_reference", /Заклад або послугу не знайдено/],
    ["promotion_in_campaign", /розсилці/],
  ])("422 %s - українське повідомлення без серверного тексту", async (code, re) => {
    h.api.createPromotion.mockRejectedValueOnce(new BeautyApiError(422, code, "SERVER TEXT"));
    wrap(<PromosScreen />);
    await userEvent.type(await screen.findByLabelText("Назва"), "Тест");
    await userEvent.click(await screen.findByRole("button", { name: "Манікюр" }));
    await userEvent.click(launchBtn());
    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent(re);
    expect(alert).not.toHaveTextContent("SERVER");
  });

  it("«Лише за кодом»: код створюється з періодом акції (yyyy-MM-dd -> ISO)", async () => {
    wrap(<PromosScreen />);
    await userEvent.type(await screen.findByLabelText("Назва"), "Тест");
    await userEvent.click(await screen.findByRole("button", { name: "Манікюр" }));
    await userEvent.click(screen.getByLabelText(/Лише за кодом/));
    await userEvent.type(screen.getByLabelText(/Код \(4–20/), "autumn15");
    await waitFor(() => expect(launchBtn()).toBeEnabled());
    await userEvent.click(launchBtn());
    await screen.findByTestId("promo-created");
    expect(h.api.createCoupon.mock.calls[0][0]).toMatchObject({
      promotionId: "newp",
      code: "AUTUMN15",
      validFrom: new Date(`${todayIso()}T00:00:00`).toISOString(),
    });
  });
});

describe("logic", () => {
  it("parseDraftDate: лише yyyy-MM-dd, без Z-обману", () => {
    expect(parseDraftDate("2026-10-07", false)).toBe(new Date("2026-10-07T00:00:00").toISOString());
    expect(parseDraftDate("07.10.2026", false)).toBeNull();
    expect(parseDraftDate("", true)).toBeNull();
  });
  it("ymdToDmy, promoPeriodError, humanizePromoError", () => {
    expect(ymdToDmy("2026-10-07")).toBe("07.10.2026");
    expect(promoPeriodError("2026-11-02", "2026-11-01", "2026-11-01")).toMatch(/не раніше початку/);
    expect(promoPeriodError("2026-11-01", "2026-11-08", "2026-11-01")).toBeNull();
    expect(humanizePromoError(new BeautyApiError(422, "unknown_reference", "x"))).toMatch(/не знайдено/);
  });
});
