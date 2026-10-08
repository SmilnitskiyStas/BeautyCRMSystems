import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { Appointment, CalendarWeek } from "../types";

const auth = { user: { id: "u", email: "e", fullName: "n", role: "owner", specialistId: null as string | null } };
vi.mock("next/navigation", () => ({ useSearchParams: () => new URLSearchParams(), useRouter: () => ({ replace: vi.fn() }) }));
vi.mock("@/features/beauty-auth/components/auth-provider", () => ({ useAuth: () => auth }));

const getCalendarWeek = vi.fn<(id: string, includeCancelled?: boolean) => Promise<CalendarWeek>>();
const cancelAppointment = vi.fn<(id: string, reason?: string) => Promise<{ refundAmount: number }>>();
vi.mock("../api", () => ({
  beautyApi: {
    getCalendarWeek: (id: string, inc?: boolean) => getCalendarWeek(id, inc),
    getAbsences: () => Promise.resolve([]),
    cancelAppointment: (id: string, reason?: string) => cancelAppointment(id, reason),
  },
}));

import { CalendarScreen } from "./calendar-screen";

const live: Appointment = {
  id: "a1", locationId: "c", specialistId: "m", serviceId: "s", clientId: "oc", clientName: "Олена К.", serviceName: "Манікюр",
  startsAt: "2026-10-06T10:00", durationMinutes: 60, status: "confirmed", source: "admin", kind: "visit", priceFinal: 700, promotionId: null,
};
const gone: Appointment = {
  ...live, id: "a2", clientName: "Ірина Л.", startsAt: "2026-10-07T12:00", status: "cancelled",
  cancelledAt: "2026-10-05T09:00", cancelledBy: { type: "client" }, cancelReason: "Захворіла",
};

const week = (over: Partial<CalendarWeek> = {}): CalendarWeek => ({
  weekLabel: "Тиждень 5 – 11 жовтня", weekStart: "2026-10-05", days: ["Пн 5", "Вт 6", "Ср 7", "Чт 8", "Пт 9", "Сб 10", "Нд 11"],
  masters: [
    { id: "m", name: "Марина", locationName: "Центр", isActive: true, toMoveCount: 0 },
    { id: "ng", name: "Наталя", locationName: "Печерськ", isActive: false, toMoveCount: 2 },
  ],
  specialistId: "m", appointments: [live], ...over,
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
  getCalendarWeek.mockReset().mockImplementation(async (_id, inc) => week({ appointments: inc ? [live, gone] : [live] }));
  cancelAppointment.mockReset().mockResolvedValue({ refundAmount: 0 });
});

describe("календар (розділ 16 контракту)", () => {
  it("за замовчуванням не показує скасовані й не просить їх у API", async () => {
    renderScreen();
    expect(await screen.findByRole("button", { name: /Олена К\./ })).toBeInTheDocument();
    expect(screen.queryByText("Ірина Л.")).not.toBeInTheDocument();
    expect(getCalendarWeek).toHaveBeenCalledWith("", false);
  });

  it("перемикач Показати скасовані запитує includeCancelled і підписує, хто скасував", async () => {
    const user = userEvent.setup();
    renderScreen();
    await screen.findByRole("button", { name: /Олена К\./ });
    await user.click(screen.getByRole("checkbox", { name: "Показати скасовані" }));
    const block = await screen.findByRole("button", { name: /Ірина Л\..*скасовано.*Скасував: клієнт/ });
    expect(block).toHaveAttribute("data-cancelled", "true");
    expect(block).toHaveTextContent("Скасував: клієнт");
    expect(getCalendarWeek).toHaveBeenLastCalledWith("", true);
    await user.click(block);
    expect(screen.getByTestId("cancel-info")).toHaveTextContent("Причина: Захворіла");
    expect(screen.queryByRole("button", { name: "Скасувати запис" })).not.toBeInTheDocument();
  });

  it("не показує причину, якщо її немає, і не лічить скасовані у статистиці", async () => {
    const user = userEvent.setup();
    getCalendarWeek.mockImplementation(async () =>
      week({ appointments: [live, { ...gone, cancelReason: undefined, cancelledBy: { type: "system" } }] }),
    );
    renderScreen();
    await user.click(await screen.findByRole("checkbox", { name: "Показати скасовані" }));
    await user.click(await screen.findByRole("button", { name: /Ірина Л\./ }));
    expect(screen.getByTestId("cancel-info")).toHaveTextContent("Скасував: система");
    expect(screen.getByTestId("cancel-info")).not.toHaveTextContent("Причина");
    expect(screen.getByText("Записів").nextSibling).toHaveTextContent("1");
  });

  it("неактивний майстер у селекторі з позначкою й попередженням про перенесення", async () => {
    const user = userEvent.setup();
    getCalendarWeek.mockImplementation(async (id) => week(id === "ng" ? { specialistId: "ng", appointments: [] } : {}));
    renderScreen();
    const chip = await screen.findByRole("button", { name: /Наталя.*неактивний/ });
    expect(screen.queryByText(/їх потрібно перенести/)).not.toBeInTheDocument();
    await user.click(chip);
    expect(await screen.findByText(/Є записи, їх потрібно перенести/)).toBeInTheDocument();
  });

  it("скасування передає необовязкову причину", async () => {
    const user = userEvent.setup();
    renderScreen();
    await user.click(await screen.findByRole("button", { name: /Олена К\./ }));
    await user.click(screen.getByRole("button", { name: "Скасувати запис" }));
    await user.type(screen.getByLabelText(/Причина/), "Майстер захворів");
    await user.click(screen.getByRole("button", { name: "Так, скасувати" }));
    await vi.waitFor(() => expect(cancelAppointment).toHaveBeenCalledWith("a1", "Майстер захворів"));
  });

  it("скасування без причини не надсилає reason", async () => {
    const user = userEvent.setup();
    renderScreen();
    await user.click(await screen.findByRole("button", { name: /Олена К\./ }));
    await user.click(screen.getByRole("button", { name: "Скасувати запис" }));
    await user.click(screen.getByRole("button", { name: "Так, скасувати" }));
    await vi.waitFor(() => expect(cancelAppointment).toHaveBeenCalledWith("a1", undefined));
  });

  it("поле причини обмежене 300 символами", async () => {
    const user = userEvent.setup();
    renderScreen();
    await user.click(await screen.findByRole("button", { name: /Олена К\./ }));
    await user.click(screen.getByRole("button", { name: "Скасувати запис" }));
    expect(screen.getByLabelText(/Причина/)).toHaveAttribute("maxlength", "300");
  });
});
