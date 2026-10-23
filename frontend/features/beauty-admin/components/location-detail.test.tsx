import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { BeautyApiError } from "@/features/beauty-auth/errors";
import type { BeautyLocation, LocationInput, StaffMember, WorkingHours } from "../types";

vi.mock("next/navigation", () => ({ useRouter: () => ({ push: vi.fn(), replace: vi.fn() }), usePathname: () => "/beauty/locations/c" }));
const auth = { user: { id: "u", email: "e", fullName: "n", role: "owner", specialistId: null as string | null } };
vi.mock("@/features/beauty-auth/components/auth-provider", () => ({ useAuth: () => auth }));

const getManagedLocations = vi.fn<() => Promise<BeautyLocation[]>>();
const getLocations = vi.fn<() => Promise<BeautyLocation[]>>();
const getStaff = vi.fn<() => Promise<StaffMember[]>>();
const getClosures = vi.fn(() => Promise.resolve([{ id: "k1", locationId: "c", dateFrom: "2026-12-31", dateTo: "2027-01-02", reason: "Свята" }]));
const setLocationWorkingHours = vi.fn<(id: string, h: WorkingHours | null, confirm?: boolean) => Promise<WorkingHours | null>>();
const updateLocation = vi.fn<(id: string, i: LocationInput) => Promise<BeautyLocation>>();
vi.mock("../api", () => ({
  beautyApi: {
    getManagedLocations: () => getManagedLocations(),
    getLocations: () => getLocations(),
    getStaff: () => getStaff(),
    getClosures: () => getClosures(),
    setLocationWorkingHours: (id: string, h: WorkingHours | null, c?: boolean) => setLocationWorkingHours(id, h, c),
    updateLocation: (id: string, i: LocationInput) => updateLocation(id, i),
  },
}));

import { LocationDetailScreen } from "./location-detail";

const center: BeautyLocation = {
  id: "c",
  name: "Центр",
  address: "вул. Хрещатик, 22 & Київ",
  phone: "+380 44 000 11 22",
  timezone: "Europe/Kyiv",
  isActive: true,
  closedWeekdays: ["sun"],
  workingHours: { mon: [{ from: "09:00", to: "18:00" }] },
};
const noHours: BeautyLocation = { ...center, id: "n", name: "Без годин", address: "", workingHours: null };

const member = (id: string, name: string, locs: { id: string; hours: WorkingHours }[], isActive = true): StaffMember => ({
  id,
  name,
  phone: null,
  position: "Майстер",
  isActive,
  services: [],
  locations: locs.map((l) => ({ locationId: l.id, locationName: l.id, workingHours: l.hours })),
});
const staff = [
  member("m", "Марина", [{ id: "c", hours: { mon: [{ from: "10:00", to: "19:00" }] } }, { id: "p", hours: { tue: [{ from: "11:00", to: "12:00" }] } }]),
  member("o", "Оксана", [{ id: "c", hours: {} }], false),
  member("i", "Ірина", [{ id: "p", hours: {} }]),
];

function renderScreen(id = "c") {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <LocationDetailScreen id={id} />
    </QueryClientProvider>,
  );
}

const conflicts = [
  { appointmentId: "a1", startsAt: "2026-10-12T07:00:00Z", serviceName: "Манікюр", specialistName: "Марина" },
  { appointmentId: "a2", startsAt: "2026-10-13T15:30:00Z", serviceName: "Стрижка", specialistName: "Ірина" },
];

beforeEach(() => {
  auth.user.role = "owner";
  auth.user.specialistId = null;
  getManagedLocations.mockReset().mockResolvedValue([center, noHours]);
  getLocations.mockReset().mockResolvedValue([center]);
  getStaff.mockReset().mockResolvedValue(staff);
  setLocationWorkingHours.mockReset();
  updateLocation.mockReset();
});

