import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { BeautyApiError } from "@/features/beauty-auth/errors";
import type { BeautyLocation, LocationInput } from "../types";

const auth = { user: { id: "u", email: "e", fullName: "n", role: "owner", specialistId: null as string | null } };
vi.mock("@/features/beauty-auth/components/auth-provider", () => ({ useAuth: () => auth }));

const getManagedLocations = vi.fn<() => Promise<BeautyLocation[]>>();
const createLocation = vi.fn<(i: LocationInput) => Promise<BeautyLocation>>();
const updateLocation = vi.fn<(id: string, i: LocationInput) => Promise<BeautyLocation>>();
vi.mock("../api", () => ({
  beautyApi: {
    getManagedLocations: () => getManagedLocations(),
    createLocation: (i: LocationInput) => createLocation(i),
    updateLocation: (id: string, i: LocationInput) => updateLocation(id, i),
    getClosures: () => Promise.resolve([]),
  },
}));

import { LocationsScreen } from "./locations-screen";

const center: BeautyLocation = { id: "c", name: "Центр", address: "вул. Хрещатик, 22", phone: "+380 44 000 11 22", timezone: "Europe/Kyiv", isActive: true };
const closed: BeautyLocation = { id: "x", name: "Старий", address: null, phone: null, timezone: "Europe/Kyiv", isActive: false };

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
  getManagedLocations.mockReset().mockResolvedValue([center, closed]);
  createLocation.mockReset();
  updateLocation.mockReset();
});

describe("екран Заклади: видимість за роллю", () => {
  it("owner/admin бачать список зі статусами й кнопку Додати заклад", async () => {
    renderScreen();
    expect(await screen.findByText("Центр")).toBeInTheDocument();
    expect(screen.getByText("Активний")).toBeInTheDocument();
    expect(screen.getByText("Неактивний")).toBeInTheDocument();
    expect(screen.getByText("вул. Хрещатик, 22")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Додати заклад" })).toBeInTheDocument();
  });

  it("admin теж бачить керування", async () => {
    auth.user.role = "admin";
    renderScreen();
    expect(await screen.findByRole("button", { name: "Додати заклад" })).toBeInTheDocument();
  });

  it("specialist не бачить керування й не вантажить список", () => {
    auth.user.role = "specialist";
    renderScreen();
    expect(screen.getByText(/лише власник і адміністратор/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Додати заклад" })).not.toBeInTheDocument();
    expect(getManagedLocations).not.toHaveBeenCalled();
  });
});

describe("форма закладу", () => {
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

  it("створює заклад зі значеннями форми (зона за замовчуванням Europe/Kyiv)", async () => {
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
  });

  it("деактивація потребує підтвердження; has_future_appointments пояснено", async () => {
    const user = userEvent.setup();
    updateLocation.mockRejectedValue(new BeautyApiError(409, "has_future_appointments", "future"));
    renderScreen();
    await screen.findByText("Центр");
    await user.click(screen.getByRole("button", { name: "Деактивувати «Центр»" }));
    expect(updateLocation).not.toHaveBeenCalled();
    await user.click(screen.getByRole("button", { name: "Так, деактивувати" }));
    await waitFor(() => expect(updateLocation).toHaveBeenCalledWith("c", expect.objectContaining({ isActive: false, timezone: "Europe/Kyiv" })));
    expect(await screen.findByRole("alert")).toHaveTextContent("Є майбутні записи");
  });

  it("зняття прапорця активності у формі теж вимагає підтвердження", async () => {
    const user = userEvent.setup();
    updateLocation.mockResolvedValue({ ...center, isActive: false });
    renderScreen();
    await screen.findByText("Центр");
    await user.click(screen.getByRole("button", { name: "Редагувати «Центр»" }));
    await user.click(screen.getByRole("checkbox", { name: /Заклад активний/ }));
    await user.click(screen.getByRole("button", { name: "Зберегти" }));
    expect(updateLocation).not.toHaveBeenCalled();
    await user.click(await screen.findByRole("button", { name: "Так, деактивувати" }));
    await waitFor(() => expect(updateLocation).toHaveBeenCalledWith("c", expect.objectContaining({ isActive: false })));
  });

  it("timezone_locked і invalid_timezone мають окремі тексти", async () => {
    const { humanizeError } = await import("@/features/beauty-auth/errors");
    expect(humanizeError(new BeautyApiError(409, "timezone_locked", ""))).toMatch(/часову зону/);
    expect(humanizeError(new BeautyApiError(422, "invalid_timezone", ""))).toMatch(/Невідома часова зона/);
  });
});
