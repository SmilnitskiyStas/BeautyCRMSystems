import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor, within } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

const h = vi.hoisted(() => ({
  api: {} as Record<string, (...a: unknown[]) => unknown>,
  auth: { user: { id: "u", email: "e", fullName: "Демо", role: "owner" } },
}));

vi.mock("@/features/beauty-auth/components/auth-provider", () => ({ useAuth: () => h.auth }));
vi.mock("../api", () => ({
  beautyApi: new Proxy({}, { get: (_t, k: string) => (...a: unknown[]) => h.api[k](...a) }),
}));

import { OnboardingChecklist } from "./onboarding-checklist";

function setup(role: string, data: { locations: unknown[]; staff: unknown[]; services: unknown[] }) {
  h.auth.user.role = role;
  h.api = {
    getLocations: vi.fn(async () => data.locations),
    getStaff: vi.fn(async () => data.staff),
    getServices: vi.fn(async () => data.services),
  };
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={qc}>
      <OnboardingChecklist />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
});

describe("«З чого почати»", () => {
  it("нічого немає: три невиконані кроки з посиланнями", async () => {
    setup("owner", { locations: [], staff: [], services: [] });
    expect(await screen.findByRole("heading", { name: "З чого почати" })).toBeInTheDocument();
    const items = within(screen.getByRole("list")).getAllByRole("listitem");
    expect(items).toHaveLength(3);
    expect(screen.getByText("Виконано кроків: 0 з 3")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Перейти до закладів" })).toHaveAttribute("href", "/beauty/locations");
    expect(screen.getByRole("link", { name: "Перейти до спеціалістів" })).toHaveAttribute("href", "/beauty/staff");
    expect(screen.getByRole("link", { name: "Перейти до послуг" })).toHaveAttribute("href", "/beauty/services");
  });

  it("лише заклад: перший крок виконано, решта - ні", async () => {
    setup("admin", { locations: [{ id: "l1" }], staff: [], services: [] });
    await screen.findByRole("heading", { name: "З чого почати" });
    const items = screen.getAllByRole("listitem");
    expect(items[0]).toHaveTextContent("(виконано)");
    expect(items[1]).toHaveTextContent("(не виконано)");
    expect(screen.getByText("Виконано кроків: 1 з 3")).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Перейти до закладів" })).not.toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Перейти до спеціалістів" })).toBeInTheDocument();
  });

  it("усе є: блоку немає", async () => {
    setup("owner", { locations: [{ id: "l1" }], staff: [{ id: "s1" }], services: [{ id: "v1" }] });
    await waitFor(() => expect(h.api.getServices).toHaveBeenCalled());
    await new Promise((r) => setTimeout(r, 30));
    expect(screen.queryByRole("heading", { name: "З чого почати" })).not.toBeInTheDocument();
  });

  it("specialist: блоку немає й запитів немає", async () => {
    setup("specialist", { locations: [], staff: [], services: [] });
    await new Promise((r) => setTimeout(r, 30));
    expect(screen.queryByRole("heading", { name: "З чого почати" })).not.toBeInTheDocument();
    expect(h.api.getLocations).not.toHaveBeenCalled();
    expect(h.api.getStaff).not.toHaveBeenCalled();
    expect(h.api.getServices).not.toHaveBeenCalled();
  });
});
