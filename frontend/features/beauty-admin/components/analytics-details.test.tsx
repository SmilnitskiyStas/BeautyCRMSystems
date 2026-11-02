import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { BeautyApiError } from "@/features/beauty-auth/errors";
import type { AnalyticsDetails, AnalyticsDetailsQuery, DetailsItem, LocationStat, NetworkAnalytics, NetworkSeries } from "../types";

const role = { current: "owner" };
const getLocations = vi.fn();
const getNetworkAnalytics = vi.fn<(q: unknown) => Promise<NetworkAnalytics>>();
const getNetworkSeries = vi.fn<(q: unknown) => Promise<NetworkSeries>>();
const getLocationsAnalytics = vi.fn<(q: unknown) => Promise<LocationStat[]>>();
const getNetworkDetails = vi.fn<(q: AnalyticsDetailsQuery) => Promise<AnalyticsDetails>>();
vi.mock("../api", () => ({
  beautyApi: {
    getLocations: () => getLocations(),
    getNetworkAnalytics: (q: unknown) => getNetworkAnalytics(q),
    getNetworkSeries: (q: unknown) => getNetworkSeries(q),
    getLocationsAnalytics: (q: unknown) => getLocationsAnalytics(q),
    getNetworkDetails: (q: AnalyticsDetailsQuery) => getNetworkDetails(q),
  },
}));
vi.mock("@/features/beauty-auth/components/auth-provider", () => ({ useAuth: () => ({ user: { id: "u", email: "e", fullName: "n", role: role.current } }) }));
vi.mock("next/navigation", () => ({ useSearchParams: () => new URLSearchParams(window.location.search) }));

import { AnalyticsScreen } from "./analytics-screen";

const T = "2026-10-24";
const totals = { appointments: 10, completed: 8, cancelled: 2, noShow: 0, revenue: 12000, averageCheck: 1500, cancellationRate: 0.2, newClients: 3, cost: 2000, masterPayout: 3000, netIncome: 7000, marginPercent: 58.3, withoutCost: 2, revenueWithoutCost: 1800.5 };
const net = (over: Partial<NetworkAnalytics> = {}): NetworkAnalytics => ({ fromDate: "2026-09-25", toDate: T, timezone: "Europe/Kyiv", totals, previous: null, ...over });
const series = (granularity: NetworkSeries["granularity"] = "day"): NetworkSeries => ({
  fromDate: "2026-10-23",
  toDate: T,
  timezone: "Europe/Kyiv",
  granularity,
  points: [
    { from: "2026-10-23", to: granularity === "week" ? "2026-10-29" : "2026-10-23", appointments: 2, completed: 2, cancelled: 0, noShow: 0, revenue: 4200, cost: 800, masterPayout: 1200, netIncome: 2200, withoutCost: 0 },
    { from: "2026-10-24", to: granularity === "week" ? "2026-10-30" : "2026-10-24", appointments: 1, completed: 1, cancelled: 0, noShow: 0, revenue: 1000, cost: 700, masterPayout: 500, netIncome: -200, withoutCost: 1 },
  ],
});
const item = (n: number, over: Partial<DetailsItem> = {}): DetailsItem => ({
  appointmentId: `a${n}`, localDate: T, localTime: `1${n}:00`, location: { id: "L1", name: "Центр" }, service: { id: "s1", name: `Манікюр ${n}` }, specialist: { id: "m1", name: "Марина Бойко" },
  priceOriginal: 1000, discount: 100, promotion: { id: "p", name: "Осінь" }, priceFinal: 900, cost: 220, masterPercent: 40, masterPayout: 360, net: 320, costMissing: false, ...over,
});
const summary = { appointments: 4, completed: 3, cancelled: 1, noShow: 0, revenue: 2700, discountTotal: 300, cost: 440, masterPayout: 720, netIncome: 1540.5, marginPercent: 57.1, withoutCost: 1, revenueWithoutCost: 900 };
const locRows = [
  { locationId: "L1", name: "Центр", timezone: "Europe/Kyiv", completed: 2, revenue: 1800, cost: 440, masterPayout: 720, netIncome: 640.5, marginPercent: 35.6, withoutCost: 0 },
  { locationId: "L2", name: "Поділ", timezone: "Europe/Kyiv", completed: 1, revenue: 900, cost: 0, masterPayout: 0, netIncome: 900, marginPercent: 100, withoutCost: 1 },
];
const details = (over: Partial<AnalyticsDetails> = {}): AnalyticsDetails => ({
  fromDate: T, toDate: T, timezone: "Europe/Kyiv", summary, locations: locRows, items: [item(1), item(2, { cost: null, masterPercent: null, masterPayout: null, net: 900, costMissing: true, discount: 0, promotion: undefined })], hasMore: false, ...over,
});

