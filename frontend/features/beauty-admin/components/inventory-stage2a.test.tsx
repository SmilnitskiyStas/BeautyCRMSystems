import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// Фіксований «зараз» (14.10.2026 12:00 Київ); реальний годинник не використовується.
vi.hoisted(() => {
  vi.useFakeTimers({ toFake: ["Date"], now: new Date("2026-10-14T09:00:00Z") });
});

vi.mock("next/navigation", () => ({ useRouter: () => ({ push: vi.fn(), replace: vi.fn() }), usePathname: () => "/beauty/inventory/alerts" }));

import { ROLE_PRESETS } from "@/features/beauty-auth/access";
import { DEMO_LOCATIONS } from "../../beauty-shared/demo-seed";
import { createMockInventory } from "../api/mock-inventory";
import { createMockInventoryV2 } from "../api/mock-inventory-v2";

const auth = vi.hoisted(() => ({ user: { id: "u", email: "e", fullName: "n", role: "owner", specialistId: null as string | null, permissions: undefined as Record<string, string> | undefined, modules: undefined as string[] | undefined } }));
vi.mock("@/features/beauty-auth/components/auth-provider", () => ({ useAuth: () => auth }));

const state = vi.hoisted(() => ({ api: null as unknown }));
vi.mock("../api", () => ({ beautyApi: new Proxy({}, { get: (_t, k) => (state.api as Record<string | symbol, unknown>)[k] }) }));

import { InventoryBadge } from "./admin-shell";
import { InventoryAlertsScreen } from "./inventory-alerts-screen";
import { InventoryStockScreen } from "./inventory-stock-screen";
import { ServiceMaterialsEditor } from "./service-materials-editor";

const SERVICES = [
  { id: "s1", name: "Манікюр" },
  { id: "s2", name: "Стрижка" },
];

function setRole(role: "owner" | "admin" | "deputy" | "specialist") {
  auth.user.role = role;
  auth.user.permissions = { ...ROLE_PRESETS[role] };
  auth.user.specialistId = role === "specialist" ? "m" : null;
  auth.user.modules = undefined;
}

function renderScreen(ui: ReactElement) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(<QueryClientProvider client={qc}>{ui}</QueryClientProvider>);
}

beforeEach(() => {
  setRole("owner");
  const inv = createMockInventory({ role: () => auth.user.role, locations: () => DEMO_LOCATIONS, specialistLocationIds: () => ["c"], now: () => Date.now(), wait: false });
  const v2 = createMockInventoryV2({
    role: () => auth.user.role,
    services: () => SERVICES,
    locations: () => DEMO_LOCATIONS,
    specialistLocationIds: () => ["c"],
    inventory: inv,
    now: () => Date.now(),
  });
  state.api = { ...inv, ...v2, getLocations: async () => DEMO_LOCATIONS };
});
afterEach(() => cleanup());

describe("залишки: нестача й сальдо (§32.17)", () => {
  it("мінус позначено текстом, а не лише кольором; бейдж «У мінусі»", async () => {
    const user = userEvent.setup();
    renderScreen(<InventoryStockScreen />);
    await user.click(await screen.findByRole("button", { name: DEMO_LOCATIONS.find((l) => l.id === "p")!.name }));
    const note = await screen.findByTestId("stock-shortage");
    expect(note.textContent).toMatch(/Нестача: 0,3 л/);
    expect(note.textContent).toMatch(/Сальдо: −0,3 л \(у мінусі\)/);
    expect(screen.getAllByText("У мінусі").length).toBeGreaterThan(0);
  });
});

