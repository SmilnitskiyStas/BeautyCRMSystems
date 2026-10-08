import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, within } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import type { ClientProfile } from "../types";

const profile: ClientProfile = {
  id: "c1", name: "Олена Кравченко", tag: "vip", contactLine: "+380", kpis: [], promos: [], notes: [], preferences: [], warning: null,
  loyalty: { balance: "—", nextLevel: "", progressPct: 0 }, cancelledCount: 3, cancelledByClientCount: 2,
  visits: [
    { id: "v1", dateLabel: "5 жовт., 10:00", serviceName: "Манікюр", specialistId: "m", specialistName: "Марина", locationName: "Центр", sum: "900 ₴", status: "completed", viaPromo: false },
    { id: "v2", dateLabel: "1 жовт., 10:00", serviceName: "Стрижка", specialistId: "m", specialistName: "Марина", locationName: "Центр", sum: "650 ₴", status: "cancelled", viaPromo: false, cancelledAtLabel: "30 вер., 18:00", cancelledBy: { type: "client" }, cancelReason: "Змінились плани" },
    { id: "v3", dateLabel: "2 жовт., 10:00", serviceName: "Педикюр", specialistId: "m", specialistName: "Марина", locationName: "Центр", sum: "1 000 ₴", status: "cancelled", viaPromo: false, cancelledAtLabel: "1 жовт., 09:00", cancelledBy: { type: "staff", name: "Світлана К." } },
    { id: "v4", dateLabel: "3 жовт., 10:00", serviceName: "Фарбування", specialistId: "m", specialistName: "Марина", locationName: "Центр", sum: "2 400 ₴", status: "cancelled", viaPromo: false, cancelledBy: { type: "system" } },
  ],
};
vi.mock("../api", () => ({ beautyApi: { getClient: () => Promise.resolve(profile) } }));

import { ClientProfileScreen } from "./clients-screens";

describe("картка клієнта: скасовані візити (розділ 16)", () => {
  it("показує лічильники, хто й коли скасував, причину лише якщо вона є", async () => {
    render(
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <ClientProfileScreen id="c1" />
      </QueryClientProvider>,
    );
    expect(await screen.findByText("з них скасував клієнт: 2")).toBeInTheDocument();
    expect(screen.getByText("3", { selector: "div" })).toBeInTheDocument();
    const infos = screen.getAllByTestId("visit-cancel-info");
    expect(infos).toHaveLength(3);
    expect(within(infos[0]).getByText("Скасував: клієнт")).toBeInTheDocument();
    expect(within(infos[0]).getByText("30 вер., 18:00")).toBeInTheDocument();
    expect(within(infos[0]).getByText("Причина: Змінились плани")).toBeInTheDocument();
    expect(within(infos[1]).getByText("Скасував: адміністратор (Світлана К.)")).toBeInTheDocument();
    expect(within(infos[1]).queryByText(/Причина/)).not.toBeInTheDocument();
    expect(within(infos[2]).getByText("Скасував: система")).toBeInTheDocument();
  });
});
