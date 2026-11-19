import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const h = vi.hoisted(() => ({
  api: {} as Record<string, (...a: unknown[]) => unknown>,
  auth: { user: { id: "u", email: "e", fullName: "Демо", role: "owner" }, logout: async () => {} },
}));

vi.mock("next/navigation", () => ({ usePathname: () => "/beauty/calendar", useRouter: () => ({ replace: vi.fn() }), useSearchParams: () => new URLSearchParams() }));
vi.mock("@/features/beauty-auth/components/auth-provider", () => ({ useAuth: () => h.auth, AuthGate: ({ children }: { children: React.ReactNode }) => children }));
vi.mock("../api", () => ({
  beautyApi: new Proxy({}, { get: (_t, k: string) => (...a: unknown[]) => h.api[k](...a) }),
}));

import { SideNav } from "./admin-shell";

let wide = true;
function setupMatchMedia() {
  window.matchMedia = ((q: string) => ({ matches: wide, media: q, addEventListener: () => {}, removeEventListener: () => {} })) as unknown as typeof window.matchMedia;
}

function renderNav(role: string) {
  h.auth.user.role = role;
  h.api = { getConversations: vi.fn(async () => ({ items: [], meta: { needsReplyCount: 0 } })), sendFeedback: vi.fn(async () => ({ id: "f" })) };
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <SideNav />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  wide = true;
  window.localStorage.clear();
  setupMatchMedia();
});
afterEach(() => {
  // @ts-expect-error прибираємо мок
  delete window.matchMedia;
});

describe("точка входу в звернення до розробників", () => {
  it.each(["owner", "admin", "specialist"])("роль %s бачить кнопку й відкриває форму", (role) => {
    renderNav(role);
    const btn = screen.getByRole("button", { name: "Звернення до розробників" });
    expect(btn).toHaveAttribute("aria-haspopup", "dialog");
    btn.focus();
    fireEvent.click(btn);
    expect(screen.getByRole("dialog", { name: "Звернення до розробників" })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Закрити" }));
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(btn).toHaveFocus();
  });

  it("вузький екран: з меню-шухляди закриває шухляду й відкриває форму", () => {
    wide = false;
    setupMatchMedia();
    renderNav("specialist");
    fireEvent.click(screen.getByRole("button", { name: "Меню" }));
    const drawer = screen.getByRole("dialog", { name: "Головне меню" });
    fireEvent.click(within(drawer).getByRole("button", { name: "Звернення до розробників" }));
    expect(screen.queryByRole("dialog", { name: "Головне меню" })).not.toBeInTheDocument();
    expect(screen.getByRole("dialog", { name: "Звернення до розробників" })).toBeInTheDocument();
  });
});
