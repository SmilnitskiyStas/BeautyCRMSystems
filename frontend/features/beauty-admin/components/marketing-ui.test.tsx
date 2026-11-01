import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { BeautyApiError } from "@/features/beauty-auth/errors";
import { CATALOG, DEMO_LOCATIONS, appointmentsSeed } from "../../beauty-shared/demo-seed";
import { createMockMarketing, estimateAudience } from "../api/mock-marketing";

const h = vi.hoisted(() => ({
  api: {} as Record<string, (...a: unknown[]) => unknown>,
  auth: { user: { id: "u", email: "e", fullName: "n", role: "owner" } },
  push: vi.fn(),
}));
vi.mock("@/features/beauty-auth/components/auth-provider", () => ({ useAuth: () => h.auth }));
vi.mock("next/navigation", () => ({ useRouter: () => ({ push: h.push }), usePathname: () => "/beauty/campaigns" }));
vi.mock("../api", () => ({ beautyApi: new Proxy({}, { get: (_t, k: string) => (...a: unknown[]) => h.api[k](...a) }) }));

import { CampaignDetailScreen } from "./campaign-detail";
import { CampaignWizard } from "./campaign-wizard";
import { CampaignsScreen } from "./campaigns-screen";
import { CouponBadge } from "./coupon-badge";
import { ClientCouponsCard, MarketingConsentCard } from "./marketing-consent-card";
import { CodesCard, PromotionsCard } from "./promo-access";

let state: { enabled: boolean; grant: boolean };
function setup(role = "owner") {
  h.auth.user.role = role;
  state = { enabled: true, grant: true };
  const real = createMockMarketing({
    role: () => role,
    wait: false,
    marketingEnabled: () => state.enabled,
    adminGrantEnabled: () => state.grant,
    services: () => CATALOG.map((s) => ({ id: s.id, name: s.name })),
    locations: () => DEMO_LOCATIONS,
    appointments: () => appointmentsSeed(),
    audience: (q) => estimateAudience(q),
  });
  const { couponFor: _c, registerPromotion: _r, ...methods } = real;
  void _c;
  void _r;
  h.api = {
    ...(methods as unknown as Record<string, (...a: unknown[]) => unknown>),
    getLocations: async () => structuredClone(DEMO_LOCATIONS),
    getPriceList: async () => CATALOG.map((s) => ({ serviceId: s.id, name: s.name, durationMinutes: s.durationMinutes, price: s.price })),
  };
}

function wrap(ui: React.ReactNode) {
  return render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })}>{ui}</QueryClientProvider>);
}

beforeEach(() => {
  h.push.mockReset();
  setup();
});

describe("список розсилок", () => {
  it("рядки зі статусами, посилання на деталі, фільтр за статусом", async () => {
    wrap(<CampaignsScreen />);
    const rows = await screen.findAllByTestId("campaign-row");
    expect(rows).toHaveLength(5);
    const text = rows.map((r) => r.textContent).join("|");
    for (const s of ["Чернетка", "Заплановано", "Надсилається", "Завершено", "Скасовано"]) expect(text).toContain(s);
    expect(screen.getByRole("link", { name: "Повернення улюблених клієнтів" })).toHaveAttribute("href", "/beauty/campaigns/k4");
    expect(screen.getByRole("link", { name: "Нова розсилка" })).toHaveAttribute("href", "/beauty/campaigns/new");
    await userEvent.click(screen.getByRole("button", { name: "Завершено" }));
    await waitFor(() => expect(screen.getAllByTestId("campaign-row")).toHaveLength(1));
  });

  it("specialist: без запитів і без даних", () => {
    setup("specialist");
    const spy = vi.fn();
    h.api.getCampaigns = spy;
    wrap(<CampaignsScreen />);
    expect(screen.getByRole("alert")).toHaveTextContent(/власнику й адміністратору/);
    expect(spy).not.toHaveBeenCalled();
    expect(screen.queryByTestId("campaign-row")).toBeNull();
  });

  it("marketing_disabled: пояснення замість списку", async () => {
    state.enabled = false;
    wrap(<CampaignsScreen />);
    expect(await screen.findByTestId("marketing-disabled")).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Нова розсилка" })).toBeNull();
  });
});