describe("сторінка закладу: секції", () => {
  it("показує основне, години, вихідні й спеціалістів цього закладу", async () => {
    renderScreen();
    expect(await screen.findByRole("heading", { level: 1, name: "Центр" })).toBeInTheDocument();
    for (const h of ["Основне", "Години роботи", "Вихідні й закриття", "Спеціалісти закладу"]) {
      expect(screen.getByRole("heading", { level: 2, name: h })).toBeInTheDocument();
    }
    expect(screen.getByLabelText("Назва *")).toHaveValue("Центр");
    expect(screen.getByRole("link", { name: "← Усі заклади" })).toHaveAttribute("href", "/beauty/locations");
    // закриття на дати - існуючий компонент
    expect(await screen.findByText(/Свята/)).toBeInTheDocument();
  });

  it("неіснуючий заклад - «Заклад не знайдено»", async () => {
    renderScreen("zzz");
    expect(await screen.findByText("Заклад не знайдено.")).toBeInTheDocument();
  });
});

describe("посилання на карту", () => {
  it("кодована адреса, нова вкладка, noopener noreferrer", async () => {
    renderScreen();
    const link = await screen.findByRole("link", { name: "Відкрити на карті" });
    expect(link).toHaveAttribute("href", `https://www.google.com/maps/search/?api=1&query=${encodeURIComponent("вул. Хрещатик, 22 & Київ")}`);
    expect(link).toHaveAttribute("target", "_blank");
    expect(link).toHaveAttribute("rel", "noopener noreferrer");
  });

  it("порожня адреса - посилання немає", async () => {
    renderScreen("n");
    await screen.findByRole("heading", { level: 1, name: "Без годин" });
    expect(screen.queryByRole("link", { name: "Відкрити на карті" })).not.toBeInTheDocument();
  });
});

describe("спеціалісти закладу", () => {
  it("лише прив'язані до закладу; активність, графік у цьому закладі й посилання з контекстом закладу", async () => {
    renderScreen();
    const list = await screen.findByRole("list", { name: "Спеціалісти закладу" });
    const items = within(list).getAllByRole("listitem");
    expect(items).toHaveLength(2);
    expect(within(list).queryByText("Ірина")).not.toBeInTheDocument();
    const marina = items[0];
    expect(within(marina).getByText("Марина")).toBeInTheDocument();
    expect(within(marina).getByText("Активний")).toBeInTheDocument();
    // графік саме в цьому закладі (c), а не в Подолі
    expect(within(marina).getByText(/Пн 10:00–19:00/)).toBeInTheDocument();
    expect(within(marina).queryByText(/Вт 11:00/)).not.toBeInTheDocument();
    expect(within(marina).getByRole("link", { name: "Профіль: Марина" })).toHaveAttribute(
      "href",
      "/beauty/staff/m?from=location&locationId=c",
    );
    expect(within(items[1]).getByText("Неактивний")).toBeInTheDocument();
  });
});

