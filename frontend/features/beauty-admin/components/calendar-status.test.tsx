import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { BeautyApiError } from "@/features/beauty-auth/errors";
import type { Appointment, CalendarWeek, DayAvailability, PatchableStatus } from "../types";

const auth = { user: { id: "u", email: "e", fullName: "n", role: "owner", specialistId: null as string | null } };
vi.mock("next/navigation", () => ({ useSearchParams: () => new URLSearchParams(), useRouter: () => ({ replace: vi.fn() }) }));
vi.mock("@/features/beauty-auth/components/auth-provider", () => ({ useAuth: () => auth }));

const getCalendarWeek = vi.fn<(id: string, includeCancelled?: boolean, locationId?: string) => Promise<CalendarWeek>>();
const setAppointmentStatus = vi.fn<(id: string, status: PatchableStatus) => Promise<void>>();
const moveAppointment = vi.fn<(id: string, startsAt: string) => Promise<void>>();
const cancelAppointment = vi.fn<(id: string, reason?: string) => Promise<{ refundAmount: number }>>();
vi.mock("../api", () => ({
  beautyApi: {
    getCalendarWeek: (id: string, inc?: boolean) => getCalendarWeek(id, inc),
    getAbsences: () => Promise.resolve([]),
    getLocations: () => Promise.resolve([{ id: "c", name: "Центр", timezone: "Europe/Kyiv", isActive: true, closedWeekdays: [], workingHours: null, address: null, phone: null }]),
    moveAppointment: (id: string, startsAt: string) => moveAppointment(id, startsAt),
    setAppointmentStatus: (id: string, status: PatchableStatus) => setAppointmentStatus(id, status),
    cancelAppointment: (id: string, reason?: string) => cancelAppointment(id, reason),
  },
}));

import { CalendarScreen, statusErrorText } from "./calendar-screen";

const iv = (from: number, to: number) => ({ from, to });
const openDay = (date: string): DayAvailability => ({
  date,
  state: "open",
  master: [iv(540, 1080)],
  location: [iv(540, 1200)],
  free: [iv(540, 1080)],
  masterOff: [],
  hoursOff: [iv(0, 540), iv(1200, 1440)],
});

const base: Appointment = {
  id: "x", locationId: "c", specialistId: "m", serviceId: "s", clientId: "oc", clientName: "Олена", serviceName: "Манікюр",
  startsAt: "2026-10-05T11:00", durationMinutes: 60, status: "pending", source: "admin", kind: "visit", priceFinal: 700, promotionId: null,
};

let current: Appointment[] = [];
const week = (weekStart: string): CalendarWeek => ({
  weekLabel: "Тиждень",
  weekStart,
  days: ["Пн", "Вт", "Ср", "Чт", "Пт", "Сб", "Нд"],
  masters: [{ id: "m", name: "Марина", locationName: "Центр", isActive: true, toMoveCount: 0 }],
  specialistId: "m",
  appointments: current,
  availability: Array.from({ length: 7 }, (_, i) => openDay(`${weekStart}-${i}`)),
  gridStartHour: 9,
  gridEndHour: 20,
});

let weekStart = "2026-10-05";
function renderScreen() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return { qc, ...render(<QueryClientProvider client={qc}><CalendarScreen /></QueryClientProvider>) };
}

async function select(name: RegExp = /Олена/) {
  const user = userEvent.setup();
  await user.click(await screen.findByRole("button", { name }));
  return user;
}

beforeEach(() => {
  auth.user.role = "owner";
  auth.user.specialistId = null;
  weekStart = "2026-10-05";
  current = [{ ...base }];
  getCalendarWeek.mockReset().mockImplementation(async () => week(weekStart));
  setAppointmentStatus.mockReset().mockResolvedValue(undefined);
  moveAppointment.mockReset().mockResolvedValue(undefined);
  cancelAppointment.mockReset().mockResolvedValue({ refundAmount: 350 });
});

