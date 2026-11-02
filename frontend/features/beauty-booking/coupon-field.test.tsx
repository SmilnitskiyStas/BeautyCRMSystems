import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { BookingApiError } from "./types";

const checkCoupon = vi.fn();
vi.mock("./api", () => ({ bookingApi: { checkCoupon: (...a: unknown[]) => checkCoupon(...a) } }));

import { mockBookingApi } from "./api/mock-client";
import { CouponField } from "./components/CouponField";
import { TenantContext } from "./components/tenant-context";
import { humanizeBookingError } from "./errors";
import { useBookingFlow } from "./hooks/useBookingFlow";

function renderField() {
  useBookingFlow.getState().reset();
  useBookingFlow.setState({
    location: { id: "c", name: "Центр", timezone: "Europe/Kyiv" },
    service: { id: "mn", name: "Манікюр", durationMinutes: 60, priceOriginal: 1000, priceFinal: 1000 },
  });
  return render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { mutations: { retry: false } } })}>
      <TenantContext.Provider value="demo">
        <CouponField />
      </TenantContext.Provider>
    </QueryClientProvider>,
  );
}

beforeEach(() => checkCoupon.mockReset());

describe("публічна форма: промокод", () => {
  it("застосовано: ціна до/після, запит з кодом, закладом і послугою", async () => {
    checkCoupon.mockResolvedValue({ outcome: "applied", priceOriginal: 1000, priceFinal: 850 });
    renderField();
    await userEvent.type(screen.getByLabelText(/Промокод/), " autumn15 ");
    await userEvent.click(screen.getByRole("button", { name: "Перевірити" }));
    expect(await screen.findByText(/Промокод застосовано: 850\D+замість 1\D*000/)).toBeInTheDocument();
    expect(checkCoupon).toHaveBeenCalledWith("demo", { code: "autumn15", locationId: "c", serviceId: "mn" });
    expect(useBookingFlow.getState().coupon?.result.priceFinal).toBe(850);
    // зміна коду скидає результат
    await userEvent.type(screen.getByLabelText(/Промокод/), "x");
    expect(useBookingFlow.getState().coupon).toBeNull();
    expect(screen.queryByText(/Промокод застосовано/)).toBeNull();
  });

  it("not_better і not_applicable - без обіцянок знижки", async () => {
    checkCoupon.mockResolvedValueOnce({ outcome: "not_better", priceOriginal: 1000, priceFinal: 800 });
    renderField();
    await userEvent.type(screen.getByLabelText(/Промокод/), "AUTUMN15");
    await userEvent.click(screen.getByRole("button", { name: "Перевірити" }));
    expect(await screen.findByText(/діє вигідніша знижка/)).toBeInTheDocument();
    checkCoupon.mockResolvedValueOnce({ outcome: "not_applicable", priceOriginal: 1000, priceFinal: 1000 });
    await userEvent.click(screen.getByRole("button", { name: "Перевірити" }));
    expect(await screen.findByText(/не діє на обрану послугу/)).toBeInTheDocument();
  });

  it("coupon_invalid: одна однакова відповідь; формат перевіряється до запиту; 429", async () => {
    checkCoupon.mockRejectedValueOnce(new BookingApiError(404, "coupon_invalid"));
    renderField();
    await userEvent.type(screen.getByLabelText(/Промокод/), "ab");
    await userEvent.click(screen.getByRole("button", { name: "Перевірити" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("Перевірте код: від 4 до 20 літер і цифр.");
    expect(checkCoupon).not.toHaveBeenCalled();
    await userEvent.type(screen.getByLabelText(/Промокод/), "cdef");
    await userEvent.click(screen.getByRole("button", { name: "Перевірити" }));
    await waitFor(() => expect(screen.getByRole("alert")).toHaveTextContent("Код не підходить: він недійсний, використаний або вже не діє."));
    expect(screen.getByRole("alert").textContent).not.toMatch(/персональн/i);
    checkCoupon.mockRejectedValueOnce(new BookingApiError(429, "rate_limited", "x", 60));
    await userEvent.click(screen.getByRole("button", { name: "Перевірити" }));
    await waitFor(() => expect(screen.getByRole("alert")).toHaveTextContent(/Забагато спроб/));
  });

  it("підказка не згадує персональні коди", () => {
    renderField();
    expect(screen.getByText(/Є промокод закладу/).textContent).not.toMatch(/персональн/i);
  });

  it("mock: загальний код діє, інші (зокрема схожі на персональні) - однакове coupon_invalid", async () => {
    const ok = await mockBookingApi.checkCoupon("demo", { code: "autumn-15", locationId: "c", serviceId: "pd" });
    expect(ok).toMatchObject({ outcome: "applied", priceOriginal: 1000, priceFinal: 850 });
    for (const code of ["Q7W2K-9XM3D", "NOPE1234", "OLD2025"]) {
      await expect(mockBookingApi.checkCoupon("demo", { code, locationId: "c", serviceId: "pd" })).rejects.toMatchObject({ status: 404, code: "coupon_invalid" });
    }
    expect(humanizeBookingError(new BookingApiError(409, "coupon_invalid"))).toMatch(/Код не підходить/);
  });
});
