import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
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

vi.mock("next/navigation", () => ({ useSearchParams: () => new URLSearchParams(window.location.search) }));

import { PromosScreen } from "./promos-screen";

function wrap(ui: React.ReactNode) {
  return render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })}>{ui}</QueryClientProvider>);
}

beforeEach(() => {
  window.history.replaceState(null, "", "/beauty/promos");
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

async function openForm() {
  await userEvent.click(await screen.findByRole("button", { name: "Додати акцію" }));
}
const launchBtn = () => screen.getByRole("button", { name: "Запустити акцію" });

describe("«Нова акція»: початковий стан і запит", () => {
  it("без демо-значень: назва порожня, послуги не вибрані, дати - сьогодні й +7 днів, кнопка неактивна", async () => {
    wrap(<PromosScreen />);
    await openForm();
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
    await openForm();
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
    await openForm();
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
    await openForm();
    await userEvent.type(await screen.findByLabelText("Назва"), "Тест");
    await userEvent.click(await screen.findByRole("button", { name: "Манікюр" }));
    await userEvent.click(launchBtn());
    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent(re);
    expect(alert).not.toHaveTextContent("SERVER");
  });

  it("«Лише за кодом»: код створюється з періодом акції (yyyy-MM-dd -> ISO)", async () => {
    wrap(<PromosScreen />);
    await openForm();
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

const DAY = 86_400_000;
const isoIn = (days: number) => new Date(Date.now() + days * DAY).toISOString();
const PROMOS = [
  { id: "p-act", name: "Осінній манікюр", discountType: "percent", discountValue: 20, startsAt: isoIn(-5), endsAt: isoIn(10), isActive: true, access: "public", activeCampaigns: 1, locationIds: [GUID_L1], serviceIds: [GUID_S1] },
  { id: "p-sched", name: "Зимова акція", discountType: "fixed", discountValue: 100, startsAt: isoIn(5), endsAt: isoIn(20), isActive: true, access: "public", activeCampaigns: 0, locationIds: [], serviceIds: [] },
  { id: "p-end", name: "Літня знижка", discountType: "percent", discountValue: 10, startsAt: isoIn(-60), endsAt: isoIn(-30), isActive: false, access: "public", activeCampaigns: 0, locationIds: [GUID_L2], serviceIds: [GUID_S1, GUID_S2] },
  { id: "p-code", name: "Для своїх", discountType: "percent", discountValue: 15, startsAt: isoIn(-1), endsAt: isoIn(30), isActive: true, access: "code_only", activeCampaigns: 0, locationIds: [], serviceIds: [] },
];
const COUPONS = [
  { id: "c1", kind: "shared", promotionId: "p-code", code: "AUTUMN15", validFrom: isoIn(-1), validTo: isoIn(20), maxUses: 100, usesCount: 12, status: "active" },
  { id: "c2", kind: "shared", promotionId: "p-code", code: "OLD2025", validFrom: isoIn(-60), validTo: isoIn(-5), usesCount: 4, status: "revoked" },
];

describe("підрозділи екрана", () => {
  beforeEach(() => {
    h.api.getPromotions = vi.fn(async () => structuredClone(PROMOS));
    h.api.getCoupons = vi.fn(async () => structuredClone(COUPONS));
    h.api.revokeCoupon = vi.fn(async () => ({}));
  });

  it("за замовчуванням «Акції»; форма прихована, відкривається кнопкою з фокусом на «Назва», «Скасувати» закриває", async () => {
    wrap(<PromosScreen />);
    expect(await screen.findByRole("tab", { name: "Акції" })).toHaveAttribute("aria-selected", "true");
    expect(screen.getByRole("tabpanel")).toBeInTheDocument();
    expect(screen.queryByLabelText("Назва")).toBeNull();
    await openForm();
    expect(screen.getByLabelText("Назва")).toHaveFocus();
    await userEvent.click(screen.getByRole("button", { name: "Скасувати" }));
    expect(screen.queryByLabelText("Назва")).toBeNull();
    await waitFor(() => expect(screen.getByRole("button", { name: "Додати акцію" })).toHaveFocus());
  });

  it("після успішного «Запустити акцію» форма закривається, список перезавантажується, є повідомлення", async () => {
    wrap(<PromosScreen />);
    await openForm();
    await userEvent.type(screen.getByLabelText("Назва"), "Тест");
    await userEvent.click(await screen.findByRole("button", { name: "Манікюр" }));
    await userEvent.click(launchBtn());
    await screen.findByTestId("promo-created");
    expect(screen.queryByLabelText("Назва")).toBeNull();
    await waitFor(() => expect(h.api.getPromotions.mock.calls.length).toBeGreaterThanOrEqual(2));
  });

  it("список акцій: знижка, період, заклади, послуги, доступ і статус словами", async () => {
    wrap(<PromosScreen />);
    const rows = await screen.findAllByTestId("promotion-item");
    expect(rows).toHaveLength(4);
    expect(rows[0]).toHaveTextContent("Осінній манікюр");
    expect(rows[0]).toHaveTextContent("−20%");
    expect(rows[0]).toHaveTextContent("Центр");
    expect(rows[0]).toHaveTextContent("Манікюр");
    expect(rows[0]).toHaveTextContent("Активна");
    expect(rows[0]).toHaveTextContent("В активній розсилці");
    expect(rows[1]).toHaveTextContent("−100 грн");
    expect(rows[1]).toHaveTextContent("Запланована");
    expect(rows[1]).toHaveTextContent("Усі заклади");
    expect(rows[2]).toHaveTextContent("Завершена");
    expect(rows[3]).toHaveTextContent("Лише за кодом");
  });

  it("порожній стан із поясненням", async () => {
    h.api.getPromotions = vi.fn(async () => []);
    wrap(<PromosScreen />);
    expect(await screen.findByText(/Акцій ще немає/)).toBeInTheDocument();
  });

  it("вкладки перемикаються стрілками, Home/End; вибір пишеться в URL", async () => {
    wrap(<PromosScreen />);
    const offers = await screen.findByRole("tab", { name: "Акції" });
    offers.focus();
    await userEvent.keyboard("{ArrowRight}");
    const codes = screen.getByRole("tab", { name: "Коди" });
    expect(codes).toHaveAttribute("aria-selected", "true");
    expect(codes).toHaveFocus();
    expect(window.location.search).toBe("?tab=codes");
    await userEvent.keyboard("{ArrowRight}");
    expect(screen.getByRole("tab", { name: "Прайс-лист" })).toHaveAttribute("aria-selected", "true");
    await userEvent.keyboard("{End}");
    expect(codes).toHaveAttribute("aria-selected", "true");
    await userEvent.keyboard("{Home}");
    expect(screen.getByRole("tab", { name: "Прайс-лист" })).toHaveFocus();
    await userEvent.keyboard("{ArrowLeft}");
    expect(codes).toHaveAttribute("aria-selected", "true");
    expect(screen.getByRole("tab", { name: "Акції" })).toHaveAttribute("tabindex", "-1");
  });

  it("початкова вкладка береться з ?tab=", async () => {
    window.history.replaceState(null, "", "/beauty/promos?tab=codes");
    wrap(<PromosScreen />);
    expect(await screen.findByRole("tab", { name: "Коди" })).toHaveAttribute("aria-selected", "true");
    expect(await screen.findByText("AUTUMN15")).toBeInTheDocument();
  });

  it("?tab=prices показує таблицю прайсу без форми", async () => {
    window.history.replaceState(null, "", "/beauty/promos?tab=prices");
    wrap(<PromosScreen />);
    expect(await screen.findByRole("heading", { name: "Прайс-лист" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Додати акцію" })).toBeNull();
  });

  it("marketing_disabled: вкладка «Коди» прихована, а ?tab=codes веде на «Акції»", async () => {
    window.history.replaceState(null, "", "/beauty/promos?tab=codes");
    h.api.getCampaigns.mockRejectedValue(new BeautyApiError(403, "marketing_disabled", "x"));
    wrap(<PromosScreen />);
    await waitFor(() => expect(screen.queryByRole("tab", { name: "Коди" })).toBeNull());
    expect(screen.getByRole("tab", { name: "Акції" })).toHaveAttribute("aria-selected", "true");
    expect(screen.getByRole("tab", { name: "Прайс-лист" })).toBeInTheDocument();
  });

  it("«Відкликати» код: підтвердження, потім запит", async () => {
    window.history.replaceState(null, "", "/beauty/promos?tab=codes");
    wrap(<PromosScreen />);
    const rows = await screen.findAllByTestId("coupon-row");
    expect(rows[0]).toHaveTextContent("12 / 100");
    expect(rows[0]).toHaveTextContent("Діє");
    expect(rows[1]).toHaveTextContent("Відкликано");
    await userEvent.click(screen.getByRole("button", { name: "Відкликати код AUTUMN15" }));
    expect(h.api.revokeCoupon).not.toHaveBeenCalled();
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Відкликати" }));
    await waitFor(() => expect(h.api.revokeCoupon).toHaveBeenCalledWith("c1"));
  });

  it("«Додати код»: перевірка полів, успіх і помилка 409 coupon_code_taken", async () => {
    window.history.replaceState(null, "", "/beauty/promos?tab=codes");
    h.api.createCoupon.mockRejectedValueOnce(new BeautyApiError(409, "coupon_code_taken", "SERVER"));
    wrap(<PromosScreen />);
    await userEvent.click(await screen.findByRole("button", { name: "Додати код" }));
    const dialog = screen.getByRole("dialog");
    const submit = within(dialog).getByRole("button", { name: "Створити код" });
    expect(submit).toBeDisabled();
    expect(within(dialog).getByLabelText("Акція")).toHaveValue("p-code");
    await userEvent.type(within(dialog).getByLabelText(/Код \(4–20/), "ab");
    expect(await within(dialog).findByText(/від 4 до 20 символів/)).toBeInTheDocument();
    await userEvent.type(within(dialog).getByLabelText(/Код \(4–20/), "cd12");
    await waitFor(() => expect(submit).toBeEnabled());
    await userEvent.click(submit);
    const alert = await within(dialog).findByRole("alert");
    expect(alert).toHaveTextContent("Такий код уже існує");
    expect(alert).not.toHaveTextContent("SERVER");
    await userEvent.click(submit);
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    expect(h.api.createCoupon).toHaveBeenCalledTimes(2);
    expect(h.api.createCoupon.mock.calls[1][0]).toMatchObject({ promotionId: "p-code", code: "ABCD12" });
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
