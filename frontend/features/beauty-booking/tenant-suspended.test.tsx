import { render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { BookingApiError } from "./types";

const getLocations = vi.fn();
vi.mock("./api", () => ({ bookingApi: { getLocations: (...a: unknown[]) => getLocations(...a) } }));

import { MOCK_SUSPENDED_TENANT, mockBookingApi } from "./api/mock-client";
import { httpBookingApi } from "./api/http-client";
import { BookingProviders } from "./components/BookingProviders";
import { LocationStep } from "./components/LocationStep";
import { isTenantSuspended } from "./errors";

afterEach(() => {
  getLocations.mockReset();
  vi.unstubAllGlobals();
});

const stubFetch = (status: number, body: unknown) =>
  vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response(JSON.stringify(body), { status })));

describe("ADR-018 D2: tenant_suspended", () => {
  it("http-client: 403 з кодом -> BookingApiError, що розпізнається як призупинення", async () => {
    stubFetch(403, { code: "tenant_suspended" });
    const err = await httpBookingApi.getLocations("salon").catch((e) => e);
    expect(err).toBeInstanceOf(BookingApiError);
    expect(isTenantSuspended(err)).toBe(true);
  });

  it("інший 403 і 404 не вважаються призупиненням", async () => {
    stubFetch(403, { code: "forbidden" });
    expect(isTenantSuspended(await httpBookingApi.getLocations("salon").catch((e) => e))).toBe(false);
    stubFetch(404, { code: "tenant_suspended" });
    expect(isTenantSuspended(await httpBookingApi.getLocations("salon").catch((e) => e))).toBe(false);
  });

  it("mock: demo-заклад призупинення віддає 403, звичайний - ні", async () => {
    const err = await mockBookingApi.getLocations(MOCK_SUSPENDED_TENANT).catch((e) => e);
    expect(isTenantSuspended(err)).toBe(true);
    expect(await mockBookingApi.getLocations("demo")).not.toHaveLength(0);
  });

  it("екран: заголовок із фокусом, без каталогу й технічних деталей", async () => {
    getLocations.mockRejectedValue(new BookingApiError(403, "tenant_suspended"));
    render(
      <BookingProviders tenant="salon">
        <LocationStep />
      </BookingProviders>,
    );
    const h = await screen.findByRole("heading", { name: "Запис тимчасово призупинено" });
    await waitFor(() => expect(h).toHaveFocus());
    expect(screen.getByRole("status")).toHaveTextContent("Зверніться до салону іншим способом.");
    expect(screen.queryByText(/tenant_suspended|403/)).toBeNull();
    expect(screen.queryByText(/Оберіть заклад/)).toBeNull();
    expect(getLocations).toHaveBeenCalledTimes(1);
  });

  it("інша помилка лишає звичайний екран кроку", async () => {
    getLocations.mockRejectedValue(new BookingApiError(403, "forbidden"));
    render(
      <BookingProviders tenant="salon">
        <LocationStep />
      </BookingProviders>,
    );
    expect(await screen.findByText(/Оберіть заклад/)).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Запис тимчасово призупинено" })).toBeNull();
  });
});
