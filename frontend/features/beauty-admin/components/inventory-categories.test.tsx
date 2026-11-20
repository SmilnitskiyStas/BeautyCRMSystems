import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

vi.hoisted(() => {
  vi.useFakeTimers({ toFake: ["Date"], now: new Date("2026-10-14T09:00:00Z") });
});

vi.mock("next/navigation", () => ({ useRouter: () => ({ push: vi.fn(), replace: vi.fn() }), usePathname: () => "/beauty/inventory/categories" }));

import { ROLE_PRESETS } from "@/features/beauty-auth/access";
import { DEMO_LOCATIONS } from "../../beauty-shared/demo-seed";
import { createMockInventory } from "../api/mock-inventory";

const auth = vi.hoisted(() => ({ user: { id: "u", email: "e", fullName: "n", role: "owner", specialistId: null as string | null, permissions: undefined as Record<string, string> | undefined, modules: undefined as string[] | undefined } }));
vi.mock("@/features/beauty-auth/components/auth-provider", () => ({ useAuth: () => auth }));

const state = vi.hoisted(() => ({ api: null as unknown }));
vi.mock("../api", () => ({ beautyApi: new Proxy({}, { get: (_t, k) => (state.api as Record<string | symbol, unknown>)[k] }) }));

import { InventoryCategoriesScreen } from "./inventory-categories-screen";
import { InventoryProductsScreen } from "./inventory-products-screen";

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
  state.api = createMockInventory({ role: () => auth.user.role, locations: () => DEMO_LOCATIONS, specialistLocationIds: () => ["c"], now: () => Date.now(), wait: false });
});
afterEach(() => cleanup());

const item = (name: string) => screen.getByText(name, { selector: "span" }).closest("li")!;
const rowOf = (name: string) => item(name).firstElementChild as HTMLElement;