describe("години роботи: редактор", () => {
  it("вмикається/вимикається прапорцем; без годин - «не задано» зберігається як null", async () => {
    const user = userEvent.setup();
    setLocationWorkingHours.mockResolvedValue(null);
    renderScreen();
    const toggle = await screen.findByRole("checkbox", { name: /Задати години роботи закладу/ });
    expect(toggle).toBeChecked();
    expect(screen.getByRole("button", { name: "Зберегти години" })).toBeDisabled();
    await user.click(toggle);
    await user.click(screen.getByRole("button", { name: "Зберегти години" }));
    await waitFor(() => expect(setLocationWorkingHours).toHaveBeenCalledWith("c", null, false));
    expect(await screen.findByText("Години роботи збережено.")).toBeInTheDocument();
  });

  it("новий заклад без годин: за замовчуванням 7 днів 09:00-18:00 при ввімкненні, збереження відправляє формат §9", async () => {
    const user = userEvent.setup();
    setLocationWorkingHours.mockImplementation((_id, h) => Promise.resolve(h));
    renderScreen("n");
    const toggle = await screen.findByRole("checkbox", { name: /Задати години роботи закладу/ });
    expect(toggle).not.toBeChecked();
    await user.click(toggle);
    // понеділок зробити вихідним
    const monday = screen.getByRole("group", { name: "Понеділок" });
    await user.click(within(monday).getByRole("checkbox", { name: "Працює" }));
    await user.click(screen.getByRole("button", { name: "Зберегти години" }));
    await waitFor(() => expect(setLocationWorkingHours).toHaveBeenCalledTimes(1));
    const [id, hours, confirm] = setLocationWorkingHours.mock.calls[0];
    expect(id).toBe("n");
    expect(confirm).toBe(false);
    expect(hours).not.toHaveProperty("mon");
    expect(hours?.tue).toEqual([{ from: "09:00", to: "18:00" }]);
    expect(hours?.sun).toEqual([{ from: "09:00", to: "18:00" }]);
  });

  it("некоректний інтервал не відправляється", async () => {
    const user = userEvent.setup();
    renderScreen();
    await screen.findByRole("checkbox", { name: /Задати години роботи закладу/ });
    const monday = screen.getByRole("group", { name: "Понеділок" });
    const from = within(monday).getByLabelText("Початок");
    await user.clear(from);
    await user.type(from, "19:00");
    await user.click(screen.getByRole("button", { name: "Зберегти години" }));
    expect(setLocationWorkingHours).not.toHaveBeenCalled();
    expect(screen.getAllByRole("alert").length).toBeGreaterThan(0);
  });
});

describe("години роботи: діалог підтвердження конфліктів", () => {
  async function triggerConflict() {
    const user = userEvent.setup();
    setLocationWorkingHours.mockRejectedValueOnce(new BeautyApiError(409, "has_appointments_outside_hours", "x", undefined, conflicts));
    renderScreen();
    await screen.findByRole("checkbox", { name: /Задати години роботи закладу/ });
    const monday = screen.getByRole("group", { name: "Понеділок" });
    const to = within(monday).getByLabelText("Кінець");
    await user.clear(to);
    await user.type(to, "12:00");
    await user.click(screen.getByRole("button", { name: "Зберегти години" }));
    return user;
  }

  it("409 показує список записів без клієнтських даних; confirm=true повторює запит", async () => {
    const user = await triggerConflict();
    const dialog = await screen.findByRole("alertdialog");
    expect(within(dialog).getByText(/На ці дні є активні записи \(2\)/)).toBeInTheDocument();
    const items = within(dialog).getAllByRole("listitem");
    expect(items).toHaveLength(2);
    expect(items[0]).toHaveTextContent("Манікюр");
    expect(items[0]).toHaveTextContent("Марина");
    expect(setLocationWorkingHours).toHaveBeenCalledTimes(1);
    expect(setLocationWorkingHours.mock.calls[0][2]).toBe(false);

    setLocationWorkingHours.mockResolvedValueOnce({ mon: [{ from: "09:00", to: "12:00" }] });
    await user.click(within(dialog).getByRole("button", { name: "Підтвердити й зберегти години" }));
    await waitFor(() => expect(setLocationWorkingHours).toHaveBeenCalledTimes(2));
    expect(setLocationWorkingHours.mock.calls[1][2]).toBe(true);
    expect(setLocationWorkingHours.mock.calls[1][1]).toEqual({ mon: [{ from: "09:00", to: "12:00" }] });
    await waitFor(() => expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument());
  });

  it("«Скасувати» не повторює запит і повертає збережені години", async () => {
    const user = await triggerConflict();
    const dialog = await screen.findByRole("alertdialog");
    await user.click(within(dialog).getByRole("button", { name: "Скасувати" }));
    expect(setLocationWorkingHours).toHaveBeenCalledTimes(1);
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
    const monday = screen.getByRole("group", { name: "Понеділок" });
    expect(within(monday).getByLabelText("Кінець")).toHaveValue("18:00");
  });

  it("інша помилка показується українською без діалогу", async () => {
    const user = userEvent.setup();
    setLocationWorkingHours.mockRejectedValueOnce(new BeautyApiError(403, "forbidden_role", "x"));
    renderScreen();
    await screen.findByRole("checkbox", { name: /Задати години роботи закладу/ });
    await user.click(screen.getByRole("checkbox", { name: /Задати години роботи закладу/ }));
    await user.click(screen.getByRole("button", { name: "Зберегти години" }));
    expect(await screen.findByText("У вас немає прав на цю дію.")).toBeInTheDocument();
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
  });
});

