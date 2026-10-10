import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { BeautyApiError } from "@/features/beauty-auth/errors";
import type { BeautyLocation, ClosureInput, LocationClosure, Weekday } from "../types";

const setClosedWeekdays = vi.fn<(id: string, w: Weekday[], confirm?: boolean) => Promise<Weekday[]>>();
const getClosures = vi.fn<() => Promise<LocationClosure[]>>();
const addClosure = vi.fn<(id: string, i: ClosureInput) => Promise<LocationClosure>>();
const deleteClosure = vi.fn<(id: string, closureId: string) => Promise<void>>();
vi.mock("../api", () => ({
  beautyApi: {
    setClosedWeekdays: (id: string, w: Weekday[], c?: boolean) => setClosedWeekdays(id, w, c),
    getClosures: () => getClosures(),
    addClosure: (id: string, i: ClosureInput) => addClosure(id, i),
    deleteClosure: (id: string, c: string) => deleteClosure(id, c),
  },
}));

import { ClosedDaysSection } from "./closed-days";

const center: BeautyLocation = { id: "c", name: "Центр", timezone: "Europe/Kyiv", isActive: true, closedWeekdays: [] };
const conflicts = [
  { appointmentId: "a1", startsAt: "2026-10-11T08:00:00Z", serviceName: "Манікюр", specialistName: "Марина Бойко" },
  { appointmentId: "a2", startsAt: "2026-10-11T10:30:00Z", serviceName: "Стрижка", specialistName: "Ірина Мельник" },
];
const needConfirm = () => new BeautyApiError(409, "has_appointments_on_closed_days", "x", undefined, conflicts);

function renderSection(over: { location?: BeautyLocation; canEdit?: boolean; showReason?: boolean } = {}) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <ClosedDaysSection location={over.location ?? center} canEdit={over.canEdit ?? true} showReason={over.showReason ?? true} />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  setClosedWeekdays.mockReset();
  getClosures.mockReset().mockResolvedValue([]);
  addClosure.mockReset();
  deleteClosure.mockReset().mockResolvedValue(undefined);
});

