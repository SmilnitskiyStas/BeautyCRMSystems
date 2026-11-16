import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

const h = vi.hoisted(() => ({ api: {} as Record<string, (...a: unknown[]) => unknown> }));
vi.mock("../api", () => ({ beautyApi: new Proxy({}, { get: (_t, k: string) => (...a: unknown[]) => h.api[k]!(...a) }) }));

import { BeautyApiError } from "@/features/beauty-auth/errors";
import { MyRevenueCard } from "./my-revenue-card";

function renderCard() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={qc}>
      <MyRevenueCard />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  h.api = {
    getMyRevenue: vi.fn(async (q: unknown) => ({
      fromDate: (q as { from: string }).from, toDate: (q as { to: string }).to, specialistId: "m", completed: 3, revenue: 1950,
      byLocation: [{ locationId: "c", name: "Центр", completed: 3, revenue: 1950 }],
    })),
  };
});

describe("«Мій виторг»", () => {
  it("показує кількість візитів і суму за замовчуванням за місяць; перемикання періоду робить новий запит", async () => {
    const user = userEvent.setup();
    renderCard();
    expect(await screen.findByTestId("my-revenue-count")).toHaveTextContent("3");
    expect(screen.getByTestId("my-revenue-sum")).toHaveTextContent("1 950");
    expect(screen.getByText(/Без собівартості й виплат/)).toBeInTheDocument();
    const calls = (h.api.getMyRevenue as ReturnType<typeof vi.fn>).mock.calls.length;
    await user.click(screen.getByRole("button", { name: "Минулий місяць" }));
    await screen.findByTestId("my-revenue");
    expect((h.api.getMyRevenue as ReturnType<typeof vi.fn>).mock.calls.length).toBeGreaterThan(calls);
  });

  it("без прив'язки до майстра - пояснення, а не вигадані цифри", async () => {
    h.api.getMyRevenue = vi.fn(async () => ({ fromDate: "a", toDate: "b", specialistId: null, completed: 0, revenue: 0, byLocation: [] }));
    renderCard();
    expect(await screen.findByRole("note")).toHaveTextContent("не прив'язано");
  });

  it("помилка API показується повідомленням", async () => {
    h.api.getMyRevenue = vi.fn(async () => {
      throw new BeautyApiError(403, "forbidden_role", "x");
    });
    renderCard();
    expect(await screen.findByRole("alert")).toBeInTheDocument();
  });
});
