import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { BeautyApiError } from "@/features/beauty-auth/errors";
import type { BeautyLocation, LocationInput, StaffMember } from "../types";

const push = vi.fn();
vi.mock("next/navigation", () => ({ useRouter: () => ({ push, replace: vi.fn() }), usePathname: () => "/beauty/locations" }));
const auth = { user: { id: "u", email: "e", fullName: "n", role: "owner", specialistId: null as string | null } };
vi.mock("@/features/beauty-auth/components/auth-provider", () => ({ useAuth: () => auth }));

const getManagedLocations = vi.fn<() => Promise<BeautyLocation[]>>();
const getStaff = vi.fn<() => Promise<StaffMember[]>>();
const createLocation = vi.fn<(i: LocationInput) => Promise<BeautyLocation>>();
const updateLocation = vi.fn<(id: string, i: LocationInput) => Promise<BeautyLocation>>();
vi.mock("../api", () => ({
  beautyApi: {
    getManagedLocations: () => getManagedLocations(),
    getStaff: () => getStaff(),
    createLocation: (i: LocationInput) => createLocation(i),
    updateLocation: (id: string, i: LocationInput) => updateLocation(id, i),
  },
}));

import { LocationsScreen } from "./locations-screen";

const center: BeautyLocation = {
  id: "c",
  name: "Центр",
  address: "вул. Хрещатик, 22",
  phone: "+380 44 000 11 22",
  timezone: "Europe/Kyiv",
  isActive: true,
  closedWeekdays: ["sun"],
  workingHours: { mon: [{ from: "09:00", to: "20:00" }], tue: [{ from: "09:00", to: "20:00" }] },
};
const closed: BeautyLocation = { id: "x", name: "Старий", address: null, phone: null, timezone: "Europe/Kyiv", isActive: false };
const member = (id: string, locs: string[], isActive = true): StaffMember => ({
  id,
  name: `М ${id}`,
  phone: null,
  position: null,
  isActive,
  services: [],
  locations: locs.map((l) => ({ locationId: l, locationName: l, workingHours: {} })),
});

function renderScreen() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <LocationsScreen />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  auth.user.role = "owner";
  push.mockReset();
  getManagedLocations.mockReset().mockResolvedValue([center, closed]);
  getStaff.mockReset().mockResolvedValue([member("a", ["c"]), member("b", ["c"]), member("n", ["c"], false), member("z", ["x"])]);
  createLocation.mockReset();
  updateLocation.mockReset();
});

describe("список закладів: лише перегляд", () => {
  it("картка показує адресу, телефон, статус, кількість активних спеціалістів, години й вихідні", async () => {
    renderScreen();
    const card = (await screen.findByText("Центр")).closest("li")!;
    expect(within(card).getByText("Активний")).toBeInTheDocument();
    expect(within(card).getByText("вул. Хрещатик, 22")).toBeInTheDocument();
    expect(within(card).getByText("+380 44 000 11 22")).toBeInTheDocument();
    await waitFor(() => expect(within(card).getByText("2")).toBeInTheDocument());
    expect(within(card).getByText(/Працює: Пн 09:00–20:00; Вт 09:00–20:00/)).toBeInTheDocument();
    expect(within(card).getByText("Вихідні: Нд")).toBeInTheDocument();
  });

  it("без годин - «Години не задано», без вихідних - «Без вихідних»; неактивний позначено", async () => {
    renderScreen();
    const card = (await screen.findByText("Старий")).closest("li")!;
    expect(within(card).getByText("Неактивний")).toBeInTheDocument();
    expect(within(card).getByText("Години не задано")).toBeInTheDocument();
    expect(within(card).getByText("Без вихідних")).toBeInTheDocument();
  });

  it("немає inline-редагування: лише посилання «Відкрити» на сторінку закладу", async () => {
    renderScreen();
    await screen.findByText("Центр");
    expect(screen.queryByRole("button", { name: /Редагувати/ })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /Деактивувати/ })).not.toBeInTheDocument();
    expect(screen.queryByRole("group", { name: /Щотижневі вихідні/ })).not.toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Відкрити заклад «Центр»" })).toHaveAttribute("href", "/beauty/locations/c");
  });

  it("owner/admin бачать «Додати заклад»; specialist - повідомлення без запитів", async () => {
    auth.user.role = "admin";
    const { unmount } = renderScreen();
    expect(await screen.findByRole("button", { name: "Додати заклад" })).toBeInTheDocument();
    unmount();
    auth.user.role = "specialist";
    getManagedLocations.mockClear();
    renderScreen();
    expect(screen.getByText(/лише власник і адміністратор/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Додати заклад" })).not.toBeInTheDocument();
    expect(getManagedLocations).not.toHaveBeenCalled();
  });
});