function renderScreen() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <AnalyticsScreen />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.useFakeTimers({ toFake: ["Date"], now: new Date("2026-10-24T09:00:00Z") });
  window.history.replaceState(null, "", "/beauty/analytics");
  role.current = "owner";
  getLocations.mockReset().mockResolvedValue([{ id: "L1", name: "Центр", timezone: "Europe/Kyiv" }, { id: "L2", name: "Поділ", timezone: "Europe/Kyiv" }]);
  getNetworkAnalytics.mockReset().mockImplementation(async () => net());
  getNetworkSeries.mockReset().mockImplementation(async () => series());
  getLocationsAnalytics.mockReset().mockResolvedValue([]);
  getNetworkDetails.mockReset().mockImplementation(async () => details());
});
afterEach(() => vi.useRealTimers());

const setup = () => userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
const bar = (name: RegExp) => screen.findByRole("button", { name });

describe("§24.8.1: клікабельні стовпці графіка", () => {
  it("кожен стовпець - button з aria-label «період: виручка, чистий дохід. Відкрити деталі»", async () => {
    renderScreen();
    const b = await bar(/23 жовт\.?: виручка 4 200 ₴, чистий дохід 2 200 ₴\. Відкрити деталі/);
    expect(b.tagName).toBe("BUTTON");
    expect(b).toHaveAttribute("type", "button");
    expect(await bar(/24 жовт\.?: виручка 1 000 ₴, чистий дохід −200 ₴\. Відкрити деталі/)).toBeInTheDocument();
  });

  it("клік відкриває діалог з заголовком дня й запитом date", async () => {
    const user = setup();
    renderScreen();
    await user.click(await bar(/24 жовт/));
    const dlg = await screen.findByRole("dialog", { name: "Деталі дня сб, 24.10" });
    expect(dlg).toHaveAttribute("aria-modal", "true");
    await waitFor(() => expect(getNetworkDetails).toHaveBeenCalled());
    expect(getNetworkDetails.mock.calls[0][0]).toEqual({ date: T });
  });

  it("Enter і Space на сфокусованому стовпці відкривають панель", async () => {
    const user = setup();
    renderScreen();
    const b = await bar(/23 жовт/);
    b.focus();
    await user.keyboard("{Enter}");
    expect(await screen.findByRole("dialog", { name: /Деталі дня/ })).toBeInTheDocument();
    await user.keyboard("{Escape}");
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    b.focus();
    await user.keyboard(" ");
    expect(await screen.findByRole("dialog", { name: /Деталі дня пт, 23\.10/ })).toBeInTheDocument();
  });

  it("тиждень: запит from+to з точки; заголовок діапазону", async () => {
    getNetworkSeries.mockImplementation(async () => series("week"));
    const user = setup();
    renderScreen();
    await user.click(await bar(/23 жовт/));
    await screen.findByRole("dialog", { name: "Деталі тижня 23.10–29.10" });
    expect(getNetworkDetails.mock.calls[0][0]).toEqual({ from: "2026-10-23", to: "2026-10-29" });
  });

  it("місяць: «Деталі місяця жовтень»", async () => {
    getNetworkSeries.mockImplementation(async () => ({ ...series("month"), points: [{ from: "2026-10-01", to: "2026-10-31", appointments: 1, completed: 1, cancelled: 0, noShow: 0, revenue: 1000, netIncome: 400 }] }));
    const user = setup();
    renderScreen();
    await user.click(await bar(/жовтень 2026/));
    await screen.findByRole("dialog", { name: "Деталі місяця жовтень" });
    expect(getNetworkDetails.mock.calls[0][0]).toEqual({ from: "2026-10-01", to: "2026-10-31" });
  });

  it("Esc закриває, фокус повертається на стовпець; фокус спершу на заголовку", async () => {
    const user = setup();
    renderScreen();
    const b = await bar(/24 жовт/);
    await user.click(b);
    const dlg = await screen.findByRole("dialog");
    await waitFor(() => expect(within(dlg).getByRole("heading", { name: /Деталі дня/ })).toHaveFocus());
    await user.keyboard("{Escape}");
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    expect(b).toHaveFocus();
  });

  it("Tab не виходить за межі діалогу (пастка фокуса)", async () => {
    const user = setup();
    renderScreen();
    await user.click(await bar(/24 жовт/));
    const dlg = await screen.findByRole("dialog");
    await screen.findByTestId("details-summary");
    for (let i = 0; i < 14; i++) {
      await user.tab();
      expect(dlg.contains(document.activeElement)).toBe(true);
    }
    for (let i = 0; i < 14; i++) {
      await user.tab({ shift: true });
      expect(dlg.contains(document.activeElement)).toBe(true);
    }
  });

  it("перемикач «Виручка / Чистий дохід» міняє заголовок і стовпці, не ламає порівняння", async () => {
    const user = setup();
    renderScreen();
    await bar(/23 жовт/);
    await user.click(screen.getByRole("radio", { name: "Чистий дохід" }));
    expect(screen.getByRole("heading", { name: /Чистий дохід мережі по днях/ })).toBeInTheDocument();
    expect(screen.getByRole("list", { name: "Чистий дохід за періодами" })).toBeInTheDocument();
    await user.click(screen.getByRole("switch", { name: "Порівняти з попереднім періодом" }));
    await waitFor(() => expect(getNetworkAnalytics).toHaveBeenLastCalledWith(expect.objectContaining({ compare: true })));
    expect(screen.getByRole("radio", { name: "Чистий дохід" })).toHaveAttribute("aria-checked", "true");
    expect(await bar(/23 жовт/)).toBeInTheDocument();
  });

  it("без чистого доходу в series перемикача немає", async () => {
    getNetworkSeries.mockImplementation(async () => ({ ...series(), points: series().points.map(({ from, to, appointments, completed, cancelled, noShow, revenue }) => ({ from, to, appointments, completed, cancelled, noShow, revenue })) }));
    renderScreen();
    const b = await bar(/23 жовт/);
    expect(b).toHaveAccessibleName(/виручка 4 200 ₴\. Відкрити деталі/);
    expect(screen.queryByRole("radio", { name: "Чистий дохід" })).toBeNull();
  });
});