describe("вихідні дні тижня", () => {
  it("за замовчуванням усі дні працюють: жоден перемикач не позначений, 7 перемикачів", async () => {
    renderSection();
    const group = screen.getByRole("group", { name: /Щотижневі вихідні/ });
    const boxes = within(group).getAllByRole("checkbox");
    expect(boxes).toHaveLength(7);
    boxes.forEach((b) => expect(b).not.toBeChecked());
    expect(screen.getByRole("button", { name: "Зберегти вихідні" })).toBeDisabled();
    await screen.findByText(/Закриттів на найближчий рік немає/);
  });

  it("зберігає перелік без confirm", async () => {
    const user = userEvent.setup();
    setClosedWeekdays.mockResolvedValue(["sun"]);
    renderSection();
    await user.click(screen.getByRole("checkbox", { name: "Неділя" }));
    await user.click(screen.getByRole("button", { name: "Зберегти вихідні" }));
    await waitFor(() => expect(setClosedWeekdays).toHaveBeenCalledWith("c", ["sun"], false));
    expect(await screen.findByText("Вихідні збережено.")).toBeInTheDocument();
  });

  it("409 has_appointments_on_closed_days: діалог зі списком; підтвердження повторює запит з confirm=true", async () => {
    const user = userEvent.setup();
    setClosedWeekdays.mockRejectedValueOnce(needConfirm()).mockResolvedValueOnce(["sun"]);
    renderSection();
    await user.click(screen.getByRole("checkbox", { name: "Неділя" }));
    await user.click(screen.getByRole("button", { name: "Зберегти вихідні" }));

    const dialog = await screen.findByRole("alertdialog");
    expect(within(dialog).getByText(/активні записи \(2\)/)).toBeInTheDocument();
    const items = within(dialog).getAllByRole("listitem");
    expect(items).toHaveLength(2);
    expect(items[0]).toHaveTextContent("Манікюр");
    expect(items[0]).toHaveTextContent("Марина Бойко");
    // 08:00Z у зоні Києва (UTC+3) = 11:00
    expect(items[0]).toHaveTextContent("11:00");
    expect(items[1]).toHaveTextContent("Стрижка · Ірина Мельник");
    expect(setClosedWeekdays).toHaveBeenCalledTimes(1);

    await user.click(within(dialog).getByRole("button", { name: "Підтвердити й зберегти вихідний" }));
    await waitFor(() => expect(setClosedWeekdays).toHaveBeenLastCalledWith("c", ["sun"], true));
    await waitFor(() => expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument());
    expect(await screen.findByText("Вихідні збережено.")).toBeInTheDocument();
  });

  it("Скасувати в діалозі: повторного запиту немає, перемикачі повертаються", async () => {
    const user = userEvent.setup();
    setClosedWeekdays.mockRejectedValue(needConfirm());
    renderSection();
    await user.click(screen.getByRole("checkbox", { name: "Неділя" }));
    await user.click(screen.getByRole("button", { name: "Зберегти вихідні" }));
    const dialog = await screen.findByRole("alertdialog");
    await user.click(within(dialog).getByRole("button", { name: "Скасувати" }));
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
    expect(setClosedWeekdays).toHaveBeenCalledTimes(1);
    expect(screen.getByRole("checkbox", { name: "Неділя" })).not.toBeChecked();
  });

  it("інша помилка показується українською без діалогу", async () => {
    const user = userEvent.setup();
    setClosedWeekdays.mockRejectedValue(new BeautyApiError(403, "forbidden_role", "x"));
    renderSection();
    await user.click(screen.getByRole("checkbox", { name: "Субота" }));
    await user.click(screen.getByRole("button", { name: "Зберегти вихідні" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("немає прав");
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
  });
});

describe("закриття на дати", () => {
  const closure: LocationClosure = { id: "cl1", locationId: "c", dateFrom: "2026-12-31", dateTo: "2027-01-02", reason: "Новорічні свята" };

  it("показує діапазон і причину керівнику; видалення викликає API", async () => {
    const user = userEvent.setup();
    getClosures.mockResolvedValue([closure]);
    renderSection();
    const item = await screen.findByRole("listitem");
    expect(item).toHaveTextContent("Новорічні свята");
    await user.click(within(item).getByRole("button", { name: /Видалити закриття/ }));
    await waitFor(() => expect(deleteClosure).toHaveBeenCalledWith("c", "cl1"));
  });

  it("видимість причини за роллю: без причини, якщо showReason=false (specialist)", async () => {
    getClosures.mockResolvedValue([closure]);
    renderSection({ canEdit: false, showReason: false });
    const item = await screen.findByRole("listitem");
    expect(item).not.toHaveTextContent("Новорічні свята");
    expect(item).toHaveTextContent("2026");
  });

  it("лише читання: немає форми, кнопок видалення й збереження, перемикачі вимкнені", async () => {
    getClosures.mockResolvedValue([closure]);
    renderSection({ canEdit: false, showReason: false });
    await screen.findByRole("listitem");
    expect(screen.queryByRole("button", { name: /Видалити/ })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Додати закриття" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Зберегти вихідні" })).not.toBeInTheDocument();
    screen.getAllByRole("checkbox").forEach((b) => expect(b).toBeDisabled());
  });

  it("додає діапазон із причиною (trim, не більше 200 символів)", async () => {
    const user = userEvent.setup();
    addClosure.mockResolvedValue(closure);
    renderSection();
    await user.type(screen.getByLabelText(/Закрито з/), "2026-12-31");
    const to = screen.getByLabelText(/Закрито по/);
    await user.clear(to);
    await user.type(to, "2027-01-02");
    const reason = screen.getByLabelText(/Причина/);
    expect(reason).toHaveAttribute("maxlength", "200");
    await user.type(reason, "  Свята ");
    await user.click(screen.getByRole("button", { name: "Додати закриття" }));
    await waitFor(() =>
      expect(addClosure).toHaveBeenCalledWith("c", { dateFrom: "2026-12-31", dateTo: "2027-01-02", reason: "Свята" }),
    );
  });

  it("кінець раніше початку не надсилається", async () => {
    const user = userEvent.setup();
    renderSection();
    await user.type(screen.getByLabelText(/Закрито з/), "2026-12-31");
    const to = screen.getByLabelText(/Закрито по/);
    await user.clear(to);
    await user.type(to, "2026-12-30");
    await user.click(screen.getByRole("button", { name: "Додати закриття" }));
    expect(await screen.findByRole("alert")).toHaveTextContent(/раніше/);
    expect(addClosure).not.toHaveBeenCalled();
  });

  it("409 has_appointments_on_closed_days для закриття: діалог і повтор з confirm=true", async () => {
    const user = userEvent.setup();
    addClosure.mockRejectedValueOnce(needConfirm()).mockResolvedValueOnce(closure);
    renderSection();
    await user.type(screen.getByLabelText(/Закрито з/), "2026-10-11");
    await user.click(screen.getByRole("button", { name: "Додати закриття" }));
    const dialog = await screen.findByRole("alertdialog");
    expect(within(dialog).getAllByRole("listitem")).toHaveLength(2);
    await user.click(within(dialog).getByRole("button", { name: "Підтвердити й зберегти вихідний" }));
    await waitFor(() =>
      expect(addClosure).toHaveBeenLastCalledWith(
        "c",
        expect.objectContaining({ dateFrom: "2026-10-11", dateTo: "2026-10-11", confirm: true }),
      ),
    );
  });

  it("closure_overlap - зрозумілий текст українською", async () => {
    const user = userEvent.setup();
    addClosure.mockRejectedValue(new BeautyApiError(409, "closure_overlap", "overlap"));
    renderSection();
    await user.type(screen.getByLabelText(/Закрито з/), "2026-12-31");
    await user.click(screen.getByRole("button", { name: "Додати закриття" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("вже є закриття закладу");
  });
});