describe("додавання закладу (мінімальна форма)", () => {
  it("форма містить назву, адресу й зону, без телефону й статусу", async () => {
    const user = userEvent.setup();
    renderScreen();
    await screen.findByText("Центр");
    await user.click(screen.getByRole("button", { name: "Додати заклад" }));
    expect(screen.getByLabelText(/Назва/)).toBeInTheDocument();
    expect(screen.getByLabelText(/Адреса/)).toBeInTheDocument();
    expect(screen.getByLabelText(/Часова зона/)).toHaveValue("Europe/Kyiv");
    expect(screen.queryByLabelText(/Телефон/)).not.toBeInTheDocument();
    expect(screen.queryByRole("checkbox", { name: /Заклад активний/ })).not.toBeInTheDocument();
  });

  it("невідома часова зона не відправляється", async () => {
    const user = userEvent.setup();
    renderScreen();
    await screen.findByText("Центр");
    await user.click(screen.getByRole("button", { name: "Додати заклад" }));
    await user.type(screen.getByLabelText(/Назва/), "Нова філія");
    const tz = screen.getByLabelText(/Часова зона/);
    await user.clear(tz);
    await user.type(tz, "Nowhere");
    await user.click(screen.getByRole("button", { name: "Додати заклад" }));
    expect(await screen.findByText(/Оберіть зону зі списку/)).toBeInTheDocument();
    expect(createLocation).not.toHaveBeenCalled();
  });

  it("створює заклад і переходить на його сторінку", async () => {
    const user = userEvent.setup();
    createLocation.mockResolvedValue({ ...center, id: "n", name: "Нова філія" });
    renderScreen();
    await screen.findByText("Центр");
    await user.click(screen.getByRole("button", { name: "Додати заклад" }));
    await user.type(screen.getByLabelText(/Назва/), "Нова філія");
    await user.type(screen.getByLabelText(/Адреса/), "вул. Нова, 1");
    await user.click(screen.getByRole("button", { name: "Додати заклад" }));
    await waitFor(() =>
      expect(createLocation).toHaveBeenCalledWith({ name: "Нова філія", address: "вул. Нова, 1", phone: "", timezone: "Europe/Kyiv", isActive: true }),
    );
    await waitFor(() => expect(push).toHaveBeenCalledWith("/beauty/locations/n"));
  });

  it("помилка сервера location_name_taken показується українською", async () => {
    const user = userEvent.setup();
    createLocation.mockRejectedValue(new BeautyApiError(409, "location_name_taken", "taken"));
    renderScreen();
    await screen.findByText("Центр");
    await user.click(screen.getByRole("button", { name: "Додати заклад" }));
    await user.type(screen.getByLabelText(/Назва/), "Центр");
    await user.click(screen.getByRole("button", { name: "Додати заклад" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("Заклад із такою назвою вже існує");
    expect(push).not.toHaveBeenCalled();
  });

  it("timezone_locked і invalid_timezone мають окремі тексти", async () => {
    const { humanizeError } = await import("@/features/beauty-auth/errors");
    expect(humanizeError(new BeautyApiError(409, "timezone_locked", ""))).toMatch(/часову зону/);
    expect(humanizeError(new BeautyApiError(422, "invalid_timezone", ""))).toMatch(/Невідома часова зона/);
  });
});