describe("майстер розсилки", () => {
  async function toStep(n: number, promo = "autumn") {
    wrap(<CampaignWizard presetPromotionId={promo} />);
    await waitFor(() => expect(screen.getByLabelText(/Назва розсилки/)).not.toHaveValue(""));
    for (let i = 1; i < n; i++) {
      await userEvent.click(screen.getByRole("button", { name: /^(Далі|Зберегти й переглянути)$/ }));
      await screen.findByRole("heading", { level: 2, name: new RegExp(`Крок ${i + 1} з 6`) });
    }
  }

  it("крок 1: порожня назва блокує перехід з поясненням", async () => {
    wrap(<CampaignWizard />);
    await screen.findByText("Новина (без акції)");
    await userEvent.click(screen.getByRole("button", { name: "Далі" }));
    expect(await screen.findByRole("alert")).toHaveTextContent(/назву/);
  });

  it("крок 2: перегляд аудиторії - лише кількості з причинами виключення", async () => {
    await toStep(2);
    const box = await screen.findByTestId("audience-preview", {}, { timeout: 3000 });
    expect(within(box).getByTestId("audience-recipients").textContent).toMatch(/^\d+$/);
    for (const k of ["noTelegram", "unsubscribed", "noConsent", "levelMismatch", "frequency"]) expect(within(box).getByTestId(`excluded-${k}`)).toBeInTheDocument();
    expect(box.textContent).toMatch(/імен і контактів клієнтів тут немає/);
    await userEvent.clear(screen.getByLabelText(/Не були, днів/));
    expect(await screen.findByRole("alert")).toHaveTextContent(/від 1 до 730/);
    expect(screen.queryByTestId("audience-preview")).toBeNull();
  });

  it("крок 3: підстановки, незмінні рядки, приклад, валідація", async () => {
    await toStep(3);
    const area = screen.getByLabelText("Текст повідомлення");
    expect(screen.getByRole("button", { name: /Вставити Код \{code\}/ })).toBeDisabled();
    await userEvent.clear(area);
    await userEvent.click(screen.getByRole("button", { name: "Далі" }));
    expect(await screen.findByRole("alert")).toHaveTextContent(/Введіть текст/);
    await userEvent.type(area, "Привіт");
    await userEvent.click(screen.getByRole("button", { name: /Вставити Знижка/ }));
    expect((area as HTMLTextAreaElement).value).toMatch(/\{discount\}/);
    const fixed = screen.getAllByTestId("fixed-line").map((e) => e.textContent);
    expect(fixed.at(-1)).toMatch(/Не надсилати акції/);
    expect(fixed[0]).toMatch(/^Умови: знижка 20%/);
    expect(screen.getByTestId("sample-message").textContent).toMatch(/Привіт/);
  });

  it("повний шлях: перегляд, діалог підтвердження (фокус, Esc), запуск з Idempotency-Key і revision", async () => {
    const confirm = vi.fn(h.api.confirmCampaign);
    h.api.confirmCampaign = confirm;
    await toStep(5);
    expect(await screen.findByTestId("preview-text")).toHaveTextContent(/Щоб не отримувати акції/);
    expect(screen.getByTestId("window-line")).toHaveTextContent(/09:00 до 20:00/);
    await userEvent.click(screen.getByRole("button", { name: "Далі" }));
    await userEvent.click(await screen.findByRole("button", { name: "Перевірити й надіслати…" }));
    const dlg = await screen.findByRole("dialog");
    expect(dlg).toHaveAttribute("aria-modal", "true");
    expect(within(dlg).getByRole("heading", { name: /^Надіслати \d+ клієнтам\?$/ })).toHaveFocus();
    expect(dlg).toHaveTextContent("Дію не можна скасувати для вже надісланих");
    expect(within(dlg).getByTestId("confirm-summary")).toHaveTextContent(/Версія розсилки\s*1/);
    expect(within(dlg).getByTestId("confirm-text")).toHaveTextContent(/Умови: знижка 20%/);
    await userEvent.keyboard("{Escape}");
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(screen.getByRole("button", { name: "Перевірити й надіслати…" })).toHaveFocus();
    await userEvent.click(screen.getByRole("button", { name: "Перевірити й надіслати…" }));
    await userEvent.click(within(await screen.findByRole("dialog")).getByRole("button", { name: /^Надіслати \d+ клієнтам$/ }));
    await waitFor(() => expect(h.push).toHaveBeenCalledWith(expect.stringMatching(/^\/beauty\/campaigns\/k/)));
    expect(confirm).toHaveBeenCalledTimes(1);
    const [id, body, key] = confirm.mock.calls[0] as unknown as [string, { revision: number }, string];
    expect(body.revision).toBe(1);
    expect(key.length).toBeGreaterThanOrEqual(8);
    expect(h.push.mock.calls[0][0]).toBe(`/beauty/campaigns/${id}`);
  });

  it("діалог: Tab не виходить за межі; повтор після збою мережі - той самий ключ; campaign_changed повертає на перегляд", async () => {
    const keys: string[] = [];
    let n = 0;
    h.api.confirmCampaign = async (...a: unknown[]) => {
      keys.push(a[2] as string);
      n += 1;
      if (n === 1) throw new TypeError("network");
      throw new BeautyApiError(409, "campaign_changed", "SERVER");
    };
    await toStep(6);
    await userEvent.click(screen.getByRole("button", { name: "Перевірити й надіслати…" }));
    const dlg = await screen.findByRole("dialog");
    const send = within(dlg).getByRole("button", { name: /^Надіслати \d+ клієнтам$/ });
    const cancel = within(dlg).getByRole("button", { name: "Скасувати" });
    cancel.focus();
    await userEvent.tab();
    expect(send).toHaveFocus();
    await userEvent.tab({ shift: true });
    expect(cancel).toHaveFocus();
    await userEvent.click(send);
    expect(await within(dlg).findByRole("alert")).toHaveTextContent(/недоступний/);
    await userEvent.click(send);
    await waitFor(() => expect(keys).toHaveLength(2));
    expect(keys[0]).toBe(keys[1]);
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    expect(await screen.findByTestId("wizard-notice")).toHaveTextContent(/Кампанію змінили/);
    expect(screen.getByRole("heading", { level: 2, name: /Крок 5 з 6/ })).toBeInTheDocument();
    expect(h.push).not.toHaveBeenCalled();
  });

  it("запланувати: без дати діалог не відкривається; пояснення вікна 09:00-20:00", async () => {
    await toStep(6);
    await userEvent.click(screen.getByLabelText("Запланувати на дату й час"));
    await userEvent.click(screen.getByRole("button", { name: "Перевірити й надіслати…" }));
    expect(await screen.findByRole("alert")).toHaveTextContent(/Вкажіть дату й час/);
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(screen.getByText(/лише з 09:00 до 20:00 за місцевим часом закладу клієнта/)).toBeInTheDocument();
  });

  it("code_only: крок «Купон» - персональні за замовчуванням, загальні коди лише активні", async () => {
    await toStep(4, "vip15");
    expect(screen.getByLabelText(/Персональний одноразовий код/)).toBeChecked();
    await userEvent.click(screen.getByRole("button", { name: "Зберегти й переглянути" }));
    expect(await screen.findByRole("alert")).toHaveTextContent(/до якої дати/);
    await userEvent.click(screen.getByRole("radio", { name: /Загальний код/ }));
    expect(await screen.findByText("AUTUMN15")).toBeInTheDocument();
    expect(screen.queryByText("OLD2025")).toBeNull();
  });
});

