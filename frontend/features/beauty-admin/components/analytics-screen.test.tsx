import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { LocationBreakdown, LocationStat, NetworkAnalytics, NetworkSeries, PromotionAnalytics } from "../types";

const getLocations = vi.fn();
const getNetworkAnalytics = vi.fn<(q: unknown) => Promise<NetworkAnalytics>>();
const getNetworkSeries = vi.fn<(q: unknown) => Promise<NetworkSeries>>();
const getLocationsAnalytics = vi.fn<(q: unknown) => Promise<LocationStat[]>>();
const getLocationBreakdown = vi.fn<(id: string, q: unknown) => Promise<LocationBreakdown>>();
const getPromotionAnalytics = vi.fn<(q: unknown) => Promise<PromotionAnalytics>>();
vi.mock("../api", () => ({
  beautyApi: {
    getLocations: () => getLocations(),
    getNetworkAnalytics: (q: unknown) => getNetworkAnalytics(q),
    getNetworkSeries: (q: unknown) => getNetworkSeries(q),
    getLocationsAnalytics: (q: unknown) => getLocationsAnalytics(q),
    getLocationBreakdown: (id: string, q: unknown) => getLocationBreakdown(id, q),
    getPromotionAnalytics: (q: unknown) => getPromotionAnalytics(q),
  },
}));

vi.mock("next/navigation", () => ({ useSearchParams: () => new URLSearchParams(window.location.search) }));

import { AnalyticsScreen } from "./analytics-screen";

const T = "2026-10-24";
const totals = { appointments: 10, completed: 8, cancelled: 2, noShow: 0, revenue: 12000, averageCheck: 1500, cancellationRate: 0.2, newClients: 3 };
const net = (over: Partial<NetworkAnalytics> = {}): NetworkAnalytics => ({ fromDate: "2026-09-25", toDate: T, timezone: "Europe/Kyiv", totals, previous: null, ...over });
const series = (granularity: NetworkSeries["granularity"] = "day"): NetworkSeries => ({
  fromDate: "2026-10-23",
  toDate: T,
  timezone: "Europe/Kyiv",
  granularity,
  points: [
    { from: "2026-10-23", to: "2026-10-23", appointments: 2, completed: 2, cancelled: 0, noShow: 0, revenue: 4200 },
    { from: "2026-10-24", to: "2026-10-24", appointments: 1, completed: 1, cancelled: 0, noShow: 0, revenue: 38000 },
  ],
});
const metrics = { appointments: 5, completed: 4, cancelled: 1, noShow: 0, revenue: 4000, busyMinutes: 240, availableMinutes: 480, load: 0.5 };
const locRow = (id: string, name: string, over: Partial<LocationStat> = {}): LocationStat => ({ locationId: id, name, timezone: "Europe/Kyiv", isActive: true, ...metrics, previous: null, ...over });
const breakdown = (id: string): LocationBreakdown => ({
  locationId: id,
  name: "x",
  timezone: "Europe/Kyiv",
  fromDate: "2026-09-25",
  toDate: T,
  busyMinutes: 240,
  availableMinutes: 480,
  load: 0.5,
  specialists: [
    { specialistId: "s1", name: "Марина", isActive: true, appointments: 3, completed: 2, cancelled: 1, noShow: 0, revenue: 2000, busyMinutes: 120, availableMinutes: 240, load: 0.5 },
    { specialistId: "s2", name: "Анна", isActive: true, appointments: 0, completed: 0, cancelled: 0, noShow: 0, revenue: 0, busyMinutes: 0, availableMinutes: 0, load: null },
  ],
  services: [{ serviceId: "v", name: "Манікюр", completed: 2, revenue: 2000 }],
});

function renderScreen() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <AnalyticsScreen />
    </QueryClientProvider>,
  );
}

