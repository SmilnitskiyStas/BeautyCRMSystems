import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import type { StaffMember } from "../types";

const auth = { user: { id: "u", email: "e", fullName: "n", role: "admin", specialistId: null as string | null } };
vi.mock("next/navigation", () => ({ useRouter: () => ({ replace: vi.fn() }), usePathname: () => "/beauty/staff/s1" }));
vi.mock("@/features/beauty-auth/components/auth-provider", () => ({ useAuth: () => auth }));

const member: StaffMember = { id: "s1", name: "Марина", phone: null, position: null, isActive: true, services: [], locations: [] };
const count = vi.fn<(id: string) => Promise<number>>();
vi.mock("../api", () => ({
  beautyApi: {
    getStaffProfile: () => Promise.resolve(member),
    getUpcomingAppointmentsCount: (id: string) => count(id),
    getStaffInvites: () => Promise.resolve([]),
  },
}));

import { StaffProfileScreen } from "./staff-screens";

async function openConfirm() {
  const user = userEvent.setup();
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <StaffProfileScreen id="s1" />
    </QueryClientProvider>,
  );
  await user.click(await screen.findByRole("button", { name: "Деактивувати працівника" }));
}

describe("деактивація працівника: майбутні записи", () => {
  it("показує кількість майбутніх записів і посилання на календар майстра", async () => {
    count.mockResolvedValue(3);
    await openConfirm();
    const info = await screen.findByTestId("upcoming-info");
    expect(await within(info).findByText(/Майбутніх записів \(найближчі 60 днів\): 3/)).toBeInTheDocument();
    expect(within(info).getByRole("link", { name: "Відкрити календар" })).toHaveAttribute("href", "/beauty/calendar?specialist=s1");
    expect(count).toHaveBeenCalledWith("s1");
  });

  it("без майбутніх записів повідомляє про це", async () => {
    count.mockResolvedValue(0);
    await openConfirm();
    expect(await screen.findByText(/Майбутніх записів \(найближчі 60 днів\) немає/)).toBeInTheDocument();
  });
});