describe("екран «Категорії»", () => {
  it("показує дерево з лічильниками; кнопка підкатегорії вимкнена на 3-му рівні з поясненням", async () => {
    renderScreen(<InventoryCategoriesScreen />);
    const tree = await screen.findByRole("list", { name: "Дерево категорій" });
    expect(within(tree).getByText("Волосся", { selector: "span" })).toBeInTheDocument();
    expect(rowOf("Волосся")).toHaveTextContent("Підкатегорій: 2");
    expect(rowOf("Безаміачні")).toHaveTextContent("Товарів: 1");
    const add = within(rowOf("Безаміачні")).getByRole("button", { name: "Додати підкатегорію до «Безаміачні»" });
    expect(add).toBeDisabled();
    expect(add).toHaveAccessibleDescription(/останній\) рівень/);
    expect(within(rowOf("Фарби")).getByRole("button", { name: "Додати підкатегорію до «Фарби»" })).toBeEnabled();
  });

  it("додати кореневу й підкатегорію; дубль імені - зрозуміла помилка 409 у діалозі", async () => {
    const user = userEvent.setup();
    renderScreen(<InventoryCategoriesScreen />);
    await screen.findByRole("list", { name: "Дерево категорій" });
    await user.click(screen.getByRole("button", { name: "Додати категорію" }));
    let dialog = await screen.findByRole("dialog", { name: "Нова категорія" });
    await user.type(within(dialog).getByLabelText(/^Назва/), "ВОЛОССЯ");
    await user.click(within(dialog).getByRole("button", { name: "Додати" }));
    expect(await within(dialog).findByRole("alert")).toHaveTextContent("Категорія з такою назвою вже є на цьому рівні.");
    await user.clear(within(dialog).getByLabelText(/^Назва/));
    await user.type(within(dialog).getByLabelText(/^Назва/), "Брови");
    await user.click(within(dialog).getByRole("button", { name: "Додати" }));
    expect(await screen.findByRole("status")).toHaveTextContent("Категорію «Брови» додано.");
    expect(screen.getByText("Брови", { selector: "span" })).toBeInTheDocument();

    await user.click(within(rowOf("Брови")).getByRole("button", { name: "Додати підкатегорію до «Брови»" }));
    dialog = await screen.findByRole("dialog", { name: "Нова підкатегорія в «Брови»" });
    await user.type(within(dialog).getByLabelText(/^Назва/), "Фарбування");
    await user.click(within(dialog).getByRole("button", { name: "Додати" }));
    await waitFor(() => expect(item("Брови")).toHaveTextContent("Фарбування"));
  });

  it("порожня назва - помилка без запиту", async () => {
    const user = userEvent.setup();
    renderScreen(<InventoryCategoriesScreen />);
    await screen.findByRole("list", { name: "Дерево категорій" });
    await user.click(screen.getByRole("button", { name: "Додати категорію" }));
    const dialog = await screen.findByRole("dialog");
    await user.click(within(dialog).getByRole("button", { name: "Додати" }));
    expect(within(dialog).getByLabelText(/^Назва/)).toHaveAttribute("aria-invalid", "true");
  });

  it("перейменування", async () => {
    const user = userEvent.setup();
    renderScreen(<InventoryCategoriesScreen />);
    await screen.findByRole("list", { name: "Дерево категорій" });
    await user.click(within(rowOf("Шампуні")).getByRole("button", { name: "Перейменувати «Шампуні»" }));
    const dialog = await screen.findByRole("dialog", { name: "Перейменувати «Шампуні»" });
    const input = within(dialog).getByLabelText(/^Назва/);
    expect(input).toHaveValue("Шампуні");
    await user.clear(input);
    await user.type(input, "Шампуні та бальзами");
    await user.click(within(dialog).getByRole("button", { name: "Зберегти" }));
    expect(await screen.findByText("Шампуні та бальзами", { selector: "span" })).toBeInTheDocument();
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });

  it("переміщення: у списку немає самого вузла, нащадків і місць, що ламають глибину", async () => {
    const user = userEvent.setup();
    renderScreen(<InventoryCategoriesScreen />);
    await screen.findByRole("list", { name: "Дерево категорій" });
    await user.click(within(rowOf("Фарби")).getByRole("button", { name: "Перемістити «Фарби»" }));
    const dialog = await screen.findByRole("dialog", { name: "Перемістити «Фарби»" });
    const select = within(dialog).getByLabelText("Новий батько") as HTMLSelectElement;
    const labels = within(select).getAllByRole("option").map((o) => o.textContent);
    // Поточний батько («Волосся») не пропонується; «Волосся › Шампуні» і «Нігті › Гель-лаки» ламають глибину (піддерево з 2 рівнів).
    expect(labels).toEqual(["Оберіть…", "На верхній рівень (без батька)", "Нігті"]);
    await user.click(within(dialog).getByRole("button", { name: "Перемістити" }));
    expect(within(dialog).getByLabelText("Новий батько")).toHaveAttribute("aria-invalid", "true");
    await user.selectOptions(select, "Нігті");
    await user.click(within(dialog).getByRole("button", { name: "Перемістити" }));
    await waitFor(() => expect(item("Нігті")).toHaveTextContent("Фарби"));
    expect(item("Волосся")).not.toHaveTextContent("Фарби");
  });

  it("архівувати / розархівувати", async () => {
    const user = userEvent.setup();
    renderScreen(<InventoryCategoriesScreen />);
    await screen.findByRole("list", { name: "Дерево категорій" });
    await user.click(within(rowOf("Гель-лаки")).getByRole("button", { name: "Архівувати «Гель-лаки»" }));
    expect(await screen.findByRole("status")).toHaveTextContent("архівовано");
    expect(within(rowOf("Гель-лаки")).getByText("Архів")).toBeInTheDocument();
    // Підкатегорію в архівній категорії додати не можна.
    expect(within(rowOf("Гель-лаки")).getByRole("button", { name: "Додати підкатегорію до «Гель-лаки»" })).toBeDisabled();
    await user.click(within(rowOf("Гель-лаки")).getByRole("button", { name: "Розархівувати «Гель-лаки»" }));
    await waitFor(() => expect(within(rowOf("Гель-лаки")).queryByText("Архів")).not.toBeInTheDocument());
  });

  it("видалення: непорожня - повідомлення й пропозиція «Архівувати»; порожня - видаляється", async () => {
    const user = userEvent.setup();
    renderScreen(<InventoryCategoriesScreen />);
    await screen.findByRole("list", { name: "Дерево категорій" });
    await user.click(within(rowOf("Гель-лаки")).getByRole("button", { name: "Видалити «Гель-лаки»" }));
    let dialog = await screen.findByRole("dialog", { name: "Видалити «Гель-лаки»?" });
    expect(within(dialog).queryByRole("button", { name: "Архівувати" })).not.toBeInTheDocument();
    await user.click(within(dialog).getByRole("button", { name: "Видалити" }));
    expect(await within(dialog).findByRole("alert")).toHaveTextContent("спершу перенесіть або видаліть підкатегорії й товари");
    await user.click(within(dialog).getByRole("button", { name: "Архівувати" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    expect(within(rowOf("Гель-лаки")).getByText("Архів")).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Додати категорію" }));
    dialog = await screen.findByRole("dialog", { name: "Нова категорія" });
    await user.type(within(dialog).getByLabelText(/^Назва/), "Тимчасова");
    await user.click(within(dialog).getByRole("button", { name: "Додати" }));
    await screen.findByText("Тимчасова", { selector: "span" });
    await user.click(within(rowOf("Тимчасова")).getByRole("button", { name: "Видалити «Тимчасова»" }));
    dialog = await screen.findByRole("dialog", { name: "Видалити «Тимчасова»?" });
    await user.click(within(dialog).getByRole("button", { name: "Видалити" }));
    await waitFor(() => expect(screen.queryByText("Тимчасова", { selector: "span" })).not.toBeInTheDocument());
  });

  it("422 category_depth від сервера показується повідомленням", async () => {
    const user = userEvent.setup();
    const { BeautyApiError } = await import("@/features/beauty-auth/errors");
    renderScreen(<InventoryCategoriesScreen />);
    await screen.findByRole("list", { name: "Дерево категорій" });
    (state.api as { moveInventoryCategory: () => Promise<unknown> }).moveInventoryCategory = () => Promise.reject(new BeautyApiError(422, "category_depth", "x"));
    await user.click(within(rowOf("Фарби")).getByRole("button", { name: "Перемістити «Фарби»" }));
    const dialog = await screen.findByRole("dialog");
    await user.selectOptions(within(dialog).getByLabelText("Новий батько"), "Нігті");
    await user.click(within(dialog).getByRole("button", { name: "Перемістити" }));
    expect(await within(dialog).findByRole("alert")).toHaveTextContent("не більше 3 рівнів");
  });

  it("без права зміни (specialist): лише перегляд, без кнопок дій", async () => {
    setRole("specialist");
    renderScreen(<InventoryCategoriesScreen />);
    await screen.findByRole("list", { name: "Дерево категорій" });
    expect(screen.getByText("Волосся", { selector: "span" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Додати категорію" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /Перейменувати|Перемістити|Архівувати|Видалити|Додати підкатегорію/ })).not.toBeInTheDocument();
  });
});

describe("товари й категорії", () => {
  it("список: колонка «Категорія» - шлях; старий текст із маркером «(текст)»; фільтр за вузлом включає підкатегорії", async () => {
    const user = userEvent.setup();
    renderScreen(<InventoryProductsScreen />);
    await screen.findByText("Шампунь професійний 500 мл");
    expect(screen.getByText("Шампунь професійний 500 мл").closest("tr")).toHaveTextContent("Волосся › Шампуні");
    expect(screen.getByText("Фарба для волосся 60 мл").closest("tr")).toHaveTextContent("Волосся › Фарби › Безаміачні");
    const old = screen.getByText("Дезінфікуючий розчин").closest("tr")!;
    expect(old).toHaveTextContent("Гігієна (текст)");

    await user.click(screen.getByRole("button", { name: /^Категорія/ }));
    await user.click(screen.getByRole("treeitem", { name: "Волосся" }));
    await waitFor(() => expect(screen.queryByText("Гель-лак (база)")).not.toBeInTheDocument());
    expect(screen.getByText("Шампунь професійний 500 мл")).toBeInTheDocument();
    expect(screen.getByText("Фарба для волосся 60 мл")).toBeInTheDocument();
    expect(screen.queryByText("Дезінфікуючий розчин")).not.toBeInTheDocument();
    // «Усі категорії» скидає фільтр.
    await user.click(screen.getByRole("button", { name: /^Категорія/ }));
    await user.click(screen.getByRole("treeitem", { name: "Усі категорії" }));
    expect(await screen.findByText("Гель-лак (база)")).toBeInTheDocument();
  });

  it("форма товару: вибір категорії в дереві йде в payload як categoryId; стара категорія - підказка", async () => {
    const user = userEvent.setup();
    renderScreen(<InventoryProductsScreen />);
    await screen.findByText("Шампунь професійний 500 мл");
    const api = state.api as { createInventoryProduct: (r: unknown) => Promise<unknown>; updateInventoryProduct: (id: string, r: unknown) => Promise<unknown> };
    const create = vi.spyOn(api, "createInventoryProduct");
    await user.click(screen.getByRole("button", { name: "Додати товар" }));
    const form = screen.getByRole("form", { name: "Новий товар" });
    await user.type(within(form).getByLabelText(/^Назва/), "Новий лак");
    await user.type(within(form).getByLabelText(/^Роздрібна ціна/), "100");
    const picker = within(form).getByRole("button", { name: /^Категорія/ });
    expect(picker).toHaveTextContent("Без категорії");
    await user.click(picker);
    await user.click(screen.getByTestId("category-toggle-" + (await idOf("Нігті"))));
    await user.click(screen.getByRole("treeitem", { name: "Гель-лаки" }));
    expect(picker).toHaveTextContent("Нігті › Гель-лаки");
    await user.click(within(form).getByRole("button", { name: "Додати товар" }));
    await waitFor(() => expect(create).toHaveBeenCalled());
    const payload = create.mock.calls[0]![0] as Record<string, unknown>;
    expect(payload.categoryId).toBe(await idOf("Гель-лаки"));
    expect("category" in payload).toBe(false);

    // Товар зі старим текстовим значенням: підказка під полем.
    await user.click(screen.getByRole("button", { name: "Редагувати товар «Дезінфікуючий розчин»" }));
    const edit = screen.getByRole("form", { name: "Товар «Дезінфікуючий розчин»" });
    expect(within(edit).getByText(/Стара категорія \(текст\): Гігієна/)).toBeInTheDocument();
    expect(within(edit).getByRole("button", { name: /^Категорія/ })).toHaveTextContent("Без категорії");
  });

  it("specialist без права каталогу: поле категорії вимкнене", async () => {
    setRole("specialist");
    const user = userEvent.setup();
    renderScreen(<InventoryProductsScreen />);
    await screen.findByText("Шампунь професійний 500 мл");
    await user.click(screen.getByRole("button", { name: "Відкрити товар «Шампунь професійний 500 мл»" }));
    const form = screen.getByRole("form", { name: "Товар «Шампунь професійний 500 мл»" });
    expect(within(form).getByRole("button", { name: /^Категорія/ })).toBeDisabled();
  });
});

async function idOf(name: string): Promise<string> {
  const cats = await (state.api as { getInventoryCategories: (a: boolean) => Promise<{ id: string; name: string }[]> }).getInventoryCategories(true);
  return cats.find((c) => c.name === name)!.id;
}