const lastQuery = (fn: { mock: { calls: unknown[][] } }) => fn.mock.calls.at(-1)![fn.mock.calls.at(-1)!.length - 1] as { from: string; to: string; compare?: boolean };

beforeEach(() => {
  // 24.10.2026 12:00 за Києвом
  vi.useFakeTimers({ toFake: ["Date"], now: new Date("2026-10-24T09:00:00Z") });
  window.history.replaceState(null, "", "/beauty/analytics");
  getLocations.mockReset().mockResolvedValue([
    { id: "11111111-aaaa", name: "Центр", timezone: "Europe/Kyiv" },
    { id: "22222222-bbbb", name: "Поділ", timezone: "Europe/Kyiv" },
  ]);
  getNetworkAnalytics.mockReset().mockImplementation(async () => net());
  getNetworkSeries.mockReset().mockImplementation(async () => series());
  getLocationsAnalytics.mockReset().mockImplementation(async () => [locRow("11111111-aaaa", "Центр"), locRow("22222222-bbbb", "Поділ")]);
  getLocationBreakdown.mockReset().mockImplementation(async (id) => breakdown(id));
  getPromotionAnalytics.mockReset().mockResolvedValue({ rows: [{ id: "p", name: "=Осінь", period: "постійна", where: "Центр", uses: 2, discountTotal: 100, revenue: 800, previous: null }] });
});
afterEach(() => vi.useRealTimers());

describe("діапазон: перемикач і запити", () => {
  it("за замовчуванням 30 днів у зоні мережі, підзаголовок показує реальний діапазон", async () => {
    renderScreen();
    await vi.waitFor(() => expect(getNetworkAnalytics).toHaveBeenCalled());
    expect(lastQuery(getNetworkAnalytics)).toEqual({ from: "2026-09-25", to: T, compare: false });
    expect(lastQuery(getNetworkSeries)).toMatchObject({ from: "2026-09-25", to: T });
    expect(screen.getByText("25 вересня 2026 – 24 жовтня 2026 · 30 днів")).toBeInTheDocument();
    expect(screen.queryByText(/Жовтень 2026 · дані за місяць/)).toBeNull();
    expect(screen.queryByText(/Останні 8 тижнів/)).toBeNull();
    expect(screen.getByRole("radio", { name: "30 днів" })).toHaveAttribute("aria-checked", "true");
  });

  it("пресет міняє запити й підзаголовок, стан пишеться в URL", async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    renderScreen();
    await vi.waitFor(() => expect(getNetworkAnalytics).toHaveBeenCalledTimes(1));
    await user.click(screen.getByRole("radio", { name: "Поточний місяць" }));
    await vi.waitFor(() => expect(lastQuery(getNetworkAnalytics)).toMatchObject({ from: "2026-10-01", to: T }));
    expect(lastQuery(getNetworkSeries)).toMatchObject({ from: "2026-10-01", to: T });
    expect(window.location.search).toBe("?range=month");
    expect(await screen.findByText("1 жовтня 2026 – 24 жовтня 2026 · 24 дні")).toBeInTheDocument();
    await user.click(screen.getByRole("radio", { name: "Квартал" }));
    await vi.waitFor(() => expect(lastQuery(getNetworkAnalytics)).toMatchObject({ from: "2026-10-01", to: T }));
    await user.click(screen.getByRole("radio", { name: "Рік" }));
    await vi.waitFor(() => expect(lastQuery(getNetworkAnalytics)).toMatchObject({ from: "2026-01-01", to: T }));
    await user.click(screen.getByRole("radio", { name: "Минулий місяць" }));
    await vi.waitFor(() => expect(lastQuery(getNetworkAnalytics)).toMatchObject({ from: "2026-09-01", to: "2026-09-30" }));
    await user.click(screen.getByRole("radio", { name: "Сьогодні" }));
    await vi.waitFor(() => expect(lastQuery(getNetworkAnalytics)).toMatchObject({ from: T, to: T }));
  });

  it("початковий стан береться з URL (?range=7d&compare=1)", async () => {
    window.history.replaceState(null, "", "/beauty/analytics?range=7d&compare=1");
    renderScreen();
    await vi.waitFor(() => expect(getNetworkAnalytics).toHaveBeenCalled());
    expect(lastQuery(getNetworkAnalytics)).toEqual({ from: "2026-10-18", to: T, compare: true });
    expect(screen.getByRole("radio", { name: "7 днів" })).toHaveAttribute("aria-checked", "true");
    expect(screen.getByRole("switch", { name: "Порівняти з попереднім періодом" })).toHaveAttribute("aria-checked", "true");
  });

  it("клавіатура: radiogroup, стрілки переміщують вибір, roving tabindex", async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    renderScreen();
    const group = screen.getByRole("radiogroup", { name: "Період" });
    const active = within(group).getByRole("radio", { name: "30 днів" });
    expect(active).toHaveAttribute("tabindex", "0");
    expect(within(group).getByRole("radio", { name: "7 днів" })).toHaveAttribute("tabindex", "-1");
    active.focus();
    await user.keyboard("{ArrowRight}");
    expect(within(group).getByRole("radio", { name: "Поточний місяць" })).toHaveAttribute("aria-checked", "true");
    expect(within(group).getByRole("radio", { name: "Поточний місяць" })).toHaveFocus();
    await user.keyboard("{ArrowLeft}{ArrowLeft}");
    expect(within(group).getByRole("radio", { name: "7 днів" })).toHaveAttribute("aria-checked", "true");
    await user.keyboard("{End}");
    expect(within(group).getByRole("radio", { name: "Власний від–до" })).toHaveAttribute("aria-checked", "true");
    await user.keyboard("{Home}");
    expect(within(group).getByRole("radio", { name: "Сьогодні" })).toHaveAttribute("aria-checked", "true");
  });
});

