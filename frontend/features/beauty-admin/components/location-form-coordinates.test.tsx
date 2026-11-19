import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { BeautyApiError } from "@/features/beauty-auth/errors";
import type { BeautyLocation, LocationInput } from "../types";

const createLocation = vi.fn<(i: LocationInput) => Promise<BeautyLocation>>();
const updateLocation = vi.fn<(id: string, i: LocationInput) => Promise<BeautyLocation>>();
vi.mock("../api", () => ({
  beautyApi: {
    createLocation: (i: LocationInput) => createLocation(i),
    updateLocation: (id: string, i: LocationInput) => updateLocation(id, i),
  },
}));

import { LocationForm } from "./location-form";

const loc: BeautyLocation = { id: "c", name: "Центр", address: "вул. 1", phone: null, timezone: "Europe/Kyiv", isActive: true, latitude: 50.45, longitude: 30.52 };

function renderForm(location: BeautyLocation | null) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <LocationForm location={location} />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  createLocation.mockReset().mockImplementation(async (i) => ({ id: "n", name: i.name, timezone: i.timezone }));
  updateLocation.mockReset().mockImplementation(async (_id, i) => ({ ...loc, ...i }));
});

describe("форма закладу: координати (§28.11)", () => {
  it("редагування: поля заповнені, зміна відправляє числа (кома -> крапка)", async () => {
    const user = userEvent.setup();
    renderForm(loc);
    const lat = screen.getByLabelText("Широта");
    expect(lat).toHaveValue("50.45");
    expect(screen.getByLabelText("Довгота")).toHaveValue("30.52");
    await user.clear(lat);
    await user.type(lat, "49,8397");
    await user.click(screen.getByRole("button", { name: "Зберегти" }));
    await waitFor(() => expect(updateLocation).toHaveBeenCalled());
    expect(updateLocation.mock.calls[0][1]).toMatchObject({ latitude: 49.8397, longitude: 30.52 });
  });

  it("очищення обох полів відправляє пару null", async () => {
    const user = userEvent.setup();
    renderForm(loc);
    await user.clear(screen.getByLabelText("Широта"));
    await user.clear(screen.getByLabelText("Довгота"));
    await user.click(screen.getByRole("button", { name: "Зберегти" }));
    await waitFor(() => expect(updateLocation).toHaveBeenCalled());
    expect(updateLocation.mock.calls[0][1]).toMatchObject({ latitude: null, longitude: null });
  });

  it("лише одне поле або значення поза діапазоном - помилка біля поля, запиту немає", async () => {
    const user = userEvent.setup();
    renderForm(null);
    await user.type(screen.getByLabelText(/^Назва/), "Новий");
    await user.type(screen.getByLabelText("Широта"), "95");
    await user.type(screen.getByLabelText("Довгота"), "30");
    await user.click(screen.getByRole("button", { name: "Додати заклад" }));
    expect(screen.getByLabelText("Широта")).toHaveAttribute("aria-invalid", "true");
    expect(createLocation).not.toHaveBeenCalled();
    await user.clear(screen.getByLabelText("Широта"));
    await user.type(screen.getByLabelText("Широта"), "50");
    await user.clear(screen.getByLabelText("Довгота"));
    await user.click(screen.getByRole("button", { name: "Додати заклад" }));
    expect(screen.getByLabelText("Довгота")).toHaveAttribute("aria-invalid", "true");
    expect(createLocation).not.toHaveBeenCalled();
  });

  it("порожні координати при створенні не блокують збереження", async () => {
    const user = userEvent.setup();
    renderForm(null);
    await user.type(screen.getByLabelText(/^Назва/), "Новий");
    await user.click(screen.getByRole("button", { name: "Додати заклад" }));
    await waitFor(() => expect(createLocation).toHaveBeenCalled());
    expect(createLocation.mock.calls[0][0].latitude).toBeUndefined();
  });

  it("422 invalid_coordinates від сервера показується зрозумілим текстом", async () => {
    updateLocation.mockRejectedValue(new BeautyApiError(422, "invalid_coordinates", "raw english"));
    const user = userEvent.setup();
    renderForm(loc);
    await user.click(screen.getByRole("button", { name: "Зберегти" }));
    const text = await screen.findByText(/Некоректні координати/);
    expect(text).toBeInTheDocument();
    expect(document.body.textContent).not.toMatch(/raw english/);
  });
});