describe("§24.8.1: вміст панелі", () => {
  async function open() {
    const user = setup();
    renderScreen();
    await user.click(await bar(/24 жовт/));
    return { user, dlg: await screen.findByRole("dialog") };
  }

  it("підсумок: виручка, собівартість, виплата, чистий дохід, маржа, завершено, без собівартості", async () => {
    const { dlg } = await open();
    const s = await within(dlg).findByTestId("details-summary");
    const pair = (label: string) => within(s).getByText(label).closest("div")!.textContent;
    expect(pair("Виручка")).toContain("2 700 ₴");
    expect(pair("Собівартість")).toContain("440 ₴");
    expect(pair("Виплата майстру")).toContain("720 ₴");
    expect(pair("Чистий дохід")).toContain("1 540,50 ₴");
    expect(pair("Маржа")).toContain("57,1%");
    expect(pair("Завершено")).toContain("3");
    expect(pair("Знижки")).toContain("300 ₴");
    expect(pair("Без собівартості")).toContain("1");
  });

  it("банер «без собівартості» з посиланням на послуги; без withoutCost банера немає", async () => {
    const { dlg } = await open();
    const banner = await within(dlg).findByTestId("details-without-cost");
    expect(banner).toHaveAttribute("role", "status");
    expect(banner).toHaveTextContent("1 завершений запис без собівартості (900 ₴ виручки) — чистий дохід завищено.");
    expect(within(banner).getByRole("link", { name: "Задати собівартість →" })).toHaveAttribute("href", "/beauty/services");
  });

  it("від'ємний чистий дохід і маржа null: «−», «—»", async () => {
    getNetworkDetails.mockImplementation(async () => details({ summary: { ...summary, revenue: 0, netIncome: -150, marginPercent: null, withoutCost: 0 } }));
    const { dlg } = await open();
    const s = await within(dlg).findByTestId("details-summary");
    expect(within(s).getByText("Чистий дохід").closest("div")).toHaveTextContent("−150 ₴");
    expect(within(s).getByText("Маржа").closest("div")).toHaveTextContent("—");
    expect(within(dlg).queryByTestId("details-without-cost")).toBeNull();
  });

  it("розбивка по закладах і записи: час, послуга, майстер, заклад, знижка/акція, собівартість, виплата, внесок", async () => {
    const { dlg } = await open();
    const locs = await within(dlg).findAllByTestId("details-location");
    expect(locs).toHaveLength(2);
    expect(locs[0]).toHaveTextContent("Центр");
    expect(locs[0]).toHaveTextContent("640,50 ₴");
    expect(locs[1]).toHaveTextContent("без собівартості 1");
    const rows = within(dlg).getAllByTestId("details-item");
    expect(rows).toHaveLength(2);
    expect(rows[0]).toHaveTextContent("11:00 · Манікюр 1");
    expect(rows[0]).toHaveTextContent("Марина Бойко · Центр");
    expect(rows[0]).toHaveTextContent("Знижка: Осінь−100 ₴");
    expect(rows[0]).toHaveTextContent("Собівартість220 ₴");
    expect(rows[0]).toHaveTextContent("Виплата майстру360 ₴ (40%)");
    expect(rows[0]).toHaveTextContent("Чистий внесок320 ₴");
    expect(rows[1]).toHaveTextContent("Собівартістьне задана");
    expect(rows[1]).toHaveTextContent("без собівартості");
    expect(within(dlg).queryByRole("table")).toBeNull(); // картки, а не таблиця: 320 px без горизонтального скролу
    expect(dlg.className).toContain("overflow-x-hidden");
  });

  it("ПД клієнтів не показуються, навіть якщо відповідь їх помилково містить", async () => {
    const leaky = { ...item(1), clientName: "Олена Кравченко", clientId: "c-1", phone: "+380501112233" } as DetailsItem;
    getNetworkDetails.mockImplementation(async () => details({ items: [leaky] }));
    const { dlg } = await open();
    await within(dlg).findAllByTestId("details-item");
    expect(dlg.textContent).not.toMatch(/Олена|Кравченко|380501112233|c-1/);
  });

  it("порожньо: «Завершених записів немає»", async () => {
    getNetworkDetails.mockImplementation(async () => details({ items: [], summary: { ...summary, completed: 0, revenue: 0, netIncome: 0, withoutCost: 0, marginPercent: null }, locations: [] }));
    const { dlg } = await open();
    expect(await within(dlg).findByText("Завершених записів немає")).toBeInTheDocument();
  });

  it("помилка 429: зрозуміле повідомлення й повтор", async () => {
    getNetworkDetails.mockRejectedValueOnce(new BeautyApiError(429, "rate_limited", "x", 30));
    const { user, dlg } = await open();
    const alert = await within(dlg).findByRole("alert");
    expect(alert).toHaveTextContent("Забагато запитів");
    await user.click(within(dlg).getByRole("button", { name: "Спробувати ще раз" }));
    expect(await within(dlg).findByTestId("details-summary")).toBeInTheDocument();
  });

  it("завантаження: статус «Завантаження…»", async () => {
    getNetworkDetails.mockImplementation(() => new Promise(() => {}));
    const { dlg } = await open();
    expect(within(dlg).getByText("Завантаження…")).toBeInTheDocument();
  });

  it("«Показати ще»: наступна сторінка за nextCursor, записи додаються без дублів", async () => {
    getNetworkDetails.mockImplementation(async (q) =>
      q.cursor ? details({ items: [item(3)], hasMore: false }) : details({ items: [item(1), item(2)], hasMore: true, nextCursor: "CUR1" }),
    );
    const { user, dlg } = await open();
    await within(dlg).findAllByTestId("details-item");
    await user.click(within(dlg).getByRole("button", { name: "Показати ще" }));
    await waitFor(() => expect(within(dlg).getAllByTestId("details-item")).toHaveLength(3));
    expect(getNetworkDetails).toHaveBeenLastCalledWith({ date: T, cursor: "CUR1" });
    expect(within(dlg).queryByRole("button", { name: "Показати ще" })).toBeNull();
  });

  it("чипи закладів: фільтр додає locationId; чипи лишаються з повного списку; можна повернутися до «Усі заклади»", async () => {
    getNetworkDetails.mockImplementation(async (q) =>
      q.locationId ? details({ locations: [locRows[1]], location: { id: "L2", name: "Поділ", timezone: "Europe/Kyiv" } }) : details(),
    );
    const { user, dlg } = await open();
    await within(dlg).findByTestId("details-summary");
    await user.click(within(dlg).getByRole("button", { name: "Поділ" }));
    await waitFor(() => expect(getNetworkDetails).toHaveBeenLastCalledWith({ date: T, locationId: "L2" }));
    await waitFor(() => expect(within(dlg).getAllByTestId("details-location")).toHaveLength(1));
    expect(within(dlg).getByRole("button", { name: "Центр" })).toBeInTheDocument();
    expect(within(dlg).getByRole("button", { name: "Поділ" })).toHaveAttribute("aria-pressed", "true");
    await user.click(within(dlg).getByRole("button", { name: "Усі заклади" }));
    await waitFor(() => expect(within(dlg).getAllByTestId("details-location")).toHaveLength(2));
  });

  it("експорт CSV: довантажує решту сторінок (take 200) і віддає файл без ПД", async () => {
    const create = vi.fn<(b: Blob) => string>(() => "blob:x");
    Object.assign(URL, { createObjectURL: create, revokeObjectURL: vi.fn() });
    const click = vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(() => {});
    getNetworkDetails.mockImplementation(async (q) => (q.cursor ? details({ items: [item(3)], hasMore: false }) : details({ items: [item(1), item(2)], hasMore: true, nextCursor: "CUR1" })));
    const { user, dlg } = await open();
    await within(dlg).findAllByTestId("details-item");
    await user.click(within(dlg).getByRole("button", { name: "Експорт CSV" }));
    await waitFor(() => expect(create).toHaveBeenCalledTimes(1));
    expect(getNetworkDetails).toHaveBeenLastCalledWith({ date: T, cursor: "CUR1", take: 200 });
    const text = await new Promise<string>((resolve) => {
      const r = new FileReader();
      r.onload = () => resolve(String(r.result));
      r.readAsText(create.mock.calls[0][0]);
    });
    const lines = text.split("\r\n");
    expect(lines[0]).toContain("Дата;Час;Заклад;Послуга;Майстер");
    expect(lines).toHaveLength(1 + 3 + 1 + 1); // заголовок + 3 записи + «Разом» + хвіст
    expect(lines.at(-2)!.startsWith("Разом;")).toBe(true);
    expect(text).not.toMatch(/Олена|\+380|a1;|clientId/);
    click.mockRestore();
  });
});