describe("основне: редагування закладу", () => {
  it("деактивація потребує підтвердження", async () => {
    const user = userEvent.setup();
    updateLocation.mockResolvedValue({ ...center, isActive: false });
    renderScreen();
    await user.click(await screen.findByRole("checkbox", { name: /Заклад активний/ }));
    await user.click(screen.getByRole("button", { name: "Зберегти" }));
    expect(updateLocation).not.toHaveBeenCalled();
    await user.click(await screen.findByRole("button", { name: "Так, деактивувати" }));
    await waitFor(() => expect(updateLocation).toHaveBeenCalledWith("c", expect.objectContaining({ isActive: false, timezone: "Europe/Kyiv" })));
  });

  it("зміна назви зберігається, has_future_appointments пояснено", async () => {
    const user = userEvent.setup();
    updateLocation.mockRejectedValue(new BeautyApiError(409, "has_future_appointments", "x"));
    renderScreen();
    const name = await screen.findByLabelText("Назва *");
    await user.clear(name);
    await user.type(name, "Центр 2");
    await user.click(screen.getByRole("checkbox", { name: /Заклад активний/ }));
    await user.click(screen.getByRole("button", { name: "Зберегти" }));
    await user.click(await screen.findByRole("button", { name: "Так, деактивувати" }));
    expect(updateLocation).toHaveBeenCalledWith("c", expect.objectContaining({ name: "Центр 2" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("Є майбутні записи");
  });
});

describe("specialist: перегляд без редагування", () => {
  beforeEach(() => {
    auth.user.role = "specialist";
    auth.user.specialistId = "m";
  });

  it("немає форм і кнопок збереження, список закладів не запитується, причина закриття прихована", async () => {
    getClosures.mockResolvedValueOnce([{ id: "k1", locationId: "c", dateFrom: "2026-12-31", dateTo: "2027-01-02" } as never]);
    renderScreen();
    expect(await screen.findByRole("heading", { level: 1, name: "Центр" })).toBeInTheDocument();
    expect(getManagedLocations).not.toHaveBeenCalled();
    expect(getLocations).toHaveBeenCalled();
    expect(screen.queryByRole("button", { name: "Зберегти" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Зберегти години" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Зберегти вихідні" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Додати закриття" })).not.toBeInTheDocument();
    expect(screen.queryByRole("textbox")).not.toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "← Усі заклади" })).not.toBeInTheDocument();
    // години й вихідні - лише текстом
    expect(screen.getByRole("list", { name: "Години роботи закладу" })).toHaveTextContent("09:00–18:00");
    expect(screen.queryByText(/Свята/)).not.toBeInTheDocument();
    expect(await screen.findByText(/31 груд\. 2026/)).toBeInTheDocument();
  });

  it("посилання на карту лишається; профіль - лише власний", async () => {
    renderScreen();
    expect(await screen.findByRole("link", { name: "Відкрити на карті" })).toBeInTheDocument();
    const list = await screen.findByRole("list", { name: "Спеціалісти закладу" });
    expect(within(list).getByRole("link", { name: "Профіль: Марина" })).toBeInTheDocument();
    expect(within(list).queryByRole("link", { name: "Профіль: Оксана" })).not.toBeInTheDocument();
  });
});
