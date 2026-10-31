import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor, within } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

const h = vi.hoisted(() => ({
  api: {} as Record<string, (...a: unknown[]) => unknown>,
  auth: { user: { id: "u", email: "e", fullName: "Демо", role: "owner" }, logout: async () => {} },
}));

vi.mock("next/navigation", () => ({ usePathname: () => "/beauty/inbox", useRouter: () => ({ replace: vi.fn() }), useSearchParams: () => new URLSearchParams() }));
vi.mock("@/features/beauty-auth/components/auth-provider", () => ({ useAuth: () => h.auth, AuthGate: ({ children }: { children: React.ReactNode }) => children }));
vi.mock("../api", () => ({
  beautyApi: new Proxy({}, { get: (_t, k: string) => (...a: unknown[]) => h.api[k](...a) }),
}));

import { SideNav } from "./admin-shell";

function renderNav(role: string, count: number) {
  h.auth.user.role = role;
  h.api = { getConversations: vi.fn(async () => ({ items: [], meta: { needsReplyCount: count } })) };
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={qc}>
      <SideNav />
    </QueryClientProvider>,
  );
}

beforeEach(() => vi.clearAllMocks());

describe("пункт меню «Розмови» з бейджем", () => {
  it("owner/admin: бейдж із кількістю «потрібна відповідь» і підписом для скрінрідера", async () => {
    for (const role of ["owner", "admin"]) {
      renderNav(role, 3);
      const link = screen.getByRole("link", { name: /Розмови/ });
      expect(link).toHaveAttribute("href", "/beauty/inbox");
      expect(link).toHaveAttribute("aria-current", "page");
      await waitFor(() => expect(within(link).getByTestId("inbox-badge")).toHaveTextContent("3"));
      expect(link).toHaveAccessibleName("Розмови, потрібна відповідь: 3");
      expect(screen.getByRole("link", { name: "Дзвінки" })).toHaveAttribute("href", "/beauty/calls");
      document.body.innerHTML = "";
    }
  });

  it("без розмов, що чекають, бейджа немає", async () => {
    renderNav("owner", 0);
    await waitFor(() => expect(h.api.getConversations).toHaveBeenCalled());
    expect(screen.queryByTestId("inbox-badge")).not.toBeInTheDocument();
  });

  it("specialist: ні пункту, ні запиту до inbox", async () => {
    renderNav("specialist", 5);
    expect(screen.queryByRole("link", { name: /Розмови/ })).not.toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Дзвінки" })).not.toBeInTheDocument();
    await new Promise((r) => setTimeout(r, 30));
    expect(h.api.getConversations).not.toHaveBeenCalled();
  });
});
