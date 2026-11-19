import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const h = vi.hoisted(() => ({
  api: {} as Record<string, (...a: unknown[]) => unknown>,
  path: "/beauty/inbox",
  auth: { user: { id: "u", email: "e", fullName: "Демо", role: "owner" }, logout: async () => {} },
}));

vi.mock("next/navigation", () => ({ usePathname: () => h.path, useRouter: () => ({ replace: vi.fn() }), useSearchParams: () => new URLSearchParams() }));
vi.mock("@/features/beauty-auth/components/auth-provider", () => ({ useAuth: () => h.auth, AuthGate: ({ children }: { children: React.ReactNode }) => children }));
vi.mock("../api", () => ({
  beautyApi: new Proxy({}, { get: (_t, k: string) => (...a: unknown[]) => h.api[k](...a) }),
}));

import { SideNav } from "./admin-shell";

let wide = false;
const listeners = new Set<() => void>();

function setupMatchMedia() {
  window.matchMedia = ((q: string) => ({
    matches: wide,
    media: q,
    addEventListener: (_: string, cb: () => void) => listeners.add(cb),
    removeEventListener: (_: string, cb: () => void) => listeners.delete(cb),
  })) as unknown as typeof window.matchMedia;
}

function renderNav(role = "owner", count = 0) {
  h.auth.user.role = role;
  h.api = { getConversations: vi.fn(async () => ({ items: [], meta: { needsReplyCount: count } })) };
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const view = render(
    <QueryClientProvider client={qc}>
      <SideNav />
    </QueryClientProvider>,
  );
  return view;
}

beforeEach(() => {
  wide = false;
  h.path = "/beauty/inbox";
  window.localStorage.clear();
  document.body.style.overflow = "";
  setupMatchMedia();
});

afterEach(() => {
  // @ts-expect-error прибираємо мок
  delete window.matchMedia;
  listeners.clear();
});

describe("адаптивне меню (TASK-797)", () => {
  it("вузький екран: кнопка «Меню» з aria, панель закрита, діалогу немає", () => {
    renderNav();
    const btn = screen.getByRole("button", { name: "Меню" });
    expect(btn).toHaveAttribute("aria-haspopup", "dialog");
    expect(btn).toHaveAttribute("aria-expanded", "false");
    expect(btn).toHaveAttribute("aria-controls");
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(screen.queryByRole("navigation", { name: "Головне меню" })).not.toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Запис клієнта" })).not.toBeInTheDocument();
  });

  it("відкриття: role=dialog aria-modal, aria-controls вказує на панель, фокус на «Закрити», скрол body заблоковано", () => {
    renderNav();
    const btn = screen.getByRole("button", { name: "Меню" });
    fireEvent.click(btn);
    const dlg = screen.getByRole("dialog", { name: "Головне меню" });
    expect(dlg).toHaveAttribute("aria-modal", "true");
    expect(btn).toHaveAttribute("aria-expanded", "true");
    expect(btn.getAttribute("aria-controls")).toBe(dlg.id);
    expect(within(dlg).getByRole("link", { name: "Запис клієнта" })).toBeInTheDocument();
    expect(within(dlg).getByRole("button", { name: "Щоденна робота" })).toBeInTheDocument();
    expect(within(dlg).getByRole("button", { name: "Вийти" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Закрити" })).toHaveFocus();
    expect(document.body.style.overflow).toBe("hidden");
  });

  it("закриття кнопкою «Закрити»: фокус повертається на «Меню», скрол відновлено", () => {
    document.body.style.overflow = "auto";
    renderNav();
    const btn = screen.getByRole("button", { name: "Меню" });
    fireEvent.click(btn);
    fireEvent.click(screen.getByRole("button", { name: "Закрити" }));
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(btn).toHaveFocus();
    expect(document.body.style.overflow).toBe("auto");
  });

  it("Esc закриває", () => {
    renderNav();
    fireEvent.click(screen.getByRole("button", { name: "Меню" }));
    fireEvent.keyDown(document, { key: "Escape" });
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Меню" })).toHaveFocus();
  });

  it("клік по backdrop закриває", () => {
    renderNav();
    fireEvent.click(screen.getByRole("button", { name: "Меню" }));
    fireEvent.click(screen.getByTestId("menu-backdrop"));
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });

  it("клік по пункту меню закриває", () => {
    renderNav();
    fireEvent.click(screen.getByRole("button", { name: "Меню" }));
    const dlg = screen.getByRole("dialog");
    fireEvent.click(within(dlg).getByRole("link", { name: "Запис клієнта" }));
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });

  it("зміна маршруту закриває", () => {
    const view = renderNav();
    fireEvent.click(screen.getByRole("button", { name: "Меню" }));
    h.path = "/beauty/channels";
    view.rerender(
      <QueryClientProvider client={new QueryClient()}>
        <SideNav />
      </QueryClientProvider>,
    );
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });

  it("trap фокуса: Tab з останнього елемента веде на перший, Shift+Tab з першого - на останній", () => {
    renderNav();
    fireEvent.click(screen.getByRole("button", { name: "Меню" }));
    const dlg = screen.getByRole("dialog");
    const items = [...dlg.querySelectorAll<HTMLElement>('a[href], button:not([disabled])')];
    const first = items[0];
    const last = items[items.length - 1];
    last.focus();
    fireEvent.keyDown(last, { key: "Tab" });
    expect(first).toHaveFocus();
    fireEvent.keyDown(first, { key: "Tab", shiftKey: true });
    expect(last).toHaveFocus();
  });

  it("індикатор «потрібна відповідь» видно на кнопці «Меню», коли меню закрите", async () => {
    renderNav("owner", 3);
    await waitFor(() => expect(screen.getByTestId("menu-button-dot")).toBeInTheDocument());
    expect(screen.getByRole("button", { name: /Меню.*потрібна відповідь: 3/ })).toBeInTheDocument();
  });

  it("specialist: у діалозі плоский список без блоків і без «Запису клієнта»", () => {
    renderNav("specialist");
    fireEvent.click(screen.getByRole("button", { name: "Меню" }));
    const dlg = screen.getByRole("dialog");
    expect(within(dlg).getByRole("link", { name: "Записи" })).toBeInTheDocument();
    expect(within(dlg).queryByRole("button", { name: /Щоденна|Каталог|Аналітика|Система/ })).not.toBeInTheDocument();
    expect(within(dlg).queryByRole("link", { name: "Запис клієнта" })).not.toBeInTheDocument();
  });

  it("широкий екран: постійний nav «Головне меню», без діалогу", () => {
    wide = true;
    renderNav();
    expect(screen.getByRole("navigation", { name: "Головне меню" })).toBeInTheDocument();
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Закрити" })).not.toBeInTheDocument();
  });

  it("перехід на широкий екран при відкритому меню знімає блокування скролу", () => {
    renderNav();
    fireEvent.click(screen.getByRole("button", { name: "Меню" }));
    expect(document.body.style.overflow).toBe("hidden");
    wide = true;
    act(() => listeners.forEach((l) => l()));
    expect(document.body.style.overflow).toBe("");
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(screen.getByRole("navigation", { name: "Головне меню" })).toBeInTheDocument();
  });
});
