import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import type { StaffMember } from "../types";

const auth = { user: { id: "u", email: "e", fullName: "n", role: "owner", specialistId: null as string | null } };
vi.mock("next/navigation", () => ({
  useRouter: () => ({ replace: vi.fn() }),
  usePathname: () => "/beauty/staff",
  useSearchParams: () => new URLSearchParams(),
}));
vi.mock("@/features/beauty-auth/components/auth-provider", () => ({ useAuth: () => auth }));

const member: StaffMember = {
  id: "s1",
  name: "Марина Бойко",
  phone: null,
  position: "Майстер",
  isActive: true,
  services: [],
  locations: [
    { locationId: "c", locationName: "Центр", workingHours: {}, isActive: true },
    { locationId: "p", locationName: "Поділ", workingHours: {}, isActive: false },
  ],
};
vi.mock("../api", () => ({
  beautyApi: {
    getStaff: () => Promise.resolve([member]),
    getAbsences: () => Promise.resolve([]),
    getLocations: () => Promise.resolve([]),
    getServices: () => Promise.resolve([]),
  },
}));

import { StaffListScreen } from "./staff-screens";

describe("F5: список працівників не показує знятий зв'язок із закладом як чинний", () => {
  it("у картці лише активні заклади", async () => {
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={qc}>
        <StaffListScreen />
      </QueryClientProvider>,
    );
    const label = await screen.findByText("Заклади");
    expect(label.nextSibling).toHaveTextContent("Центр");
    expect(label.nextSibling).not.toHaveTextContent("Поділ");
  });
});