describe("деталі кампанії", () => {
  it("статус, прогрес, статистика, примітка про «переглянуто», CSV", async () => {
    wrap(<CampaignDetailScreen id="k4" />);
    expect(await screen.findByTestId("campaign-status")).toHaveTextContent("Завершено");
    await waitFor(() => expect(screen.getByTestId("progress-text")).toHaveTextContent("Надіслано 78 з 85"));
    expect(await screen.findByTestId("viewed-note")).toHaveTextContent(/Telegram не повідомляє/);
    expect(await screen.findByText("Натиснули «Записатися»")).toBeInTheDocument();
    expect(await screen.findByTestId("coupon-summary")).toHaveTextContent(/видано 78/);
    expect(screen.queryByRole("button", { name: "Скасувати кампанію" })).toBeNull();
    expect(screen.getByRole("button", { name: "Завантажити CSV" })).toBeInTheDocument();
  });

  it("скасування sending: діалог, наслідки, статус «Скасовано»", async () => {
    wrap(<CampaignDetailScreen id="k3" />);
    await userEvent.click(await screen.findByRole("button", { name: "Скасувати кампанію" }));
    const dlg = await screen.findByRole("dialog");
    expect(dlg).toHaveTextContent(/Уже доставлені повідомлення і купони/);
    await userEvent.click(within(dlg).getByRole("button", { name: "Скасувати кампанію" }));
    await waitFor(() => expect(screen.getByTestId("campaign-status")).toHaveTextContent("Скасовано"));
  });

  it("чернетка відкривається в майстрі; 404 обробляється", async () => {
    wrap(<CampaignDetailScreen id="k1" />);
    expect(await screen.findByRole("heading", { level: 1, name: "Редагування розсилки" })).toBeInTheDocument();
    document.body.innerHTML = "";
    wrap(<CampaignDetailScreen id="nope" />);
    expect(await screen.findByRole("alert")).toHaveTextContent(/не знайдено/);
  });
});

