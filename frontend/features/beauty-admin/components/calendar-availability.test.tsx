import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { Appointment, CalendarWeek, DayAvailability } from "../types";

const auth = { user: { id: "u", email: "e", fullName: "n", role: "owner", specialistId: null as string | null } };
vi.mock("next/navigation", () => ({ useSearchParams: () => new URLSearchParams(), useRouter: () => ({ replace: vi.fn() }) }));
vi.mock("@/features/beauty-auth/components/auth-provider", () => ({ useAuth: () => auth }));

const getCalendarWeek = vi.fn<(id: string, includeCancelled?: boolean, locationId?: string) => Promise<CalendarWeek>>();
vi.mock("../api", () => ({
  beautyApi: {
    getCalendarWeek: (id: string, inc?: boolean, loc?: string) => (loc === undefined ? getCalendarWeek(id, inc) : getCalendarWeek(id, inc, loc)),
    getAbsences: () => Promise.resolve([]),
    moveAppointment: () => Promise.resolve(),
  },
}));

import { CalendarScreen } from "./calendar-screen";

/** F1: календар показує доступність (графік майстра ∩ години закладу ∩ вихідні), а не лише вихідні й записи. */

const iv = (from: number, to: number) => ({ from, to });
const av = (i: number, over: Partial<DayAvailability> = {}): DayAvailability => ({
  date: `2026-10-${String(5 + i).padStart(2, "0")}`,
  state: "open",
  master: [iv(540, 1080)],
  location: [iv(600, 1020)],
  free: [iv(600, 1020)],
  masterOff: [],
  hoursOff: [iv(0, 600), iv(1020, 1440)],
  ...over,
});
const availability = [
  av(0),
  av(1, { state: "master_off", master: [], free: [], hoursOff: [] }),
  av(2, { state: "location_hours_off", location: [], free: [], hoursOff: [iv(0, 1440)] }),
  av(3, { master: [iv(660, 960)], masterOff: [iv(600, 660), iv(960, 1020)], free: [iv(660, 960)] }),
  av(4),
  av(5, { state: "location_closed", free: [] }),
  av(6, { state: "location_closed", free: [] }),
];

const base: Appointment = {
  id: "x", locationId: "c", specialistId: "m", serviceId: "s", clientId: "oc", clientName: "Клієнт", serviceName: "Манікюр",
  startsAt: "2026-10-05T11:00", durationMinutes: 60, status: "confirmed", source: "admin", kind: "visit", priceFinal: 700, promotionId: null,
};
const inside: Appointment = { ...base, id: "i1", clientName: "Всередині", startsAt: "2026-10-05T11:00" };
const early: Appointment = { ...base, id: "e1", clientName: "Зарано", startsAt: "2026-10-05T09:00" };

const week = (over: Partial<CalendarWeek> = {}): CalendarWeek => ({
  weekLabel: "Тиждень 5 – 11 жовтня",
  weekStart: "2026-10-05",
  days: ["Пн 5", "Вт 6", "Ср 7", "Чт 8", "Пт 9", "Сб 10", "Нд 11"],
  masters: [{ id: "m", name: "Марина", locationName: "Центр", isActive: true, toMoveCount: 0 }],
  specialistId: "m",
  appointments: [inside, early],
  availability,
  gridStartHour: 9,
  gridEndHour: 18,
  ...over,
});

function renderScreen() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <CalendarScreen />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  auth.user.role = "owner";
  getCalendarWeek.mockReset().mockImplementation(async () => week());
});

