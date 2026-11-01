import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { BeautyApiError } from "@/features/beauty-auth/errors";
import { canOpen, canUseMarketing, navFor } from "@/features/beauty-auth/permissions";
import { CATALOG, CONVERSATIONS_SEED, DEMO_LOCATIONS } from "../../beauty-shared/demo-seed";
import { CAMPAIGNS_SEED, CLIENT_COUPONS_SEED, CONSENT_SEED, PROMOTIONS_SEED, SHARED_COUPONS_SEED } from "../../beauty-shared/marketing-seed";
import { COUPON_BODY, marketingChip, marketingChipForLabel, previewText } from "../inbox-logic";

const h = vi.hoisted(() => ({
  api: {} as Record<string, ReturnType<typeof vi.fn>>,
  auth: { user: { id: "u", email: "e", fullName: "n", role: "owner" } },
}));
vi.mock("@/features/beauty-auth/components/auth-provider", () => ({ useAuth: () => h.auth }));
vi.mock("../api", () => ({ beautyApi: new Proxy({}, { get: (_t, k: string) => (...a: unknown[]) => (h.api[k] as unknown as (...x: unknown[]) => unknown)(...a) }) }));

import { PromosScreen } from "./promos-screen";

const promo = (over = {}) => ({ id: "p1", name: "Акція", discountType: "percent", discountValue: 20, startsAt: null, endsAt: null, isActive: true, access: "public", activeCampaigns: 0, locationIds: [], serviceIds: [], ...over });

function wrap(ui: React.ReactNode) {
  return render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })}>{ui}</QueryClientProvider>);
}

async function fillPromo() {
  await userEvent.type(await screen.findByLabelText("Назва"), "Тест");
  await userEvent.click(await screen.findByRole("button", { name: CATALOG[0].name }));
}

beforeEach(() => {
  h.auth.user.role = "owner";
  h.api = {
    getLocations: vi.fn(async () => structuredClone(DEMO_LOCATIONS)),
    getPriceList: vi.fn(async () => CATALOG.map((s) => ({ serviceId: s.id, name: s.name, durationMinutes: s.durationMinutes, price: s.price }))),
    previewPromotion: vi.fn(async () => ({ rows: [], locationSummary: "Усі заклади", samplePrice: 1, sampleOriginal: 2 })),
    createPromotion: vi.fn(async () => ({ id: "newp" })),
    createCoupon: vi.fn(async () => ({})),
    getCampaigns: vi.fn(async () => ({ items: [] })),
    getPromotions: vi.fn(async () => [promo()]),
    getCoupons: vi.fn(async () => []),
  };
});