describe("картка клієнта: згода", () => {
  it("рівень, джерело, версія тексту, журнал", async () => {
    wrap(<MarketingConsentCard clientId="oc" />);
    const st = await screen.findByTestId("consent-status");
    expect(st).toHaveTextContent("Повна інформація");
    expect(st).toHaveTextContent(/Клієнт обрав у боті/);
    expect(st).toHaveTextContent("mc-v1");
    await userEvent.click(screen.getByText(/Історія змін/));
    expect(screen.getByTestId("consent-history").children).toHaveLength(1);
  });

  it("зниження за підтвердженням; повернення рівня адміном - лише з підставою", async () => {
    wrap(<MarketingConsentCard clientId="oc" />);
    await userEvent.click(await screen.findByRole("button", { name: "Вимкнути розсилки" }));
    const dlg = await screen.findByRole("dialog");
    await userEvent.click(within(dlg).getByRole("button", { name: "Вимкнути розсилки" }));
    await waitFor(() => expect(screen.getByTestId("consent-status")).toHaveTextContent("Розсилки вимкнено"));
    await userEvent.click(screen.getByRole("button", { name: /Підвищити згоду…/ }));
    const form = screen.getByRole("form", { name: "Підвищення згоди" });
    await userEvent.click(within(form).getByRole("button", { name: "Підвищити згоду" }));
    expect(within(form).getByRole("alert")).toHaveTextContent("Оберіть підставу");
    await userEvent.selectOptions(within(form).getByLabelText("Підстава"), "written");
    await userEvent.click(within(form).getByRole("button", { name: "Підвищити згоду" }));
    expect(within(form).getByRole("alert")).toHaveTextContent("Додайте нотатку");
    await userEvent.type(within(form).getByLabelText(/Нотатка/), "Анкета від 12.10");
    await userEvent.click(within(form).getByLabelText("Лише акції на мої послуги"));
    await userEvent.click(within(form).getByRole("button", { name: "Підвищити згоду" }));
    await waitFor(() => expect(screen.getByTestId("consent-status")).toHaveTextContent("Лише акції на мої послуги"));
  });

  it("AdminGrant вимкнено: підвищення недоступне з поясненням", async () => {
    state.grant = false;
    wrap(<MarketingConsentCard clientId="tg" />);
    expect(await screen.findByTestId("grant-unavailable")).toHaveTextContent(/лише сам клієнт у боті/);
    expect(screen.queryByRole("button", { name: /Підвищити згоду…/ })).toBeNull();
  });

  it("409 admin_grant_disabled при спробі ховає форму з поясненням", async () => {
    h.api.getMarketingConsent = async () => ({ channels: [{ channel: "telegram", level: "none", state: "unset" }], telegramAvailable: true, history: [] });
    h.api.setMarketingConsent = async () => {
      throw new BeautyApiError(409, "admin_grant_disabled", "SERVER");
    };
    wrap(<MarketingConsentCard clientId="sm" />);
    await userEvent.click(await screen.findByRole("button", { name: /Підвищити згоду…/ }));
    const form = screen.getByRole("form", { name: "Підвищення згоди" });
    await userEvent.selectOptions(within(form).getByLabelText("Підстава"), "other");
    await userEvent.type(within(form).getByLabelText(/Нотатка/), "x");
    await userEvent.click(within(form).getByRole("button", { name: "Підвищити згоду" }));
    expect(await screen.findByTestId("grant-unavailable")).toBeInTheDocument();
    expect(screen.queryByRole("form", { name: "Підвищення згоди" })).toBeNull();
  });

  it("specialist: картка не рендериться й не робить запитів", () => {
    setup("specialist");
    const spy = vi.fn();
    h.api.getMarketingConsent = spy;
    const { container } = wrap(<MarketingConsentCard clientId="oc" />);
    expect(container).toBeEmptyDOMElement();
    expect(spy).not.toHaveBeenCalled();
  });

  it("купони клієнта: лише codeHint, повного коду в DOM немає; вимкнено - картки немає", async () => {
    wrap(<ClientCouponsCard clientId="oc" />);
    const item = await screen.findByTestId("client-coupon");
    expect(item).toHaveTextContent("•••••-••M3D");
    expect(item).toHaveTextContent(/Використано/);
    expect(document.body.textContent).not.toMatch(/[A-Z0-9]{5}-[A-Z0-9]{5}/);
    document.body.innerHTML = "";
    state.enabled = false;
    const { container } = wrap(<ClientCouponsCard clientId="oc" />);
    await waitFor(() => expect(container).toBeEmptyDOMElement());
  });
});

