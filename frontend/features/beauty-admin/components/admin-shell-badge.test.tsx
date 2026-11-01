import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

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

function renderNav(role: string, count: number, extra: Record<string, (...a: unknown[]) => unknown> = {}) {
  h.auth.user.role = role;
  h.api = {
    getConversations: vi.fn(async () => ({ items: [], meta: { needsReplyCount: count } })),
    ...extra,
  };
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={qc}>
      <SideNav />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  h.path = "/beauty/inbox";
  window.localStorage.clear();
});

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

describe("групування меню (TASK-796)", () => {
  it("owner: 4 заголовки-кнопки з aria-expanded/aria-controls, «Запис клієнта» над блоками", () => {
    renderNav("owner", 0);
    const nav = screen.getByRole("navigation", { name: "Головне меню" });
    for (const name of ["Щоденна робота", "Каталог і продажі", "Аналітика та AI", "Система"]) {
      const btn = within(nav).getByRole("button", { name });
      expect(btn).toHaveAttribute("aria-expanded");
      expect(document.getElementById(btn.getAttribute("aria-controls")!)).not.toBeNull();
    }
    const book = within(nav).getByRole("link", { name: "Запис клієнта" });
    const first = within(nav).getByRole("button", { name: "Щоденна робота" });
    expect(book.compareDocumentPosition(first) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it("згортання/розгортання зберігається в localStorage; активний блок не згортається", () => {
    renderNav("owner", 0);
    const sys = screen.getByRole("button", { name: "Система" });
    expect(sys).toHaveAttribute("aria-expanded", "true");
    fireEvent.click(sys);
    expect(sys).toHaveAttribute("aria-expanded", "false");
    expect(screen.queryByRole("link", { name: "Канали" })).not.toBeInTheDocument();
    expect(JSON.parse(window.localStorage.getItem("beauty-nav-collapsed")!)).toEqual(["system"]);
    fireEvent.click(sys);
    expect(sys).toHaveAttribute("aria-expanded", "true");
    expect(screen.getByRole("link", { name: "Канали" })).toBeInTheDocument();
    const daily = screen.getByRole("button", { name: "Щоденна робота" });
    fireEvent.click(daily);
    expect(daily).toHaveAttribute("aria-expanded", "true");
  });

  it("збережений згорнутий стан застосовується, але активний блок авторозгортається", async () => {
    window.localStorage.setItem("beauty-nav-collapsed", JSON.stringify(["daily", "system"]));
    h.path = "/beauty/channels";
    renderNav("owner", 0);
    await waitFor(() => expect(screen.getByRole("button", { name: "Щоденна робота" })).toHaveAttribute("aria-expanded", "false"));
    expect(screen.getByRole("button", { name: "Система" })).toHaveAttribute("aria-expanded", "true");
    expect(screen.getByRole("link", { name: "Канали" })).toHaveAttribute("aria-current", "page");
  });

  it("згорнутий блок із «Розмови» показує індикатор непрочитаних на заголовку", async () => {
    window.localStorage.setItem("beauty-nav-collapsed", JSON.stringify(["daily"]));
    h.path = "/beauty/channels";
    renderNav("owner", 4);
    await waitFor(() => expect(screen.getByTestId("inbox-group-dot")).toBeInTheDocument());
    expect(screen.getByRole("button", { name: /Щоденна робота.*потрібна відповідь: 4/ })).toBeInTheDocument();
  });

  it("marketing disabled: «Розсилки» прихована, решта блоку лишається", async () => {
    renderNav("owner", 0, { getMarketingSettings: vi.fn(async () => { throw Object.assign(new Error("x"), { status: 403, code: "marketing_disabled" }); }) });
    expect(screen.getByRole("button", { name: "Каталог і продажі" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Ціни та акції" })).toBeInTheDocument();
  });

  it("specialist: плоский список без заголовків і без «Запису клієнта»", () => {
    renderNav("specialist", 0);
    expect(screen.queryAllByRole("button", { name: /Щоденна|Каталог|Аналітика|Система/ })).toHaveLength(0);
    expect(screen.getByRole("link", { name: "Записи" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Мій профіль" })).toHaveAttribute("href", "/beauty/staff");
    expect(screen.queryByRole("link", { name: "Запис клієнта" })).not.toBeInTheDocument();
  });
});
