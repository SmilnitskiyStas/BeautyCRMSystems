import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { LocationAnalytics } from "../types";

const getLocations = vi.fn();
const getLocationAnalytics = vi.fn<(id: string) => Promise<LocationAnalytics>>();
vi.mock("../api", () => ({
  beautyApi: {
    getLocations: () => getLocations(),
    getLocationAnalytics: (id: string) => getLocationAnalytics(id),
    getNetworkAnalytics: () => new Promise(() => {}),
  },
}));

import { AnalyticsScreen } from "./analytics-screen";

const data = (locationId: string): LocationAnalytics => ({ locationId, kpis: [{ label: "Виручка", value: "1 ₴", note: "" }], specialists: [], topServices: [] });

function renderScreen() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <AnalyticsScreen />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  getLocations.mockReset().mockResolvedValue([
    { id: "11111111-aaaa", name: "Центр" },
    { id: "22222222-bbbb", name: "Поділ" },
  ]);
  getLocationAnalytics.mockReset().mockImplementation(async (id) => data(id));
});

describe("F6: аналітика «Заклади» не стартує з вигаданого locationId", () => {
  it("початковий заклад = перший реальний; запит із ним, а не з «c» (інакше 422 від backend)", async () => {
    const user = userEvent.setup();
    renderScreen();
    await user.click(screen.getByRole("button", { name: "Заклади" }));
    await vi.waitFor(() => expect(getLocationAnalytics).toHaveBeenCalledWith("11111111-aaaa"));
    expect(getLocationAnalytics).not.toHaveBeenCalledWith("c");
    expect(await screen.findByRole("button", { name: "Центр" })).toHaveAttribute("aria-pressed", "true");
  });

  it("клік по іншому закладу вантажить його", async () => {
    const user = userEvent.setup();
    renderScreen();
    await user.click(screen.getByRole("button", { name: "Заклади" }));
    await user.click(await screen.findByRole("button", { name: "Поділ" }));
    await vi.waitFor(() => expect(getLocationAnalytics).toHaveBeenLastCalledWith("22222222-bbbb"));
  });

  it("немає жодного закладу - пояснення, а не запит і не нескінченне завантаження", async () => {
    getLocations.mockResolvedValue([]);
    const user = userEvent.setup();
    renderScreen();
    await user.click(screen.getByRole("button", { name: "Заклади" }));
    expect(await screen.findByText(/Закладів ще немає/)).toBeInTheDocument();
    expect(getLocationAnalytics).not.toHaveBeenCalled();
  });
});
