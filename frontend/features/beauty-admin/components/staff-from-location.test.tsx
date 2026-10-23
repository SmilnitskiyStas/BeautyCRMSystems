import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { StaffMember } from "../types";

let query = "";
const auth = { user: { id: "u", email: "e", fullName: "n", role: "admin", specialistId: null as string | null } };
vi.mock("next/navigation", () => ({
  useRouter: () => ({ replace: vi.fn() }),
  usePathname: () => "/beauty/staff/s1",
  useSearchParams: () => new URLSearchParams(query),
}));
vi.mock("@/features/beauty-auth/components/auth-provider", () => ({ useAuth: () => auth }));

const member: StaffMember = { id: "s1", name: "Марина", phone: null, position: null, isActive: true, services: [], locations: [] };
vi.mock("../api", () => ({
  beautyApi: {
    getStaffProfile: () => Promise.resolve(member),
    getUpcomingAppointmentsCount: () => Promise.resolve(0),
    getStaffInvites: () => Promise.resolve([]),
  },
}));

import { StaffProfileScreen } from "./staff-screens";

function renderProfile() {
  return render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <StaffProfileScreen id="s1" />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  query = "";
  auth.user.role = "admin";
});

describe("профіль спеціаліста: перехід із закладу (§18.3)", () => {
  it("?from=location&locationId=... показує «← Заклад» з посиланням на заклад", async () => {
    query = "from=location&locationId=c%2F1";
    renderProfile();
    expect(await screen.findByRole("link", { name: "← Заклад" })).toHaveAttribute("href", "/beauty/locations/c%2F1");
    expect(screen.getByRole("link", { name: "← Усі спеціалісти" })).toBeInTheDocument();
  });

  it("без query або без locationId - посилання немає", async () => {
    renderProfile();
    await screen.findByRole("heading", { level: 1, name: "Марина" });
    expect(screen.queryByRole("link", { name: "← Заклад" })).not.toBeInTheDocument();
  });

  it("from=location без locationId - посилання немає", async () => {
    query = "from=location";
    renderProfile();
    await screen.findByRole("heading", { level: 1, name: "Марина" });
    expect(screen.queryByRole("link", { name: "← Заклад" })).not.toBeInTheDocument();
  });
});
