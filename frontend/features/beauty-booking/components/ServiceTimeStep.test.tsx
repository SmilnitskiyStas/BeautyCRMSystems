import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { Location } from "../types";

const flow = {
  location: null as Location | null,
  specialist: { id: "m", name: "Марина", role: "" },
  service: { id: "mn", name: "Манікюр", durationMinutes: 60, priceOriginal: 700, priceFinal: 700 },
  slot: null,
  setService: vi.fn(),
  setSlot: vi.fn(),
};
vi.mock("../hooks/useBookingFlow", () => ({ useBookingFlow: () => flow }));

const useSlots = vi.fn((q: { date: string; enabled?: boolean }) => ({
  data: q.enabled === false ? undefined : [],
  isLoading: false,
  isError: false,
  error: null,
}));
vi.mock("../hooks/queries", () => ({
  useServices: () => ({ data: [flow.service], isLoading: false, isError: false, error: null }),
  useSlots: (q: { date: string; enabled?: boolean }) => useSlots(q),
}));

import { ServiceTimeStep } from "./ServiceTimeStep";

// Субота 10 жовтня 2026, 12:00 за Києвом.
const NOW = new Date("2026-10-10T09:00:00Z");

const location = (over: Partial<Location> = {}): Location => ({
  id: "c",
  name: "Центр",
  timezone: "Europe/Kyiv",
  closedWeekdays: ["sun"],
  closures: [{ dateFrom: "2026-10-13", dateTo: "2026-10-14" }],
  ...over,
});

beforeEach(() => {
  vi.useFakeTimers({ toFake: ["Date"], now: NOW });
  useSlots.mockClear();
  flow.setSlot.mockClear();
  flow.location = location();
});
afterEach(() => vi.useRealTimers());

const options = () => screen.getAllByTestId("date-option");

describe("вибір дати: закриті дні закладу (§17)", () => {
  it("щотижневі вихідні й закриття за датою відключені з підказкою; решта доступні", () => {
    render(<ServiceTimeStep />);
    const o = options();
    expect(o).toHaveLength(14);
    // 10 (сб) - працює, 11 (нд) - вихідний, 12 (пн) - працює, 13-14 - закриття, 18 (нд) - вихідний
    const closed = o.filter((b) => b.getAttribute("aria-disabled") === "true");
    expect(closed).toHaveLength(4);
    expect(o[1]).toHaveAttribute("aria-disabled", "true");
    expect(o[3]).toHaveAttribute("aria-disabled", "true");
    expect(o[4]).toHaveAttribute("aria-disabled", "true");
    expect(o[8]).toHaveAttribute("aria-disabled", "true");
    expect(o[0]).not.toHaveAttribute("aria-disabled");
    expect(o[2]).not.toHaveAttribute("aria-disabled");
    closed.forEach((b) => expect(b).toHaveAccessibleName(/Заклад не працює в цей день/));
    closed.forEach((b) => expect(b).toHaveAttribute("title", "Заклад не працює в цей день"));
  });

  it("клік по закритому дню показує підказку й не змінює дату та слоти", async () => {
    const user = userEvent.setup();
    render(<ServiceTimeStep />);
    expect(useSlots).toHaveBeenLastCalledWith(expect.objectContaining({ date: "2026-10-10", enabled: true }));
    await user.click(options()[1]);
    expect(screen.getByText(/Заклад не працює в цей день\. Оберіть інший день/)).toBeInTheDocument();
    expect(flow.setSlot).not.toHaveBeenCalled();
    expect(useSlots).toHaveBeenLastCalledWith(expect.objectContaining({ date: "2026-10-10" }));
    expect(options()[0]).toHaveAttribute("aria-pressed", "true");
  });

  it("клік по робочому дню обирає його", async () => {
    const user = userEvent.setup();
    render(<ServiceTimeStep />);
    await user.click(options()[2]);
    expect(useSlots).toHaveBeenLastCalledWith(expect.objectContaining({ date: "2026-10-12" }));
    expect(flow.setSlot).toHaveBeenCalledWith(null);
  });

  it("якщо сьогодні закрито, початкова дата - перший робочий день", () => {
    flow.location = location({ closedWeekdays: ["sat"], closures: [] });
    render(<ServiceTimeStep />);
    expect(useSlots).toHaveBeenLastCalledWith(expect.objectContaining({ date: "2026-10-11" }));
    expect(options()[1]).toHaveAttribute("aria-pressed", "true");
  });

  it("причина закриття не показується (публічний API її не віддає)", () => {
    flow.location = location({ closures: [{ dateFrom: "2026-10-13", dateTo: "2026-10-14", reason: "Ремонт" } as never] });
    render(<ServiceTimeStep />);
    expect(screen.queryByText(/Ремонт/)).not.toBeInTheDocument();
  });

  it("заклад без вихідних: жодна дата не відключена", () => {
    flow.location = location({ closedWeekdays: [], closures: [] });
    render(<ServiceTimeStep />);
    options().forEach((b) => expect(b).not.toHaveAttribute("aria-disabled"));
  });

  it("усі дні закриті: слоти не запитуються, повідомлення про недоступність", () => {
    flow.location = location({ closedWeekdays: ["mon", "tue", "wed", "thu", "fri", "sat", "sun"], closures: [] });
    render(<ServiceTimeStep />);
    expect(useSlots).toHaveBeenLastCalledWith(expect.objectContaining({ enabled: false }));
    expect(screen.getByText(/У найближчі дні заклад не працює/)).toBeInTheDocument();
  });
});