describe("§24.8.1: KPI й банер на екрані", () => {
  it("нові KPI «Собівартість», «Виплата майстрам», «Чистий дохід» (маржа в примітці)", async () => {
    renderScreen();
    const cost = await screen.findByText("Собівартість");
    expect(cost.parentElement).toHaveTextContent("2 000 ₴");
    expect(screen.getByText("Виплата майстрам").parentElement).toHaveTextContent("3 000 ₴");
    const nets = screen.getAllByText("Чистий дохід");
    expect(nets.some((n) => n.parentElement?.textContent?.includes("7 000 ₴") && n.parentElement.textContent.includes("маржа 58,3%"))).toBe(true);
  });

  it("банер withoutCost > 0 з сумою виручки й посиланням; withoutCost = 0 - без банера", async () => {
    renderScreen();
    const banner = await screen.findByTestId("without-cost-banner");
    expect(banner).toHaveAttribute("role", "status");
    expect(banner).toHaveTextContent("2 завершені записи без собівартості (1 800,50 ₴ виручки) — чистий дохід завищено.");
    expect(within(banner).getByRole("link", { name: "Задати собівартість →" })).toHaveAttribute("href", "/beauty/services");
  });

  it("без withoutCost банера немає", async () => {
    getNetworkAnalytics.mockImplementation(async () => net({ totals: { ...totals, withoutCost: 0, revenueWithoutCost: 0 } }));
    renderScreen();
    await screen.findByText("Собівартість");
    expect(screen.queryByTestId("without-cost-banner")).toBeNull();
  });

  it("marginPercent = null -> «—»; без фінансових полів (старий backend) KPI не з'являються", async () => {
    getNetworkAnalytics.mockImplementation(async () => net({ totals: { ...totals, marginPercent: null, revenue: 0 } }));
    const { unmount } = renderScreen();
    await screen.findByText("Собівартість");
    expect(screen.getAllByText("маржа —").length).toBeGreaterThan(0);
    unmount();
    getNetworkAnalytics.mockImplementation(async () => net({ totals: { appointments: 1, completed: 1, cancelled: 0, noShow: 0, revenue: 900, averageCheck: 900, cancellationRate: 0, newClients: 0 } }));
    renderScreen();
    await screen.findByText("Виручка мережі");
    expect(screen.queryByText("Собівартість")).toBeNull();
    expect(screen.queryByTestId("without-cost-banner")).toBeNull();
  });

  it("порівняння: delta нових KPI (гроші - %, маржа - п.п.)", async () => {
    getNetworkAnalytics.mockImplementation(async (q) =>
      net({ previous: (q as { compare: boolean }).compare ? { ...totals, fromDate: "2026-08-26", toDate: "2026-09-24", cost: 1000, masterPayout: 3000, netIncome: 5000, marginPercent: 50 } : null }),
    );
    const user = setup();
    renderScreen();
    await screen.findByText("Собівартість");
    await user.click(screen.getByRole("switch", { name: "Порівняти з попереднім періодом" }));
    // собівартість 2000 vs 1000: зростання - погано
    const costDelta = await screen.findByText("▲ +100% (+1 000 ₴)");
    expect(costDelta.closest("[data-tone]")).toHaveAttribute("data-tone", "bad");
    // чистий дохід 7000 vs 5000: +40%, добре
    expect(screen.getByText("▲ +40% (+2 000 ₴)").closest("[data-tone]")).toHaveAttribute("data-tone", "good");
    // виплата 3000 vs 3000: без змін
    expect(screen.getAllByText("▬ 0% (0 ₴)").length).toBeGreaterThan(0);
    // маржа 58,3 vs 50 = +8,3 п.п.
    expect(screen.getByText("▲ +8,3 п.п.")).toBeInTheDocument();
  });
});

describe("§24.9: ролі", () => {
  it("specialist: розділ недоступний, запитів аналітики немає", async () => {
    role.current = "specialist";
    renderScreen();
    expect(screen.getByRole("note")).toHaveTextContent("лише власнику й адміністратору");
    await Promise.resolve();
    expect(getNetworkAnalytics).not.toHaveBeenCalled();
    expect(getNetworkSeries).not.toHaveBeenCalled();
    expect(getNetworkDetails).not.toHaveBeenCalled();
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("admin бачить фінанси так само, як owner", async () => {
    role.current = "admin";
    renderScreen();
    expect(await screen.findByText("Собівартість")).toBeInTheDocument();
  });
});
