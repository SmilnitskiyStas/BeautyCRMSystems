import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

const replace = vi.fn();
const auth = { user: { id: "u", email: "e", fullName: "n", role: "specialist", specialistId: "spec-1" as string | null } };

vi.mock("next/navigation", () => ({
  useRouter: () => ({ replace }),
  usePathname: () => "/beauty/staff",
  useSearchParams: () => new URLSearchParams(),
}));
vi.mock("@/features/beauty-auth/components/auth-provider", () => ({ useAuth: () => auth }));
vi.mock("../api", () => ({
  beautyApi: {
    getStaff: () => Promise.resolve([]),
    getAbsences: () => Promise.resolve([]),
    getLocations: () => Promise.resolve([]),
    getServices: () => Promise.resolve([]),
  },
}));

import { StaffListScreen } from "./staff-screens";

function renderList() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <StaffListScreen />
    </QueryClientProvider>,
  );
}

describe("redirect specialist зі списку працівників", () => {
  beforeEach(() => replace.mockClear());

  it("specialist перенаправляється на власний профіль", async () => {
    auth.user.role = "specialist";
    auth.user.specialistId = "spec-1";
    renderList();
    await waitFor(() => expect(replace).toHaveBeenCalledWith("/beauty/staff/spec-1"));
    expect(screen.getByRole("status")).toHaveTextContent(/профіль/);
  });

  it("specialist без привʼязаного профілю - на календар", async () => {
    auth.user.role = "specialist";
    auth.user.specialistId = null;
    renderList();
    await waitFor(() => expect(replace).toHaveBeenCalledWith("/beauty/calendar"));
  });

  it("owner не перенаправляється", async () => {
    auth.user.role = "owner";
    renderList();
    await new Promise((r) => setTimeout(r, 20));
    expect(replace).not.toHaveBeenCalled();
  });
});