describe("акції: кнопка розсилки, коди", () => {
  it("«Розіслати клієнтам» веде в майстер з акцією; активна розсилка - бейдж і пояснення", async () => {
    wrap(<PromotionsCard marketingOn />);
    const link = await screen.findByRole("link", { name: "Розіслати клієнтам: Осінній манікюр" });
    expect(link).toHaveAttribute("href", "/beauty/campaigns/new?promotionId=autumn");
    expect(screen.queryByRole("link", { name: /Літня знижка/ })).toBeNull();
    expect(screen.getAllByText("В активній розсилці").length).toBeGreaterThan(0);
    expect(screen.getByTestId("promotion-in-campaign-note")).toHaveTextContent(/лише вимкнути/);
  });

  it("marketingOn=false: кнопок розсилки немає", async () => {
    wrap(<PromotionsCard marketingOn={false} />);
    await screen.findAllByTestId("promotion-item");
    expect(screen.queryByRole("link", { name: /Розіслати клієнтам/ })).toBeNull();
  });

  it("коди: лічильник, відкликання за підтвердженням", async () => {
    wrap(<CodesCard />);
    const rows = await screen.findAllByTestId("coupon-row");
    expect(rows[0]).toHaveTextContent("AUTUMN15");
    expect(rows[0]).toHaveTextContent("12 / 100");
    await userEvent.click(screen.getByRole("button", { name: "Відкликати код AUTUMN15" }));
    await userEvent.click(within(await screen.findByRole("dialog")).getByRole("button", { name: "Відкликати" }));
    await waitFor(() => expect(screen.queryByRole("button", { name: "Відкликати код AUTUMN15" })).toBeNull());
  });
});

describe("позначка купона на записі", () => {
  const appt = { priceOriginal: 1000, priceFinal: 850, coupon: { id: "c", kind: "personal" as const, codeHint: "M3D", promotionName: "Для своїх −15%", outcome: "applied" as const } };

  it("керівник бачить назву, знижку й codeHint; specialist - без codeHint", () => {
    const { unmount } = render(<CouponBadge appt={appt} />);
    expect(screen.getByTestId("coupon-badge")).toHaveTextContent("Записано за купоном");
    expect(screen.getByTestId("coupon-badge")).toHaveTextContent(/знижка −150 ₴/);
    expect(screen.getByTestId("coupon-badge")).toHaveTextContent("•••••-••M3D");
    unmount();
    h.auth.user.role = "specialist";
    render(<CouponBadge appt={appt} />);
    expect(screen.getByTestId("coupon-badge")).not.toHaveTextContent("M3D");
    expect(screen.getByTestId("coupon-badge")).toHaveTextContent("Для своїх");
  });

  it("не показується з not_better", () => {
    const { container } = render(<CouponBadge appt={{ priceFinal: 1, coupon: { ...appt.coupon, outcome: "not_better" } }} />);
    expect(container).toBeEmptyDOMElement();
  });
});