describe("«Ціни та акції»: доступ і загальний код", () => {
  it("«Для всіх» за замовчуванням; після створення - кнопка «Розіслати клієнтам» з акцією", async () => {
    wrap(<PromosScreen />);
    expect(await screen.findByLabelText(/Для всіх/)).toBeChecked();
    await fillPromo();
    await userEvent.click(screen.getByRole("button", { name: "Запустити акцію" }));
    const done = await screen.findByTestId("promo-created");
    expect(within(done).getByRole("link", { name: /Розіслати клієнтам/ })).toHaveAttribute("href", "/beauty/campaigns/new?promotionId=newp");
    expect(h.api.createPromotion.mock.calls[0][0]).toMatchObject({ access: "public" });
    expect(h.api.createCoupon).not.toHaveBeenCalled();
  });

  it("«Лише за кодом»: поля коду, валідація, після створення акції створюється код", async () => {
    wrap(<PromosScreen />);
    await userEvent.click(await screen.findByLabelText(/Лише за кодом/));
    const code = screen.getByLabelText(/Код \(4–20/);
    await userEvent.type(code, "ab");
    expect(await screen.findByText(/від 4 до 20 символів/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Запустити акцію" })).toBeDisabled();
    await userEvent.clear(code);
    await userEvent.type(code, "autumn15");
    expect(code).toHaveValue("AUTUMN15");
    await userEvent.type(screen.getByLabelText(/Ліміт використань/), "50");
    await fillPromo();
    await userEvent.click(screen.getByRole("button", { name: "Запустити акцію" }));
    await screen.findByTestId("promo-created");
    expect(h.api.createPromotion.mock.calls[0][0]).toMatchObject({ access: "code_only" });
    expect(h.api.createCoupon.mock.calls[0][0]).toMatchObject({ promotionId: "newp", code: "AUTUMN15", maxUses: 50 });
  });

  it("помилка коду після створення акції: пояснення і повтор без другої акції", async () => {
    h.api.createCoupon.mockRejectedValueOnce(new BeautyApiError(409, "coupon_code_taken", "SERVER"));
    wrap(<PromosScreen />);
    await userEvent.click(await screen.findByLabelText(/Лише за кодом/));
    await userEvent.type(screen.getByLabelText(/Код \(4–20/), "TAKEN1");
    await fillPromo();
    await userEvent.click(screen.getByRole("button", { name: "Запустити акцію" }));
    const alert = await screen.findByText(/Акцію створено, але код не збережено/);
    expect(alert).toHaveTextContent("Такий код уже існує");
    expect(alert).not.toHaveTextContent("SERVER");
    await userEvent.click(screen.getByRole("button", { name: "Створити код ще раз" }));
    await waitFor(() => expect(h.api.createCoupon).toHaveBeenCalledTimes(2));
    expect(h.api.createPromotion).toHaveBeenCalledTimes(1);
  });

  it("marketing_disabled: «Лише за кодом» і кнопки розсилки сховані з поясненням", async () => {
    h.api.getCampaigns.mockRejectedValue(new BeautyApiError(403, "marketing_disabled", "x"));
    h.api.getPromotions.mockRejectedValue(new BeautyApiError(403, "marketing_disabled", "x"));
    wrap(<PromosScreen />);
    expect(await screen.findByTestId("code-only-unavailable")).toBeInTheDocument();
    expect(screen.queryByLabelText(/Лише за кодом/)).toBeNull();
    await fillPromo();
    await userEvent.click(screen.getByRole("button", { name: "Запустити акцію" }));
    await screen.findByTestId("promo-created");
    expect(screen.queryByRole("link", { name: /Розіслати клієнтам/ })).toBeNull();
  });
});

describe("Inbox: події згоди й купона", () => {
  it("чипи за підписами кнопок без кодів і ПД", () => {
    expect(marketingChipForLabel("🎟 Записатися зі знижкою")).toBe("Клієнт натиснув «Записатися зі знижкою»");
    expect(marketingChipForLabel("🔕 Не надсилати акції")).toBe("Клієнт відписався від акцій");
    expect(marketingChipForLabel("🔕 Не надсилати")).toBe("Клієнт відписався від акцій");
    expect(marketingChipForLabel("📰 Повна інформація")).toBe("Клієнт обрав розсилки: повна інформація");
    expect(marketingChipForLabel("🏷 Лише акції на мої послуги")).toBe("Клієнт обрав розсилки: лише акції на його послуги");
    expect(marketingChipForLabel("🔕 Більше не нагадувати")).toBeNull();
    expect(marketingChip({ kind: "text", body: COUPON_BODY })).toBe("Клієнт ввів код");
    expect(marketingChip({ kind: "callback", body: "[кнопка] x", pressedLabel: "✅ Підтверджую візит" })).toBeNull();
    expect(marketingChip({ kind: "text", body: "Привіт" })).toBeNull();
  });

  it("превʼю в списку: код купона і кнопки; текст з кодом маскується", () => {
    expect(previewText("[код купона]")).toBe("Клієнт ввів код");
    expect(previewText("[кнопка] 🔕 Не надсилати акції")).toBe("Клієнт відписався від акцій");
    expect(previewText("[кнопка] Так, скасувати")).toBe("Клієнт натиснув: Так, скасувати");
    expect(previewText("Ваш код: Q7W2K-9XM3D")).toBe("Ваш код: •••••-••M3D");
  });
});

describe("меню й ролі", () => {
  it("«Розсилки» лише для owner/admin; specialist не бачить і не відкриває", () => {
    expect(navFor("owner").some((n) => n.href === "/beauty/campaigns")).toBe(true);
    expect(navFor("admin").some((n) => n.href === "/beauty/campaigns")).toBe(true);
    expect(navFor("specialist").some((n) => n.href === "/beauty/campaigns")).toBe(false);
    expect(canOpen("specialist", "/beauty/campaigns")).toBe(false);
    expect(canOpen("specialist", "/beauty/campaigns/new")).toBe(false);
    expect(canUseMarketing("owner") && canUseMarketing("admin") && !canUseMarketing("specialist")).toBe(true);
  });
});

describe("demo-seed §26: узгодженість", () => {
  it("посилання сідів ведуть на наявні акції, клієнтів, коди й розмови", () => {
    const promos = new Set(PROMOTIONS_SEED.map((p) => p.id));
    const clients = new Set(["oc", "sm", "kl", "tg", "mp"]);
    expect(CAMPAIGNS_SEED.every((c) => !c.promotionId || promos.has(c.promotionId))).toBe(true);
    expect(SHARED_COUPONS_SEED.every((c) => PROMOTIONS_SEED.find((p) => p.id === c.promotionId)?.access === "code_only")).toBe(true);
    expect(CAMPAIGNS_SEED.filter((c) => c.sharedCouponId).every((c) => SHARED_COUPONS_SEED.some((s) => s.id === c.sharedCouponId))).toBe(true);
    expect(CLIENT_COUPONS_SEED.every((c) => clients.has(c.clientId) && c.codeHint.length === 3)).toBe(true);
    expect(CONSENT_SEED.map((c) => c.clientId).sort()).toEqual([...clients].sort());
    expect(new Set(CAMPAIGNS_SEED.map((c) => c.status))).toEqual(new Set(["draft", "scheduled", "sending", "done", "cancelled"]));
    expect(CAMPAIGNS_SEED.filter((c) => c.couponMode !== "none").every((c) => PROMOTIONS_SEED.find((p) => p.id === c.promotionId)?.access === "code_only")).toBe(true);
    const mp = CONVERSATIONS_SEED.find((c) => c.id === "cv6")!;
    expect(mp.messages.some((m) => m.couponText)).toBe(true);
    expect(mp.messages.some((m) => m.pressed === "🔕 Не надсилати акції")).toBe(true);
  });
});
