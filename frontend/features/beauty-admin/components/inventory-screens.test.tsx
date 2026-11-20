import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// Фіксований «зараз» (14.10.2026 12:00 Київ): і для mock-склад, і для дат у формах. Реальний годинник не використовується.
vi.hoisted(() => {
  vi.useFakeTimers({ toFake: ["Date"], now: new Date("2026-10-14T09:00:00Z") });
});

const push = vi.fn();
vi.mock("next/navigation", () => ({ useRouter: () => ({ push, replace: vi.fn() }), usePathname: () => "/beauty/inventory" }));

import { ROLE_PRESETS } from "@/features/beauty-auth/access";
import { DEMO_LOCATIONS } from "../../beauty-shared/demo-seed";
import { createMockInventory } from "../api/mock-inventory";

const auth = vi.hoisted(() => ({ user: { id: "u", email: "e", fullName: "n", role: "owner", specialistId: null as string | null, permissions: undefined as Record<string, string> | undefined, modules: undefined as string[] | undefined } }));
vi.mock("@/features/beauty-auth/components/auth-provider", () => ({ useAuth: () => auth }));

const state = vi.hoisted(() => ({ api: null as unknown }));
vi.mock("../api", () => ({ beautyApi: new Proxy({}, { get: (_t, k) => (state.api as Record<string | symbol, unknown>)[k] }) }));

import { InventoryBatchesScreen } from "./inventory-batches-screen";
import { InventoryMovementsScreen } from "./inventory-movements-screen";
import { InventoryProductsScreen } from "./inventory-products-screen";
import { InventoryStockScreen } from "./inventory-stock-screen";
import { InventoryStocktakeDetailScreen } from "./inventory-stocktake-detail";
import { InventoryStocktakesScreen } from "./inventory-stocktakes-screen";
import { InventorySuppliersScreen } from "./inventory-suppliers-screen";

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
  push.mockReset();
  setRole("owner");
  state.api = createMockInventory({
    role: () => auth.user.role,
    locations: () => DEMO_LOCATIONS,
    specialistLocationIds: () => ["c"],
    now: () => Date.now(),
    wait: false,
  });
});
afterEach(() => cleanup());

/** Вибір опції `<select>` за підписом поля й текстом опції. */
async function pick(user: ReturnType<typeof userEvent.setup>, label: RegExp | string, optionText: RegExp | string, root: HTMLElement | Document = document) {
  const select = within(root as HTMLElement).getByLabelText(label) as HTMLSelectElement;
  const option = within(select).getByRole("option", { name: optionText });
  await user.selectOptions(select, option);
}