describe("власний діапазон", () => {
  async function openCustom() {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    renderScreen();
    await user.click(screen.getByRole("radio", { name: "Власний від–до" }));
    return user;
  }

  it("валідний діапазон застосовується; запити йдуть з новими датами", async () => {
    await openCustom();
    fireEvent.change(screen.getByLabelText("Від"), { target: { value: "2026-10-01" } });
    fireEvent.change(screen.getByLabelText("До"), { target: { value: "2026-10-10" } });
    fireEvent.click(screen.getByRole("button", { name: "Застосувати" }));
    await vi.waitFor(() => expect(lastQuery(getNetworkAnalytics)).toMatchObject({ from: "2026-10-01", to: "2026-10-10" }));
    expect(window.location.search).toBe("?range=custom&from=2026-10-01&to=2026-10-10");
  });

  it("from > to: зрозуміла помилка, застосувати неможливо, запит не йде", async () => {
    await openCustom();
    const calls = getNetworkAnalytics.mock.calls.length;
    fireEvent.change(screen.getByLabelText("Від"), { target: { value: "2026-10-20" } });
    fireEvent.change(screen.getByLabelText("До"), { target: { value: "2026-10-10" } });
    expect(screen.getByRole("alert")).toHaveTextContent("Початок періоду не може бути пізніше за кінець");
    expect(screen.getByRole("button", { name: "Застосувати" })).toBeDisabled();
    expect(screen.getByLabelText("Від")).toHaveAttribute("aria-invalid", "true");
    expect(getNetworkAnalytics.mock.calls.length).toBe(calls);
  });

  it("більше 366 днів: помилка з кількістю днів", async () => {
    await openCustom();
    fireEvent.change(screen.getByLabelText("Від"), { target: { value: "2025-01-01" } });
    fireEvent.change(screen.getByLabelText("До"), { target: { value: "2026-10-10" } });
    expect(screen.getByRole("alert")).toHaveTextContent(/366/);
    expect(screen.getByRole("button", { name: "Застосувати" })).toBeDisabled();
  });
});