describe("календар: доступність майстра", () => {
  it("весь день: «Не працює» (поза графіком) і «Заклад не працює» (поза годинами) - різні стани", async () => {
    renderScreen();
    await screen.findByRole("button", { name: /Всередині/ });
    expect(screen.getByTestId("master-off")).toHaveTextContent("Не працює");
    expect(screen.getByTestId("hours-day-off")).toHaveTextContent("Заклад не працює");
    expect(screen.getByTestId("master-off")).toHaveClass("pointer-events-none");
    expect(screen.getByRole("group", { name: /Вт 6.*Майстер не працює, слоти недоступні/ })).toBeInTheDocument();
    expect(screen.getByRole("group", { name: /Ср 7.*Заклад не працює, слоти недоступні/ })).toBeInTheDocument();
    expect(screen.getByRole("group", { name: /Пн 5.*Доступно: 10:00–17:00/ })).toBeInTheDocument();
  });

  it("частково: сегменти «Заклад зачинено» (поза годинами) і «Не працює» (поза графіком)", async () => {
    renderScreen();
    await screen.findByRole("button", { name: /Всередині/ });
    const hours = screen.getAllByTestId("shade-hours");
    expect(hours.length).toBeGreaterThanOrEqual(4);
    expect(hours.some((h) => h.textContent === "Заклад зачинено")).toBe(true);
    expect(screen.getAllByTestId("shade-master")[0]).toHaveTextContent("Не працює");
  });

  it("легенда показує усі стани доступності", async () => {
    renderScreen();
    await screen.findByRole("button", { name: /Всередині/ });
    const legend = screen.getByRole("list", { name: "Легенда" });
    for (const t of [/Вихідний закладу/, /Відсутність майстра/, /Не працює \(поза графіком/, /Заклад зачинено \(поза годинами/, /Запис поза доступним часом/]) {
      expect(within(legend).getByText(t)).toBeInTheDocument();
    }
  });

  it("запис у недоступній клітинці лишається видимим із попередженням; в доступній - без", async () => {
    renderScreen();
    const warn = await screen.findByRole("button", { name: /Зарано.*поза доступним часом/ });
    expect(warn).toHaveAttribute("data-out-of-hours", "true");
    expect(screen.getByRole("button", { name: /Всередині/ })).not.toHaveAttribute("data-out-of-hours");
    await userEvent.setup().click(warn);
    expect(screen.getByTestId("out-of-hours-note")).toBeInTheDocument();
  });

  it("у недоступні клітинки запис не створюється й не переноситься", async () => {
    const user = userEvent.setup();
    renderScreen();
    await screen.findByRole("button", { name: /Всередині/ });
    // жодних елементів створення запису в колонках: лише блоки записів
    expect(within(screen.getByRole("group", { name: /Вт 6/ })).queryAllByRole("button")).toHaveLength(0);

    await user.click(screen.getByRole("button", { name: /Всередині/ }));
    await user.click(screen.getByRole("button", { name: "Перенести запис" }));
    const input = screen.getByLabelText("Новий час візиту");
    fireEvent.change(input, { target: { value: "2026-10-06T11:00" } }); // вт - майстер не працює
    expect(screen.getByTestId("move-problem")).toHaveTextContent("поза графіком майстра або годинами закладу");
    expect(screen.getByRole("button", { name: "Зберегти час" })).toBeDisabled();
    fireEvent.change(input, { target: { value: "2026-10-09T11:00" } }); // пт 11:00-12:00 всередині 10-17
    expect(screen.queryByTestId("move-problem")).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Зберегти час" })).toBeEnabled();
    fireEvent.change(input, { target: { value: "2026-10-10T11:00" } }); // сб - заклад закритий
    expect(screen.getByTestId("move-problem")).toBeInTheDocument();
  });

  it("сітка підлаштована під діапазон: 10-15 -> немає міток 9:00 і 15:00", async () => {
    getCalendarWeek.mockImplementation(async () => week({ gridStartHour: 10, gridEndHour: 15, appointments: [inside] }));
    renderScreen();
    await screen.findByRole("button", { name: /Всередині/ });
    expect(screen.getByText("10:00")).toBeInTheDocument();
    expect(screen.getByText("14:00")).toBeInTheDocument();
    expect(screen.queryByText("9:00")).not.toBeInTheDocument();
    expect(screen.queryByText("15:00")).not.toBeInTheDocument();
  });

  it("сітка розширюється, щоб запис поза діапазоном не вийшов за межі", async () => {
    getCalendarWeek.mockImplementation(async () => week({ gridStartHour: 10, gridEndHour: 15, appointments: [early] }));
    renderScreen();
    await screen.findByRole("button", { name: /Зарано/ });
    expect(screen.getByText("9:00")).toBeInTheDocument();
  });

  it("завантаження тижня рахується від доступних годин, а не від фіксованих 66", async () => {
    // доступно: пн 7 год + чт 5 год + пт 7 год = 19 год; запис 1 год -> 5%
    getCalendarWeek.mockImplementation(async () => week({ appointments: [inside] }));
    renderScreen();
    await screen.findByRole("button", { name: /Всередині/ });
    expect(screen.getByText("Завантаження").nextSibling).toHaveTextContent("5%");
  });

  it("вибір закладу: чипи, лічильник записів в іншому закладі, клік передає locationId у запит", async () => {
    const user = userEvent.setup();
    getCalendarWeek.mockImplementation(async (_id, _inc, loc) =>
      week({ locations: [{ id: "c", name: "Центр" }, { id: "k", name: "Печерськ" }], locationId: loc ?? "c", otherLocationsCount: 2 }),
    );
    renderScreen();
    const group = await screen.findByRole("group", { name: "Заклад" });
    expect(within(group).getByText(/Записів в іншому закладі цього тижня: 2/)).toBeInTheDocument();
    expect(within(group).getByRole("button", { name: "Центр" })).toHaveAttribute("aria-pressed", "true");
    await user.click(within(group).getByRole("button", { name: "Печерськ" }));
    await vi.waitFor(() => expect(getCalendarWeek).toHaveBeenLastCalledWith("", false, "k"));
  });

  it("один заклад - без чипів вибору закладу", async () => {
    getCalendarWeek.mockImplementation(async () => week({ locations: [{ id: "c", name: "Центр" }], locationId: "c" }));
    renderScreen();
    await screen.findByRole("button", { name: /Всередині/ });
    expect(screen.queryByRole("group", { name: "Заклад" })).not.toBeInTheDocument();
  });

  it("збій завантаження вихідних - ненавʼязливе попередження", async () => {
    getCalendarWeek.mockImplementation(async () => week({ availabilityWarning: true }));
    renderScreen();
    expect(await screen.findByTestId("availability-warning")).toHaveTextContent("Не вдалося завантажити вихідні");
  });

  it("без попередження, коли все завантажилось", async () => {
    renderScreen();
    await screen.findByRole("button", { name: /Всередині/ });
    expect(screen.queryByTestId("availability-warning")).not.toBeInTheDocument();
  });
});