describe("гейтинг екрана", () => {
  it("без права / вимкнений модуль - пояснення, без запитів до API", async () => {
    auth.user.modules = ["beauty_booking"];
    renderScreen(<InventoryStockScreen />);
    expect(screen.getByText(/недоступний для вашого рівня доступу або модуль/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Надходження" })).not.toBeInTheDocument();
  });

  it("module_disabled від сервера - окреме повідомлення", async () => {
    const { BeautyApiError } = await import("@/features/beauty-auth/errors");
    (state.api as { getInventoryScope: () => Promise<unknown> }).getInventoryScope = () => Promise.reject(new BeautyApiError(403, "module_disabled", "x"));
    renderScreen(<InventoryStockScreen />);
    expect(await screen.findByRole("alert")).toHaveTextContent("Модуль «Склад» вимкнено");
  });
});

describe("залишки", () => {
  it("зведення: чіпи «Усі заклади» + заклади, мінімальний залишок підсвічено текстом, колонка вартості для owner", async () => {
    renderScreen(<InventoryStockScreen />);
    const chips = await screen.findByRole("group", { name: "Заклад" });
    expect(within(chips).getByRole("button", { name: "Усі заклади" })).toHaveAttribute("aria-pressed", "true");
    for (const name of ["Центр", "Поділ", "Печерськ"]) expect(within(chips).getByRole("button", { name })).toBeInTheDocument();
    const row = (await screen.findByText("Рукавички одноразові (уп. 100 шт)")).closest("tr")!;
    expect(within(row).getByText("Мало десь")).toBeInTheDocument();
    expect(row.textContent).toContain("Центр: 12 уп (мало)");
    expect(screen.getByRole("columnheader", { name: "Вартість" })).toBeInTheDocument();
  });

  it("заклад: «Мало», «Є прострочене», найближчий термін; фільтр «Низький залишок»", async () => {
    const user = userEvent.setup();
    renderScreen(<InventoryStockScreen />);
    await user.click(await screen.findByRole("button", { name: "Центр" }));
    const dye = (await screen.findByText("Фарба для волосся 60 мл")).closest("tr")!;
    expect(within(dye).getByText("Є прострочене")).toBeInTheDocument();
    expect(within(dye).getByText("09.10.2026")).toBeInTheDocument();
    const gloves = screen.getByText("Рукавички одноразові (уп. 100 шт)").closest("tr")!;
    expect(within(gloves).getByText("Мало")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Низький залишок" }));
    await waitFor(() => expect(screen.queryByText("Фарба для волосся 60 мл")).not.toBeInTheDocument());
    expect(screen.getByText("Рукавички одноразові (уп. 100 шт)")).toBeInTheDocument();
  });

  it("deputy не бачить цін; specialist бачить лише свій заклад і без кнопок операцій", async () => {
    setRole("deputy");
    const { unmount } = renderScreen(<InventoryStockScreen />);
    await screen.findByText("Рукавички одноразові (уп. 100 шт)");
    expect(screen.queryByRole("columnheader", { name: "Вартість" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Надходження" })).toBeEnabled();
    unmount();
    setRole("specialist");
    renderScreen(<InventoryStockScreen />);
    const chips = await screen.findByRole("group", { name: "Заклад" });
    expect(within(chips).getByRole("button", { name: "Центр" })).toBeInTheDocument();
    expect(within(chips).queryByRole("button", { name: "Поділ" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Надходження" })).not.toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Постачальники" })).not.toBeInTheDocument();
  });
});

describe("надходження", () => {
  it("цілі для шт/уп: помилка з aria-invalid, без запиту; успішне надходження оновлює залишок", async () => {
    const user = userEvent.setup();
    renderScreen(<InventoryStockScreen />);
    await user.click(await screen.findByRole("button", { name: "Центр" }));
    await screen.findByText("Гель-лак (база)");
    await user.click(screen.getByRole("button", { name: "Надходження" }));
    const dialog = await screen.findByRole("dialog", { name: "Надходження товару" });
    await pick(user, /Товар \(позиція 1\)/, /Гель-лак/, dialog);
    const qty = within(dialog).getByLabelText(/^Кількість/);
    await user.type(qty, "2,5");
    await user.click(within(dialog).getByRole("button", { name: "Зберегти надходження" }));
    expect(qty).toHaveAttribute("aria-invalid", "true");
    expect(qty.getAttribute("aria-describedby")).toBeTruthy();
    expect(within(dialog).getByText(/має бути цілою/)).toBeInTheDocument();
    await user.clear(qty);
    await user.type(qty, "4");
    await user.click(within(dialog).getByRole("button", { name: "Зберегти надходження" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    expect(await screen.findByText("Надходження збережено.")).toBeInTheDocument();
    const gel = (await screen.findByText("Гель-лак (база)")).closest("tr")!;
    await waitFor(() => expect(within(gel).getByText("7 шт")).toBeInTheDocument());
  });

  it("товар зі строком придатності: термін обов'язковий; поле ціни - лише з costs=edit", async () => {
    const user = userEvent.setup();
    renderScreen(<InventoryStockScreen />);
    await user.click(await screen.findByRole("button", { name: "Центр" }));
    await user.click(await screen.findByRole("button", { name: "Надходження" }));
    const dialog = await screen.findByRole("dialog");
    await pick(user, /Товар \(позиція 1\)/, /Шампунь/, dialog);
    expect(within(dialog).getByLabelText(/Закупівельна ціна за одиницю/)).toBeInTheDocument();
    await user.type(within(dialog).getByLabelText(/^Кількість/), "3");
    await user.click(within(dialog).getByRole("button", { name: "Зберегти надходження" }));
    expect(within(dialog).getByLabelText(/Термін придатності/)).toHaveAttribute("aria-invalid", "true");
    await user.type(within(dialog).getByLabelText(/Термін придатності/), "2026-10-01");
    await user.click(within(dialog).getByRole("button", { name: "Зберегти надходження" }));
    expect(within(dialog).getByText("Термін придатності вже минув.")).toBeInTheDocument();
  });

  it("deputy не бачить поля ціни в позиції", async () => {
    setRole("deputy");
    const user = userEvent.setup();
    renderScreen(<InventoryStockScreen />);
    await user.click(await screen.findByRole("button", { name: "Центр" }));
    await user.click(await screen.findByRole("button", { name: "Надходження" }));
    const dialog = await screen.findByRole("dialog");
    await pick(user, /Товар \(позиція 1\)/, /Шампунь/, dialog);
    expect(within(dialog).queryByLabelText(/Закупівельна ціна/)).not.toBeInTheDocument();
  });
});

describe("списання й переміщення", () => {
  it("списання потребує підтвердження (alertdialog) і списує за FEFO", async () => {
    const user = userEvent.setup();
    renderScreen(<InventoryStockScreen />);
    await user.click(await screen.findByRole("button", { name: "Центр" }));
    await user.click(await screen.findByRole("button", { name: "Списання" }));
    const dialog = await screen.findByRole("dialog", { name: "Списання товару" });
    await pick(user, /^Причина/, "Пошкоджено", dialog);
    await waitFor(() => expect(within(dialog).getByLabelText(/Товар \(позиція 1\)/)).toBeInTheDocument());
    await pick(user, /Товар \(позиція 1\)/, /Фарба/, dialog);
    await user.type(within(dialog).getByLabelText(/^Кількість/), "8");
    await user.click(within(dialog).getByRole("button", { name: "Списати" }));
    const confirm = within(dialog).getByRole("group", { name: "Підтвердіть операцію" });
    expect(confirm).toHaveTextContent("Списати 1 поз.");
    await user.click(within(confirm).getByRole("button", { name: "Так, списати" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    expect(await screen.findByText("Списання збережено.")).toBeInTheDocument();
    const dye = (await screen.findByText("Фарба для волосся 60 мл")).closest("tr")!;
    await waitFor(() => expect(within(dye).getByText("12 шт")).toBeInTheDocument());
    expect(within(dye).queryByText("Є прострочене")).not.toBeInTheDocument(); // прострочена партія списана першою
  });

  it("нестача: 422 insufficient_stock показує потрібно/доступно, товар не списано", async () => {
    const user = userEvent.setup();
    renderScreen(<InventoryStockScreen />);
    await user.click(await screen.findByRole("button", { name: "Центр" }));
    await user.click(await screen.findByRole("button", { name: "Списання" }));
    const dialog = await screen.findByRole("dialog");
    await pick(user, /^Причина/, "Втрачено", dialog);
    await waitFor(() => expect(within(dialog).getByLabelText(/Товар \(позиція 1\)/)).toBeInTheDocument());
    await pick(user, /Товар \(позиція 1\)/, /Гель-лак/, dialog);
    await user.type(within(dialog).getByLabelText(/^Кількість/), "10");
    await user.click(within(dialog).getByRole("button", { name: "Списати" }));
    await user.click(within(dialog).getByRole("button", { name: "Так, списати" }));
    expect(await within(dialog).findByRole("alert")).toHaveTextContent(/потрібно 10 шт, доступно 3 шт/);
    expect(within(dialog).queryByRole("group", { name: "Підтвердіть операцію" })).not.toBeInTheDocument();
  });

  it("коментар обов'язковий для «Виправлення помилки»", async () => {
    const user = userEvent.setup();
    renderScreen(<InventoryStockScreen />);
    await user.click(await screen.findByRole("button", { name: "Центр" }));
    await user.click(await screen.findByRole("button", { name: "Списання" }));
    const dialog = await screen.findByRole("dialog");
    await pick(user, /^Причина/, "Виправлення помилки", dialog);
    await user.click(within(dialog).getByRole("button", { name: "Списати" }));
    expect(within(dialog).getByLabelText(/^Коментар/)).toHaveAttribute("aria-invalid", "true");
    expect(within(dialog).getAllByText("Додайте коментар до списання.").length).toBeGreaterThan(0);
  });

  it("переміщення: прострочена партія недоступна для вибору, підтвердження, результат у закладі призначення", async () => {
    const user = userEvent.setup();
    renderScreen(<InventoryStockScreen />);
    await user.click(await screen.findByRole("button", { name: "Центр" }));
    await user.click(await screen.findByRole("button", { name: "Переміщення" }));
    const dialog = await screen.findByRole("dialog", { name: "Переміщення між закладами" });
    await pick(user, /^Куди/, "Поділ", dialog);
    await waitFor(() => expect(within(dialog).getByLabelText(/Товар \(позиція 1\)/)).toBeInTheDocument());
    await pick(user, /Товар \(позиція 1\)/, /Фарба/, dialog);
    const batchSelect = within(dialog).getByLabelText("Партія");
    expect(within(batchSelect).getByRole("option", { name: /прострочена/ })).toBeDisabled();
    await user.type(within(dialog).getByLabelText(/^Кількість/), "5");
    await user.click(within(dialog).getByRole("button", { name: "Перемістити" }));
    const confirm = within(dialog).getByRole("group", { name: "Підтвердіть операцію" });
    expect(confirm).toHaveTextContent("із «Центр» до «Поділ»");
    await user.click(within(confirm).getByRole("button", { name: "Так, перемістити" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    expect(await screen.findByText("Переміщення виконано.")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Поділ" }));
    const dye = (await screen.findByText("Фарба для волосся 60 мл")).closest("tr")!;
    expect(within(dye).getByText("5 шт")).toBeInTheDocument();
  });
});

describe("партії й терміни, журнал", () => {
  it("партії: прострочена й скора підсвічені текстом статусу й кількістю діб; фільтр за статусом", async () => {
    const user = userEvent.setup();
    renderScreen(<InventoryBatchesScreen />);
    await user.click(await screen.findByRole("button", { name: "Прострочені" }));
    const row = (await screen.findByText("DY-0101")).closest("tr")!;
    expect(within(row).getByText("Прострочено")).toBeInTheDocument();
    expect(within(row).getByText("Прострочено 5 діб тому")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Скоро спливають" }));
    const soon = (await screen.findByText("SH-0412")).closest("tr")!;
    expect(within(soon).getByText("Скоро спливає")).toBeInTheDocument();
    expect(within(soon).getByText("Залишилось 20 діб")).toBeInTheDocument();
    expect(screen.queryByText("DY-0101")).not.toBeInTheDocument();
  });

  it("журнал: знакові кількості, причина списання; specialist бачить лише свій заклад", async () => {
    renderScreen(<InventoryMovementsScreen />);
    const damaged = (await screen.findByText(/Пошкоджено: Розбита пляшка/)).closest("tr")!;
    expect(within(damaged).getByText("−2 шт")).toBeInTheDocument();
    expect(within(damaged).getByText("Списання")).toBeInTheDocument();
    cleanup();
    setRole("specialist");
    renderScreen(<InventoryMovementsScreen />);
    await screen.findByText(/Пошкоджено: Розбита пляшка/);
    const chips = screen.getByRole("group", { name: "Заклад" });
    expect(within(chips).queryByRole("button", { name: "Поділ" })).not.toBeInTheDocument();
    expect(screen.queryByRole("columnheader", { name: "Ціна за од." })).not.toBeInTheDocument();
  });
});

describe("товари й постачальники", () => {
  it("каталог: колонка ціни за costs; створення товару; barcode_taken - зрозуміле повідомлення", async () => {
    const user = userEvent.setup();
    renderScreen(<InventoryProductsScreen />);
    await screen.findByText("Шампунь професійний 500 мл");
    expect(screen.getByRole("columnheader", { name: "Закупівельна ціна" })).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Додати товар" }));
    const form = screen.getByRole("form", { name: "Новий товар" });
    await user.type(within(form).getByLabelText(/^Назва/), "Тестовий товар");
    await user.type(within(form).getByLabelText(/^Роздрібна ціна/), "199,90");
    await user.type(within(form).getByLabelText(/^Штрихкод/), "4820000000011");
    await user.click(within(form).getByRole("button", { name: "Додати товар" }));
    expect(await within(form).findByRole("alert")).toHaveTextContent("Товар з таким штрихкодом уже є.");
    await user.clear(within(form).getByLabelText(/^Штрихкод/));
    await user.type(within(form).getByLabelText(/^Мінімальний залишок/), "2,5");
    await user.click(within(form).getByRole("button", { name: "Додати товар" }));
    expect(within(form).getByLabelText(/^Мінімальний залишок/)).toHaveAttribute("aria-invalid", "true");
    await user.clear(within(form).getByLabelText(/^Мінімальний залишок/));
    await user.type(within(form).getByLabelText(/^Мінімальний залишок/), "3");
    await user.click(within(form).getByRole("button", { name: "Додати товар" }));
    await waitFor(() => expect(screen.queryByRole("form", { name: "Новий товар" })).not.toBeInTheDocument());
    expect(await screen.findByText("Тестовий товар")).toBeInTheDocument();
  });

  it("роздрібна ціна: показ за призначенням, очищення при salon_use, обов'язковість, список", async () => {
    const user = userEvent.setup();
    renderScreen(<InventoryProductsScreen />);
    await screen.findByText("Шампунь професійний 500 мл");
    expect(screen.getByRole("columnheader", { name: "Роздрібна ціна" })).toBeInTheDocument();
    const shampooRow = screen.getByText("Шампунь професійний 500 мл").closest("tr")!;
    expect(shampooRow).toHaveTextContent("450");
    const dyeRow = screen.getByText("Фарба для волосся 60 мл").closest("tr")!;
    expect(within(dyeRow).queryByText("не вказана")).not.toBeInTheDocument();
    // «Знятий з виробництва крем» (sale) без ціни - у списку «не вказана».
    await user.selectOptions(screen.getByLabelText(/^Статус/), "all");
    expect(await screen.findByText("не вказана")).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Додати товар" }));
    const form = screen.getByRole("form", { name: "Новий товар" });
    const price = () => within(form).queryByLabelText(/^Роздрібна ціна/);
    expect(price()).toBeInTheDocument();
    await user.type(within(form).getByLabelText(/^Назва/), "Крем");
    await user.click(within(form).getByRole("button", { name: "Додати товар" }));
    expect(price()).toHaveAttribute("aria-invalid", "true");
    expect(price()).toHaveAccessibleDescription(/За одиницю виміру/);
    await user.type(price()!, "-5");
    await user.click(within(form).getByRole("button", { name: "Додати товар" }));
    expect(price()).toHaveAttribute("aria-invalid", "true");
    await user.clear(price()!);
    await user.type(price()!, "12");
    await user.selectOptions(within(form).getByLabelText(/^Призначення/), "salon_use");
    expect(price()).not.toBeInTheDocument();
    await user.selectOptions(within(form).getByLabelText(/^Призначення/), "sale");
    expect(price()).toHaveValue("");
  });

  it("deputy: каталог редагує, але ціни не бачить і поля ціни немає; specialist - лише перегляд", async () => {
    setRole("deputy");
    const user = userEvent.setup();
    const { unmount } = renderScreen(<InventoryProductsScreen />);
    await screen.findByText("Шампунь професійний 500 мл");
    expect(screen.queryByRole("columnheader", { name: "Закупівельна ціна" })).not.toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Додати товар" }));
    expect(screen.queryByLabelText(/Закупівельна ціна/)).not.toBeInTheDocument();
    unmount();
    setRole("specialist");
    renderScreen(<InventoryProductsScreen />);
    await screen.findByText("Шампунь професійний 500 мл");
    expect(screen.queryByRole("button", { name: "Додати товар" })).not.toBeInTheDocument();
    expect(screen.getAllByRole("button", { name: /^Відкрити товар/ }).length).toBeGreaterThan(0);
  });

  it("одиниця товару з рухом заблокована (unit_locked); налаштування закладу: мін. залишок і повернення до мережевого", async () => {
    const user = userEvent.setup();
    renderScreen(<InventoryProductsScreen />);
    await user.click(await screen.findByRole("button", { name: "Редагувати товар «Рукавички одноразові (уп. 100 шт)»" }));
    const form = await screen.findByRole("form", { name: /Товар «Рукавички/ });
    expect(within(form).getByLabelText(/Одиниця виміру/)).toBeDisabled();
    const center = await screen.findByRole("group", { name: "Заклад Центр" });
    const min = within(center).getByLabelText(/Мін. залишок у «Центр»/);
    await user.type(min, "5");
    await user.click(within(center).getByRole("button", { name: "Зберегти налаштування закладу Центр" }));
    expect(await screen.findByText("Налаштування закладу збережено.")).toBeInTheDocument();
    await user.click(await within(await screen.findByRole("group", { name: "Заклад Центр" })).findByRole("button", { name: "Повернути мережеві значення для закладу Центр" }));
    expect(await screen.findByText("Повернуто мережеві значення.")).toBeInTheDocument();
  });

  it("постачальники: список, створення з валідацією, supplier_name_taken; specialist - без доступу", async () => {
    const user = userEvent.setup();
    renderScreen(<InventorySuppliersScreen />);
    await screen.findByText("Pro Salon Supply");
    await user.click(screen.getByRole("button", { name: "Додати постачальника" }));
    const form = screen.getByRole("form", { name: "Новий постачальник" });
    await user.type(within(form).getByLabelText(/^Назва/), "Pro Salon Supply");
    await user.type(within(form).getByLabelText(/^Пошта/), "bad");
    await user.click(within(form).getByRole("button", { name: "Додати постачальника" }));
    expect(within(form).getByLabelText(/^Пошта/)).toHaveAttribute("aria-invalid", "true");
    await user.clear(within(form).getByLabelText(/^Пошта/));
    await user.click(within(form).getByRole("button", { name: "Додати постачальника" }));
    expect(await within(form).findByRole("alert")).toHaveTextContent("Постачальник з такою назвою вже є.");
    cleanup();
    setRole("specialist");
    renderScreen(<InventorySuppliersScreen />);
    expect(screen.getByText(/недоступний для вашого рівня доступу/)).toBeInTheDocument();
  });
});

describe("інвентаризація", () => {
  it("створення: перехід на чернетку; друга для того ж закладу - 409 з посиланням на поточну", async () => {
    const user = userEvent.setup();
    renderScreen(<InventoryStocktakesScreen />);
    await user.click(await screen.findByRole("button", { name: "Нова інвентаризація" }));
    await waitFor(() => expect(push).toHaveBeenCalledTimes(1));
    const href = push.mock.calls[0]![0] as string;
    expect(href).toMatch(/^\/beauty\/inventory\/stocktakes\/st\d+$/);
    await user.click(screen.getByRole("button", { name: "Нова інвентаризація" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("У закладі вже триває інвентаризація.");
    expect(screen.getByRole("link", { name: "Відкрити поточну інвентаризацію" })).toHaveAttribute("href", href);
  });

  it("підрахунок: різниця, надлишок з терміном, збереження, проведення через підтвердження; проведена - лише для читання", async () => {
    const api = state.api as ReturnType<typeof createMockInventory>;
    const gloves = (await api.getInventoryProducts({ search: "Рукавички" })).items[0]!;
    const shampoo = (await api.getInventoryProducts({ search: "Шампунь" })).items[0]!;
    const st = await api.createStocktake({ locationId: "c", productIds: [gloves.id, shampoo.id] });
    const user = userEvent.setup();
    renderScreen(<InventoryStocktakeDetailScreen id={st.id} />);
    const glovesRow = (await screen.findByText("Рукавички одноразові (уп. 100 шт)")).closest("tr")!;
    const count = within(glovesRow).getByLabelText(/Підраховано: Рукавички/);
    await user.type(count, "10");
    expect(within(glovesRow).getByText("−2 уп")).toBeInTheDocument();
    const shampooRow = screen.getByText("Шампунь професійний 500 мл").closest("tr")!;
    await user.type(within(shampooRow).getByLabelText(/Підраховано: Шампунь/), "20");
    expect(within(shampooRow).getByLabelText(/Термін придатності надлишку/)).toBeInTheDocument();
    await user.type(within(shampooRow).getByLabelText(/Термін придатності надлишку/), "2026-10-01");
    await user.click(screen.getByRole("button", { name: "Провести інвентаризацію" }));
    expect(within(shampooRow).getByText("Термін придатності вже минув.")).toBeInTheDocument();
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    const date = within(shampooRow).getByLabelText(/Термін придатності надлишку/);
    await user.clear(date);
    await user.type(date, "2027-01-01");
    await user.click(screen.getByRole("button", { name: "Зберегти підрахунки" }));
    expect(await screen.findByText("Підрахунки збережено.")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Провести інвентаризацію" }));
    const dialog = await screen.findByRole("dialog", { name: "Провести інвентаризацію?" });
    expect(dialog).toHaveTextContent("Пораховано позицій: 2");
    await user.click(within(dialog).getByRole("button", { name: "Так, провести" }));
    expect(await screen.findByText(/Інвентаризацію проведено: різниці відображено в журналі рухів/)).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByLabelText(/Підраховано: Рукавички/)).not.toBeInTheDocument());
    expect(screen.getByText("Проведено")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Провести інвентаризацію" })).not.toBeInTheDocument();
  });

  it("скасування через діалог; конфлікт версій показує повідомлення", async () => {
    const api = state.api as ReturnType<typeof createMockInventory>;
    const gloves = (await api.getInventoryProducts({ search: "Рукавички" })).items[0]!;
    const st = await api.createStocktake({ locationId: "c", productIds: [gloves.id] });
    const user = userEvent.setup();
    renderScreen(<InventoryStocktakeDetailScreen id={st.id} />);
    const input = await screen.findByLabelText(/Підраховано: Рукавички/);
    // інший користувач змінює інвентаризацію
    await api.saveStocktakeLines(st.id, 1, [{ productId: gloves.id, countedQuantity: 1 }]);
    await user.type(input, "7");
    await user.click(screen.getByRole("button", { name: "Зберегти підрахунки" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("Інвентаризацію змінив інший користувач. Оновіть сторінку.");
    await user.click(screen.getByRole("button", { name: "Скасувати інвентаризацію" }));
    const dialog = await screen.findByRole("dialog", { name: "Скасувати інвентаризацію?" });
    await user.click(within(dialog).getByRole("button", { name: "Так, скасувати" }));
    expect(await screen.findByText("Інвентаризацію скасовано.")).toBeInTheDocument();
    await waitFor(() => expect(screen.getByText("Скасовано")).toBeInTheDocument());
  });
});