describe("запис у календарі: зміна статусу (TASK-775)", () => {
  it("pending: доступні «Підтвердити запис», «Завершити візит», «Не прийшов», «Перенести», «Скасувати»", async () => {
    renderScreen();
    await select();
    expect(screen.getByTestId("appt-status")).toHaveTextContent("Очікує підтвердження");
    for (const n of ["Підтвердити запис", "Завершити візит", "Не прийшов", "Перенести запис", "Скасувати запис"]) {
      expect(screen.getByRole("button", { name: n })).toBeInTheDocument();
    }
  });

  it("confirmed: «Підтвердити» немає; «Підтвердити запис» шле confirmed без діалогу і показує повідомлення", async () => {
    renderScreen();
    const user = await select();
    await user.click(screen.getByRole("button", { name: "Підтвердити запис" }));
    expect(setAppointmentStatus).toHaveBeenCalledWith("x", "confirmed");
    expect(await screen.findByText("Запис підтверджено.")).toBeInTheDocument();
  });

  it("confirmed-запис не має кнопки «Підтвердити запис»", async () => {
    current = [{ ...base, status: "confirmed" }];
    renderScreen();
    await select();
    expect(screen.queryByRole("button", { name: "Підтвердити запис" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Завершити візит" })).toBeInTheDocument();
  });

  it("«Завершити візит»: діалог пояснює фіксацію ціни й собівартості, фокус у діалозі, без підтвердження запит не йде", async () => {
    current = [{ ...base, status: "confirmed" }];
    renderScreen();
    const user = await select();
    await user.click(screen.getByRole("button", { name: "Завершити візит" }));
    const dialog = screen.getByRole("group", { name: "Завершити візит?" });
    expect(dialog).toHaveTextContent("фіксується ціна візиту (700 ₴) і собівартість");
    expect(dialog).toHaveFocus();
    expect(screen.queryByTestId("complete-future-warning")).not.toBeInTheDocument(); // візит у минулому
    expect(setAppointmentStatus).not.toHaveBeenCalled();
    // «Назад» повертає кнопки, нічого не відправляє
    await user.click(screen.getByRole("button", { name: "Назад" }));
    expect(screen.queryByTestId("status-confirm")).not.toBeInTheDocument();
    expect(setAppointmentStatus).not.toHaveBeenCalled();
    // підтвердження
    await user.click(screen.getByRole("button", { name: "Завершити візит" }));
    await user.click(screen.getByRole("button", { name: "Так, завершити" }));
    expect(setAppointmentStatus).toHaveBeenCalledWith("x", "completed");
    expect(await screen.findByText(/Візит завершено\. Ціну й собівартість зафіксовано у звіті\./)).toBeInTheDocument();
  });

  it("після завершення календар перечитується: статус «Завершено», перенос і скасування зникають", async () => {
    current = [{ ...base, status: "confirmed" }];
    renderScreen();
    const user = await select();
    setAppointmentStatus.mockImplementation(async () => {
      current = [{ ...base, status: "completed" }];
    });
    await user.click(screen.getByRole("button", { name: "Завершити візит" }));
    await user.click(screen.getByRole("button", { name: "Так, завершити" }));
    await waitFor(() => expect(screen.getByTestId("appt-status")).toHaveTextContent("Завершено"));
    for (const n of ["Завершити візит", "Не прийшов", "Перенести запис", "Скасувати запис", "Підтвердити запис"]) {
      expect(screen.queryByRole("button", { name: n })).not.toBeInTheDocument();
    }
  });

  it("візит у майбутньому: завершити можна, але з попередженням", async () => {
    weekStart = "2099-10-05";
    current = [{ ...base, status: "confirmed", startsAt: "2099-10-05T11:00" }];
    renderScreen();
    const user = await select();
    await user.click(screen.getByRole("button", { name: "Завершити візит" }));
    expect(screen.getByTestId("complete-future-warning")).toHaveTextContent("ще не настав");
    expect(screen.getByRole("button", { name: "Так, завершити" })).toBeEnabled();
  });

  it("«Не прийшов»: потрібне підтвердження, далі PATCH no_show і повідомлення", async () => {
    renderScreen();
    const user = await select();
    await user.click(screen.getByRole("button", { name: "Не прийшов" }));
    expect(screen.getByRole("group", { name: "Позначити «Не прийшов»?" })).toHaveFocus();
    expect(setAppointmentStatus).not.toHaveBeenCalled();
    await user.click(screen.getByRole("button", { name: "Так, не прийшов" }));
    expect(setAppointmentStatus).toHaveBeenCalledWith("x", "no_show");
    expect(await screen.findByText("Запис позначено як «Не прийшов».")).toBeInTheDocument();
  });

  it("409 appointment_closed: дружній текст українською без серверного, дані перечитуються", async () => {
    current = [{ ...base, status: "confirmed" }];
    renderScreen();
    const user = await select();
    setAppointmentStatus.mockRejectedValue(new BeautyApiError(409, "appointment_closed", "Appointment with status 'completed' cannot be changed."));
    const callsBefore = getCalendarWeek.mock.calls.length;
    await user.click(screen.getByRole("button", { name: "Завершити візит" }));
    await user.click(screen.getByRole("button", { name: "Так, завершити" }));
    const alert = await screen.findByTestId("status-error");
    expect(alert).toHaveAttribute("role", "alert");
    expect(alert).toHaveTextContent("Запис уже закрито");
    expect(alert.textContent).not.toMatch(/Appointment|cannot be changed/);
    await waitFor(() => expect(getCalendarWeek.mock.calls.length).toBeGreaterThan(callsBefore));
    expect(screen.queryByTestId("status-confirm")).not.toBeInTheDocument();
  });

  it("інші помилки - тексти за кодом, мережа - окреме повідомлення", () => {
    expect(statusErrorText(new BeautyApiError(404, "appointment_not_found", "Appointment not found."))).toBe("Запис не знайдено.");
    expect(statusErrorText(new BeautyApiError(500, "boom", "stack trace"))).toBe("Помилка сервера. Спробуйте пізніше.");
    expect(statusErrorText(new Error("x"))).toBe("Не вдалося змінити статус запису. Спробуйте ще раз.");
  });

  it("скасований і «не прийшов» запис: дій зі статусом немає", async () => {
    current = [{ ...base, status: "no_show" }];
    renderScreen();
    await select();
    expect(screen.getByTestId("appt-status")).toHaveTextContent("Не прийшов");
    expect(screen.queryByRole("button", { name: "Завершити візит" })).not.toBeInTheDocument();
  });

  it("specialist: власний запис - дії є, чужий - немає (backend віддав би 404)", async () => {
    auth.user.role = "specialist";
    auth.user.specialistId = "m";
    renderScreen();
    await select();
    expect(screen.getByRole("button", { name: "Завершити візит" })).toBeInTheDocument();
  });

  it("specialist не бачить дій над записом іншого майстра", async () => {
    auth.user.role = "specialist";
    auth.user.specialistId = "other";
    renderScreen();
    await select();
    expect(screen.queryByRole("button", { name: "Завершити візит" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Підтвердити запис" })).not.toBeInTheDocument();
  });

  it("під час перенесення або скасування кнопки статусу сховані", async () => {
    renderScreen();
    const user = await select();
    await user.click(screen.getByRole("button", { name: "Перенести запис" }));
    expect(screen.queryByRole("button", { name: "Завершити візит" })).not.toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Не переносити" }));
    await user.click(screen.getByRole("button", { name: "Скасувати запис" }));
    expect(screen.queryByRole("button", { name: "Завершити візит" })).not.toBeInTheDocument();
  });
});

describe("перенос запису: регресія TASK-775 (перенос не блокується легальними часами)", () => {
  it("вільний слот того ж дня (ще й поза початковою годиною) і новий час у повідомленні", async () => {
    current = [{ ...base, status: "confirmed" }];
    renderScreen();
    const user = await select();
    await user.click(screen.getByRole("button", { name: "Перенести запис" }));
    const input = screen.getByLabelText("Новий час візиту");
    fireEvent.change(input, { target: { value: "2026-10-05T16:30" } });
    expect(screen.queryByTestId("move-problem")).not.toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Зберегти час" }));
    expect(moveAppointment).toHaveBeenCalledWith("x", "2026-10-05T16:30");
    expect(await screen.findByText(/Запис перенесено на .*16:30\./)).toBeInTheDocument();
  });

  it("КОРІНЬ: owner без обраного майстра (chosenId = \"\") - після переносу календар перечитується й блок стоїть на новому часі", async () => {
    current = [{ ...base, status: "confirmed" }];
    renderScreen();
    const user = await select();
    moveAppointment.mockImplementation(async (_id, startsAt) => {
      current = [{ ...base, status: "confirmed", startsAt }];
    });
    const callsBefore = getCalendarWeek.mock.calls.length;
    await user.click(screen.getByRole("button", { name: "Перенести запис" }));
    fireEvent.change(screen.getByLabelText("Новий час візиту"), { target: { value: "2026-10-05T15:00" } });
    await user.click(screen.getByRole("button", { name: "Зберегти час" }));
    expect(await screen.findByRole("button", { name: /Олена.*15:00–16:00/ })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /Олена.*11:00–12:00/ })).not.toBeInTheDocument();
    expect(getCalendarWeek.mock.calls.length).toBeGreaterThan(callsBefore);
  });

  it("КОРІНЬ (скасування): календар owner теж перечитується, скасований запис зникає з тижня", async () => {
    current = [{ ...base, status: "confirmed" }];
    renderScreen();
    const user = await select();
    cancelAppointment.mockImplementation(async () => {
      current = [];
      return { refundAmount: 350 };
    });
    await user.click(screen.getByRole("button", { name: "Скасувати запис" }));
    await user.click(screen.getByRole("button", { name: "Так, скасувати" }));
    await waitFor(() => expect(screen.queryByRole("button", { name: /Олена/ })).not.toBeInTheDocument());
  });

  it("інший день, інший тиждень (перевіряє backend) і зміна лише хвилин - кнопка активна", async () => {
    current = [{ ...base, status: "confirmed" }];
    renderScreen();
    const user = await select();
    await user.click(screen.getByRole("button", { name: "Перенести запис" }));
    const input = screen.getByLabelText("Новий час візиту");
    for (const v of ["2026-10-07T09:00", "2026-10-05T11:15", "2026-10-14T10:00"]) {
      fireEvent.change(input, { target: { value: v } });
      expect(screen.queryByTestId("move-problem")).not.toBeInTheDocument();
      expect(screen.getByRole("button", { name: "Зберегти час" })).toBeEnabled();
    }
  });

  it("помилка backend при переносі показується українською, форма лишається для нового вибору", async () => {
    current = [{ ...base, status: "confirmed" }];
    moveAppointment.mockRejectedValue(new BeautyApiError(422, "slot_in_past", "Start time is in the past."));
    renderScreen();
    const user = await select();
    await user.click(screen.getByRole("button", { name: "Перенести запис" }));
    fireEvent.change(screen.getByLabelText("Новий час візиту"), { target: { value: "2026-10-05T12:00" } });
    await user.click(screen.getByRole("button", { name: "Зберегти час" }));
    expect(await screen.findByText("Не можна перенести запис у минуле.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Зберегти час" })).toBeEnabled();
  });
});