describe("вкладка «Мережа»", () => {
  it("графік з одного запиту series; підписи осей за гранулярністю", async () => {
    getNetworkSeries.mockImplementation(async () => series("week"));
    renderScreen();
    expect(await screen.findByRole("heading", { name: /Виручка мережі по тижнях/ })).toBeInTheDocument();
    expect(getNetworkSeries).toHaveBeenCalledTimes(1);
    expect(screen.getByRole("listitem", { name: /23 жовт\.?: 4,2 тис\. ₴/ })).toBeInTheDocument();
    expect(screen.getByText("23.10")).toBeInTheDocument();
  });

  it("гранулярність month: підписи місяців", async () => {
    getNetworkSeries.mockImplementation(async () => ({ ...series("month"), points: [{ from: "2026-09-01", to: "2026-09-30", appointments: 1, completed: 1, cancelled: 0, noShow: 0, revenue: 1000 }] }));
    renderScreen();
    expect(await screen.findByRole("heading", { name: /по місяцях/ })).toBeInTheDocument();
    expect(screen.getByRole("listitem", { name: "вересень 2026: 1 тис. ₴" })).toBeInTheDocument();
  });

  it("порівняння: дельти біля KPI, null/ділення на 0 -> «—», зростання скасувань - погано", async () => {
    getNetworkAnalytics.mockImplementation(async (q) => {
      const cmp = (q as { compare: boolean }).compare;
      return net({ previous: cmp ? { ...totals, fromDate: "2026-08-26", toDate: "2026-09-24", revenue: 10000, cancelled: 1, noShow: 0, newClients: 0 } : null });
    });
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    renderScreen();
    await screen.findByText("Виручка мережі");
    expect(screen.queryByText(/▲/)).toBeNull();
    await user.click(screen.getByRole("switch", { name: "Порівняти з попереднім періодом" }));
    await vi.waitFor(() => expect(lastQuery(getNetworkAnalytics)).toMatchObject({ compare: true }));
    expect(await screen.findByText("▲ +20% (+2 000 ₴)")).toBeInTheDocument();
    // нові клієнти 3 vs 0 -> % не визначений
    expect(screen.getByText("▲ — (+3)")).toBeInTheDocument();
    // скасувань було 1, стало 2: зростання - погано
    expect(screen.getByText("▲ +100% (+1)").closest("[data-tone]")).toHaveAttribute("data-tone", "bad");
    // виручка: зростання - добре, і не лише колір: є стрілка й текст для зчитувача
    expect(screen.getByText("▲ +20% (+2 000 ₴)").closest("[data-tone]")).toHaveAttribute("data-tone", "good");
    expect(screen.getByText(/Більше на 20%/)).toBeInTheDocument();
    expect(screen.getByText(/Порівняння з 26 серпня 2026 – 24 вересня 2026/)).toBeInTheDocument();
  });

  it("експорт CSV: кнопка вимкнена, поки вантажиться; після - завантажує файл з діапазоном у назві", async () => {
    const create = vi.fn(() => "blob:x");
    const revoke = vi.fn();
    Object.assign(URL, { createObjectURL: create, revokeObjectURL: revoke });
    const click = vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(() => {});
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    let release: (s: NetworkSeries) => void = () => {};
    getNetworkSeries.mockImplementationOnce(() => new Promise((r) => (release = r)));
    renderScreen();
    expect(await screen.findByRole("button", { name: "Експорт CSV" })).toBeDisabled();
    release(series());
    await vi.waitFor(() => expect(screen.getByRole("button", { name: "Експорт CSV" })).toBeEnabled());
    await user.click(screen.getByRole("button", { name: "Експорт CSV" }));
    expect(create).toHaveBeenCalledTimes(1);
    const blob = create.mock.calls[0] as unknown as [Blob];
    expect((blob[0] as Blob).type).toContain("text/csv");
    expect(click).toHaveBeenCalled();
    expect(revoke).toHaveBeenCalledWith("blob:x");
    click.mockRestore();
  });

  it("помилка series не ламає KPI", async () => {
    getNetworkSeries.mockRejectedValue(new Error("boom"));
    renderScreen();
    expect(await screen.findByText("Виручка мережі")).toBeInTheDocument();
    expect(await screen.findByRole("alert")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Експорт CSV" })).toBeDisabled();
  });
});

describe("F6 + §19.4: вкладка «Заклади»", () => {
  it("початковий заклад = перший реальний; breakdown, а не /appointments; колонка «Завантаження» (null -> «—»)", async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    renderScreen();
    await user.click(screen.getByRole("button", { name: "Заклади" }));
    await vi.waitFor(() => expect(getLocationBreakdown).toHaveBeenCalledWith("11111111-aaaa", expect.objectContaining({ from: "2026-09-25", to: T })));
    expect(getLocationBreakdown).not.toHaveBeenCalledWith("c", expect.anything());
    expect(await screen.findByRole("button", { name: "Центр" })).toHaveAttribute("aria-pressed", "true");
    const table = await screen.findByRole("table");
    const rows = within(table).getAllByRole("row");
    expect(within(rows[1]).getByText("50%")).toBeInTheDocument();
    expect(within(rows[2]).getByText("—")).toBeInTheDocument();
    expect(within(table).getByRole("columnheader", { name: "Завантаження" })).toBeInTheDocument();
  });

  it("клік по іншому закладу вантажить його", async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    renderScreen();
    await user.click(screen.getByRole("button", { name: "Заклади" }));
    await user.click(await screen.findByRole("button", { name: "Поділ" }));
    await vi.waitFor(() => expect(getLocationBreakdown).toHaveBeenLastCalledWith("22222222-bbbb", expect.anything()));
  });

  it("немає жодного закладу - пояснення, а не запит breakdown", async () => {
    getLocationsAnalytics.mockResolvedValue([]);
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    renderScreen();
    await user.click(screen.getByRole("button", { name: "Заклади" }));
    expect(await screen.findByText(/Закладів ще немає/)).toBeInTheDocument();
    expect(getLocationBreakdown).not.toHaveBeenCalled();
  });

  it("KPI «Завантаження» і порівняння у п.п.", async () => {
    getLocationsAnalytics.mockImplementation(async (q) => [
      locRow("11111111-aaaa", "Центр", { previous: (q as { compare: boolean }).compare ? { ...metrics, load: 0.4, revenue: 0 } : null }),
    ]);
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    renderScreen();
    await user.click(screen.getByRole("button", { name: "Заклади" }));
    await screen.findByText("Завантаження", { selector: "div" });
    await user.click(screen.getByRole("switch", { name: "Порівняти з попереднім періодом" }));
    expect(await screen.findByText("▲ +10 п.п.")).toBeInTheDocument();
    // виручка: попередня 0 -> «—»
    expect(screen.getByText("▲ — (+4 000 ₴)")).toBeInTheDocument();
  });
});

describe("вкладка «Акції»", () => {
  it("рядки з використаннями; експорт захищений від формул", async () => {
    const create = vi.fn(() => "blob:p");
    Object.assign(URL, { createObjectURL: create, revokeObjectURL: vi.fn() });
    const click = vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(() => {});
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    renderScreen();
    await user.click(screen.getByRole("button", { name: "Акції" }));
    expect(await screen.findByRole("link", { name: "=Осінь" })).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Експорт CSV" }));
    const blob = (create.mock.calls[0] as unknown as [Blob])[0];
    const text = await new Promise<string>((res) => {
      const fr = new FileReader();
      fr.onload = () => res(String(fr.result));
      fr.readAsText(blob);
    });
    expect(text).toContain("'=Осінь");
    click.mockRestore();
  });
});