describe("екран сповіщень складу (§32.19)", () => {
  it("список, a11y таблиці, позначити прочитаним з озвученням, позначити всі", async () => {
    const user = userEvent.setup();
    renderScreen(<InventoryAlertsScreen />);
    const table = await screen.findByRole("table", { name: "Сповіщення складу" });
    expect(screen.getByRole("region", { name: "Сповіщення складу" }).getAttribute("tabindex")).toBe("0");
    const rows = within(table).getAllByTestId("alert-row");
    expect(rows.length).toBeGreaterThan(2);
    // стан не лише кольором: текст «Нове» і вид сповіщення
    expect(within(rows[0]!).getByText("Нове")).toBeInTheDocument();
    expect(within(table).getAllByText("Нестача").length).toBeGreaterThan(0);

    const before = rows.length;
    const btn = within(rows[0]!).getByRole("button", { name: /^Позначити прочитаним:/ });
    await user.click(btn);
    expect(await screen.findByText(/^Позначено прочитаним:/)).toBeInTheDocument();
    // «активні» лишаються (read ще активне), але кнопки в першого рядка вже немає
    expect(within(await screen.findAllByTestId("alert-row").then((r) => r[0]!)).queryByRole("button", { name: /^Позначити прочитаним:/ })).toBeNull();
    expect((await screen.findAllByTestId("alert-row")).length).toBe(before);

    await user.click(screen.getByRole("button", { name: "Позначити всі прочитаними" }));
    expect(await screen.findByText(/^Позначено прочитаними: \d+\./)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Позначити всі прочитаними" })).toBeDisabled();
  });

  it("фільтри: вид і стан; «Розв’язані» показує лише розв’язані; порожній стан", async () => {
    const user = userEvent.setup();
    renderScreen(<InventoryAlertsScreen />);
    await screen.findByRole("table", { name: "Сповіщення складу" });
    await user.click(screen.getByRole("button", { name: "Спливає термін" }));
    await waitFor(() => {
      for (const r of screen.getAllByTestId("alert-row")) expect(within(r).getByText("Спливає термін")).toBeInTheDocument();
    });
    expect(screen.getByRole("button", { name: "Спливає термін" })).toHaveAttribute("aria-pressed", "true");
    await user.click(screen.getByRole("button", { name: "Розв’язані" }));
    await user.click(screen.getByRole("button", { name: "Нестача" }));
    expect(await screen.findByText("Сповіщень за цими умовами немає.")).toBeInTheDocument();
  });

  it("посилання на залишки закладу й товар; без ПД клієнтів у тексті", async () => {
    renderScreen(<InventoryAlertsScreen />);
    const table = await screen.findByRole("table", { name: "Сповіщення складу" });
    expect(within(table).getAllByRole("link", { name: /^Залишки: / })[0]).toHaveAttribute("href", "/beauty/inventory");
    expect(within(table).getAllByRole("link", { name: /^Товар: / })[0]).toHaveAttribute("href", "/beauty/inventory/products");
    expect(table.textContent).not.toMatch(/\+380|клієнт/i);
  });

  it("майстер бачить лише сповіщення свого закладу", async () => {
    setRole("specialist");
    renderScreen(<InventoryAlertsScreen />);
    const table = await screen.findByRole("table", { name: "Сповіщення складу" });
    const names = new Set(within(table).getAllByTestId("alert-row").map((r) => within(r).getAllByRole("cell")[1]!.textContent));
    expect([...names]).toEqual([DEMO_LOCATIONS.find((l) => l.id === "c")!.name]);
  });

  it("вимкнений модуль / без права - пояснення без запитів", async () => {
    auth.user.modules = ["beauty_booking"];
    const spy = vi.fn();
    state.api = { ...(state.api as object), getInventoryAlerts: spy };
    renderScreen(<InventoryAlertsScreen />);
    expect(screen.getByText(/недоступний для вашого рівня доступу або модуль/)).toBeInTheDocument();
    expect(spy).not.toHaveBeenCalled();
  });
});

describe("бейдж «Склад» у меню (§32.19)", () => {
  it("показує число для зору й повний підпис для скрінрідера", async () => {
    renderScreen(<InventoryBadge />);
    const badge = await screen.findByTestId("inventory-badge");
    expect(badge.textContent).toMatch(/\d+, нових сповіщень складу: \d+/);
  });

  it("«99+» вище 99, підпис з точною кількістю", async () => {
    state.api = { ...(state.api as object), getInventoryAlertsUnreadCount: async () => 250 };
    renderScreen(<InventoryBadge />);
    const badge = await screen.findByTestId("inventory-badge");
    expect(within(badge).getByText("99+")).toHaveAttribute("aria-hidden", "true");
    expect(badge.textContent).toContain("понад 99");
  });

  it("модуль вимкнено або немає inventory.view - запитів немає, бейджа немає", async () => {
    const spy = vi.fn(async () => 5);
    state.api = { ...(state.api as object), getInventoryAlertsUnreadCount: spy };
    auth.user.modules = ["beauty_booking"];
    const first = renderScreen(<InventoryBadge />);
    expect(first.container.querySelector("[data-testid=inventory-badge]")).toBeNull();
    first.unmount();
    auth.user.modules = undefined;
    auth.user.permissions = { ...ROLE_PRESETS.owner, "inventory.view": "none" };
    const second = renderScreen(<InventoryBadge />);
    expect(second.container.querySelector("[data-testid=inventory-badge]")).toBeNull();
    expect(spy).not.toHaveBeenCalled();
  });

  it("module_disabled від сервера - бейджа немає", async () => {
    const { BeautyApiError } = await import("@/features/beauty-auth/errors");
    state.api = {
      ...(state.api as object),
      getInventoryAlertsUnreadCount: async () => {
        throw new BeautyApiError(403, "module_disabled", "x");
      },
    };
    const r = renderScreen(<InventoryBadge />);
    await new Promise((res) => setTimeout(res, 20));
    expect(r.container.querySelector("[data-testid=inventory-badge]")).toBeNull();
  });
});

describe("норми матеріалів у картці послуги (§32.16)", () => {
  const open = () => renderScreen(<ServiceMaterialsEditor serviceId="s1" serviceName="Манікюр" onClose={() => {}} />);

  it("пояснення про порожній override; мережевий набір із сида; збереження змінює кількість", async () => {
    const user = userEvent.setup();
    open();
    expect(screen.getByText(/Порожній набір закладу означає: у цьому закладі ця послуга матеріалів не списує/)).toBeInTheDocument();
    const form = await screen.findByRole("form", { name: "Для всієї мережі" });
    const qty = within(form).getByLabelText(/^Кількість 1/);
    expect((qty as HTMLInputElement).value).toBe("1");
    await user.clear(qty);
    await user.type(qty, "2");
    await user.click(within(form).getByRole("button", { name: "Зберегти норму" }));
    expect(await within(form).findByText(/^Збережено\./)).toBeInTheDocument();
  });

  it("у списку товарів лише salon_use|both (товар «для продажу» відсутній)", async () => {
    const user = userEvent.setup();
    open();
    const form = await screen.findByRole("form", { name: "Для всієї мережі" });
    await user.click(within(form).getByRole("button", { name: "Додати матеріал" }));
    const select = within(form).getByLabelText("Матеріал 3") as HTMLSelectElement;
    const names = within(select).getAllByRole("option").map((o) => o.textContent ?? "");
    expect(names.some((n) => n.startsWith("Маска для обличчя"))).toBe(false);
    expect(names.some((n) => n.startsWith("Фарба для волосся"))).toBe(true);
  });

  it("валідація: помилка рядка, підсумок і фокус на першому полі з aria-invalid", async () => {
    const user = userEvent.setup();
    open();
    const form = await screen.findByRole("form", { name: "Для всієї мережі" });
    await user.click(within(form).getByRole("button", { name: "Додати матеріал" }));
    await user.click(within(form).getByRole("button", { name: "Зберегти норму" }));
    expect(await within(form).findByText(/Виправте помилки у формі: 1/)).toBeInTheDocument();
    expect(within(form).getByText("Оберіть товар.")).toBeInTheDocument();
    await waitFor(() => expect(document.activeElement).toBe(within(form).getByLabelText("Матеріал 3")));
  });

  it("override закладу: порожній набір зберігається, у мережі - ні", async () => {
    const user = userEvent.setup();
    open();
    await screen.findByRole("form", { name: "Для всієї мережі" });
    await user.click(screen.getByRole("button", { name: "Додати набір для закладу" }));
    await user.selectOptions(screen.getByLabelText("Заклад"), DEMO_LOCATIONS[0]!.id);
    const form = await screen.findByRole("form", { name: `Заклад: ${DEMO_LOCATIONS[0]!.name}` });
    await user.click(within(form).getByRole("button", { name: "Зберегти норму" }));
    // після збереження набір з'являється серед закладів; порожній набір не є помилкою
    expect(await screen.findByRole("form", { name: `Заклад: ${DEMO_LOCATIONS[0]!.name}` })).toBeInTheDocument();
    expect(screen.queryByText(/Виправте помилки/)).toBeNull();
  });

  it("серверна помилка рядка (purpose_locked-подібна 422 з line) показується біля рядка", async () => {
    const { BeautyApiError } = await import("@/features/beauty-auth/errors");
    const user = userEvent.setup();
    state.api = {
      ...(state.api as object),
      setServiceMaterials: async () => {
        throw new BeautyApiError(422, "product_not_for_salon_use", "x", undefined, undefined, undefined, undefined, undefined, { line: 0 });
      },
    };
    open();
    const form = await screen.findByRole("form", { name: "Для всієї мережі" });
    await user.click(within(form).getByRole("button", { name: "Зберегти норму" }));
    expect((await within(form).findAllByText(/Рядок 1:.*лише для продажу/)).length).toBeGreaterThan(0);
  });

  it("видалення мережевої норми - з підтвердженням", async () => {
    const user = userEvent.setup();
    open();
    const form = await screen.findByRole("form", { name: "Для всієї мережі" });
    await user.click(within(form).getByRole("button", { name: "Видалити: Для всієї мережі" }));
    const dialog = await screen.findByRole("alertdialog");
    await user.click(within(dialog).getByRole("button", { name: "Видалити" }));
    expect(await screen.findByText("Матеріалів ще немає. Додайте перший.")).toBeInTheDocument();
  });

  it("лише перегляд: без права inventory.manage = all немає полів і кнопок збереження", async () => {
    setRole("specialist");
    open();
    const section = await screen.findByLabelText("Для всієї мережі");
    expect(within(section).getByText(/Рукавички одноразові/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Зберегти норму" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Додати набір для закладу" })).toBeNull();
    expect(screen.getByText(/змінювати норми може лише власник чи адміністратор/)).toBeInTheDocument();
  });

  it("модуль вимкнено / немає inventory.view - пояснення, без запитів", () => {
    auth.user.modules = ["beauty_booking"];
    const spy = vi.fn();
    state.api = { ...(state.api as object), getServiceMaterials: spy };
    open();
    expect(screen.getByText(/доступні лише за правом перегляду складу/)).toBeInTheDocument();
    expect(spy).not.toHaveBeenCalled();
  });
});
