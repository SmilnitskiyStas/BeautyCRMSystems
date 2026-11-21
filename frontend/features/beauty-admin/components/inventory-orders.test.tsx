import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// Фіксований «зараз» (14.10.2026 12:00 Київ); реальний годинник не використовується.
vi.hoisted(() => {
  vi.useFakeTimers({ toFake: ["Date"], now: new Date("2026-10-14T09:00:00Z") });
});

vi.mock("next/navigation", () => ({ useRouter: () => ({ push: vi.fn(), replace: vi.fn() }), usePathname: () => "/beauty/inventory/orders" }));

import { ROLE_PRESETS } from "@/features/beauty-auth/access";
import { BeautyApiError } from "@/features/beauty-auth/errors";
import { DEMO_LOCATIONS } from "../../beauty-shared/demo-seed";
import { createMockInventory } from "../api/mock-inventory";
import { createMockProductOrders } from "../api/mock-product-orders";

const auth = vi.hoisted(() => ({ user: { id: "u", email: "e", fullName: "n", role: "owner", specialistId: null as string | null, permissions: undefined as Record<string, string> | undefined, modules: undefined as string[] | undefined } }));
vi.mock("@/features/beauty-auth/components/auth-provider", () => ({ useAuth: () => auth }));

const state = vi.hoisted(() => ({ api: null as unknown }));
vi.mock("../api", () => ({ beautyApi: new Proxy({}, { get: (_t, k) => (state.api as Record<string | symbol, unknown>)[k] }) }));

import { InventoryOrdersScreen } from "./inventory-orders-screen";

function setRole(role: "owner" | "admin" | "deputy" | "specialist") {
  auth.user.role = role;
  auth.user.permissions = { ...ROLE_PRESETS[role] };
  auth.user.specialistId = role === "specialist" ? "m" : null;
  auth.user.modules = undefined;
}
function renderScreen(ui: ReactElement = <InventoryOrdersScreen />) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(<QueryClientProvider client={qc}>{ui}</QueryClientProvider>);
}
const orderApi = () => state.api as ReturnType<typeof createMockProductOrders> & Record<string, unknown>;
const patch = (extra: Record<string, unknown>) => {
  state.api = { ...(state.api as object), ...extra };
};

beforeEach(() => {
  setRole("owner");
  const inv = createMockInventory({ role: () => auth.user.role, locations: () => DEMO_LOCATIONS, specialistLocationIds: () => ["c"], now: () => Date.now(), wait: false });
  const orders = createMockProductOrders({ role: () => auth.user.role, locations: () => DEMO_LOCATIONS, specialistLocationIds: () => ["c"], now: () => Date.now(), wait: false });
  state.api = { ...inv, ...orders, getLocations: async () => DEMO_LOCATIONS };
});
afterEach(() => cleanup());

const rows = () => screen.queryAllByTestId("order-row");
const rowNumbers = () => rows().map((r) => within(r).getByRole("button").textContent);
const openOrder = async (user: ReturnType<typeof userEvent.setup>, number: number) => {
  await user.click(await screen.findByRole("button", { name: `Відкрити замовлення №${number}` }));
  return screen.findByRole("dialog");
};

describe("екран замовлень: список", () => {
  it("за замовчуванням - оплачені до видачі (open): таблиця з caption і заголовками, статуси словами, індикатор оплати в процесі", async () => {
    renderScreen();
    await screen.findAllByTestId("order-row");
    expect(screen.getByRole("heading", { level: 1, name: "Замовлення" })).toBeInTheDocument();
    const table = screen.getByRole("table", { name: "Замовлення товарів" });
    const headers = within(table).getAllByRole("columnheader").map((h) => h.textContent);
    expect(headers).toEqual(["№", "Дата", "Заклад", "Клієнт", "Позицій", "Сума", "Статус"]);
    expect(rowNumbers()).toEqual(["№1040", "№1041", "№1042", "№1034"]);
    expect(screen.getByRole("button", { name: "Оплачені до видачі" })).toHaveAttribute("aria-pressed", "true");
    const r1042 = rows().find((r) => r.textContent?.includes("№1042"))!;
    expect(r1042.textContent).toContain("Оплачено, до видачі");
    expect(r1042.textContent).toContain("2 позиції, 3 шт");
    expect(r1042.textContent).toContain("690 ₴");
    expect(r1042.textContent).toContain("Центр");
    const r1041 = rows().find((r) => r.textContent?.includes("№1041"))!;
    expect(r1041.textContent).toContain("Оплата в процесі");
    expect(rows().find((r) => r.textContent?.includes("№1040"))!.textContent).toContain("Очікує оплати");
  });

  it("owner бачить повний телефон", async () => {
    renderScreen();
    const r = (await screen.findAllByTestId("order-row")).find((x) => x.textContent?.includes("№1042"))!;
    expect(r.textContent).toContain("+380501234567");
    expect(within(r).queryByTestId("order-phone-masked")).toBeNull();
  });

  it("без clients.contacts=full - маска, для скрінрідера словами; повного номера в DOM немає", async () => {
    setRole("specialist");
    renderScreen();
    const r = (await screen.findAllByTestId("order-row")).find((x) => x.textContent?.includes("№1042"))!;
    const masked = within(r).getByTestId("order-phone-masked");
    expect(masked.textContent).toContain("+••••••••••67");
    expect(masked.textContent).toContain("Телефон приховано, закінчується на 67");
    expect(document.body.textContent).not.toContain("+380501234567");
    // specialist бачить лише свій заклад
    expect(rowNumbers()).toEqual(["№1040", "№1041", "№1042"]);
    expect(rows().every((x) => x.textContent?.includes("Центр"))).toBe(true);
  });

  it("чіпи статусу: «Усі», «Видані», «Скасовані», «Повернені», «Прострочені»", async () => {
    const user = userEvent.setup();
    renderScreen();
    await screen.findAllByTestId("order-row");
    await user.click(screen.getByRole("button", { name: "Усі" }));
    await waitFor(() => expect(rows()).toHaveLength(8));
    await user.click(screen.getByRole("button", { name: "Видані" }));
    await waitFor(() => expect(rowNumbers()).toEqual(["№1039"]));
    expect(rows()[0]!.textContent).toContain("Видано");
    await user.click(screen.getByRole("button", { name: "Скасовані" }));
    await waitFor(() => expect(rowNumbers()).toEqual(["№1037"]));
    await user.click(screen.getByRole("button", { name: "Повернені" }));
    await waitFor(() => expect(rowNumbers()).toEqual(["№1038"]));
    expect(rows()[0]!.textContent).toContain("Повернено кошти");
    await user.click(screen.getByRole("button", { name: "Прострочені" }));
    await waitFor(() => expect(rowNumbers()).toEqual(["№1036"]));
    expect(rows()[0]!.textContent).toContain("Прострочено");
  });

  it("фільтр закладу й період", async () => {
    const user = userEvent.setup();
    renderScreen();
    await screen.findAllByTestId("order-row");
    await user.click(screen.getByRole("button", { name: "Усі" }));
    await waitFor(() => expect(rows()).toHaveLength(8));
    await user.click(screen.getByRole("button", { name: "Поділ" }));
    await waitFor(() => expect(rows().every((r) => r.textContent?.includes("Поділ"))).toBe(true));
    expect(rowNumbers()).toEqual(expect.arrayContaining(["№1039", "№1036", "№1034"]));
    await user.click(screen.getByRole("button", { name: "Усі заклади" }));
    await user.type(screen.getByLabelText("Створено від"), "2026-10-14");
    await waitFor(() => expect(rowNumbers()).not.toContain("№1038"));
    expect(rowNumbers()).toContain("№1042");
  });

  it("помилковий період: повідомлення, запиту немає", async () => {
    const spy = vi.fn(orderApi().getProductOrders);
    patch({ getProductOrders: spy });
    const user = userEvent.setup();
    renderScreen();
    await screen.findAllByTestId("order-row");
    spy.mockClear();
    await user.type(screen.getByLabelText("Створено від"), "2026-10-14");
    await user.type(screen.getByLabelText("Створено до"), "2026-10-01");
    expect(await screen.findByText(/«Від» не пізніше за «До»/)).toBeInTheDocument();
    expect(spy).not.toHaveBeenCalledWith(expect.objectContaining({ from: "2026-10-14T00:00:00Z", to: "2026-10-01T23:59:59Z" }));
  });

  it("пошук за номером (№1042) з фільтром «Усі»; порожній результат; некоректний ввід", async () => {
    const user = userEvent.setup();
    renderScreen();
    await screen.findAllByTestId("order-row");
    await user.click(screen.getByRole("button", { name: "Усі" }));
    await user.type(screen.getByLabelText("Номер замовлення"), "№1038");
    await user.click(screen.getByRole("button", { name: "Знайти" }));
    await waitFor(() => expect(rowNumbers()).toEqual(["№1038"]));
    await user.clear(screen.getByLabelText("Номер замовлення"));
    await user.type(screen.getByLabelText("Номер замовлення"), "9999");
    await user.click(screen.getByRole("button", { name: "Знайти" }));
    expect(await screen.findByText("Замовлення №9999 за цими умовами не знайдено.")).toBeInTheDocument();
    await user.clear(screen.getByLabelText("Номер замовлення"));
    await user.type(screen.getByLabelText("Номер замовлення"), "abc");
    await user.click(screen.getByRole("button", { name: "Знайти" }));
    expect(await screen.findByText("Вкажіть номер цифрами, наприклад 1042.")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Скинути" }));
    await waitFor(() => expect(rows()).toHaveLength(8));
  });

  it("порожній стан для open", async () => {
    patch({ getProductOrders: async () => ({ items: [], hasMore: false }) });
    renderScreen();
    expect(await screen.findByText("Оплачених замовлень до видачі немає.")).toBeInTheDocument();
  });

  it("помилка завантаження - QueryState з повтором", async () => {
    patch({
      getProductOrders: async () => {
        throw new BeautyApiError(422, "invalid_cursor", "x");
      },
    });
    renderScreen();
    expect(await screen.findByText("Не вдалося завантажити дані.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Спробувати ще раз" })).toBeInTheDocument();
  });

  it("пагінація курсором: «Показати ще» довантажує", async () => {
    const real = orderApi().getProductOrders;
    patch({ getProductOrders: (q: object) => real({ ...q, take: 3 }) });
    const user = userEvent.setup();
    renderScreen();
    await screen.findAllByTestId("order-row");
    await user.click(screen.getByRole("button", { name: "Усі" }));
    await waitFor(() => expect(rows()).toHaveLength(3));
    expect(screen.getByText("Показано: 3")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Показати ще" }));
    await waitFor(() => expect(rows()).toHaveLength(6));
    await user.click(screen.getByRole("button", { name: "Показати ще" }));
    await waitFor(() => expect(rows()).toHaveLength(8));
    expect(screen.queryByRole("button", { name: "Показати ще" })).toBeNull();
  });
});

describe("екран замовлень: деталі й права", () => {
  it("деталь: рядки (знімок), клієнт, оплата, дії для owner", async () => {
    const user = userEvent.setup();
    renderScreen();
    const dlg = await openOrder(user, 1042);
    expect(within(dlg).getByRole("heading", { name: "Замовлення №1042" })).toBeInTheDocument();
    const lines = await within(dlg).findByRole("table", { name: "Склад замовлення №1042" });
    expect(lines.textContent).toContain("Шампунь професійний 500 мл");
    expect(lines.textContent).toContain("Олія для кутикули");
    expect(lines.textContent).toContain("240 ₴");
    expect(dlg.textContent).toContain("Олена Мельник");
    expect(dlg.textContent).toContain("+380501234567");
    expect(dlg.textContent).toContain("Оплачено, спроб: 1");
    expect(dlg.textContent).toContain("Разом: 690 ₴");
    expect(within(dlg).getByRole("button", { name: "Видати" })).toBeInTheDocument();
    expect(within(dlg).getByRole("button", { name: "Скасувати" })).toBeInTheDocument();
    expect(within(dlg).queryByRole("button", { name: "Перевірити оплату" })).toBeNull();
  });

  it("фокус у діалозі на заголовку; Esc закриває", async () => {
    const user = userEvent.setup();
    renderScreen();
    const dlg = await openOrder(user, 1042);
    expect(within(dlg).getByRole("heading", { name: "Замовлення №1042" })).toHaveFocus();
    await user.keyboard("{Escape}");
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
  });

  it("specialist (inventory.manage = none): деталь є, кнопок дій немає", async () => {
    setRole("specialist");
    const user = userEvent.setup();
    renderScreen();
    const dlg = await openOrder(user, 1042);
    await within(dlg).findByRole("table", { name: "Склад замовлення №1042" });
    for (const name of ["Видати", "Скасувати", "Перевірити оплату"]) expect(within(dlg).queryByRole("button", { name })).toBeNull();
    expect(within(dlg).getByRole("button", { name: "Закрити" })).toBeInTheDocument();
  });

  it("немає canManage у закладі за /scope - кнопок дій немає", async () => {
    const real = orderApi().getInventoryScope as () => Promise<{ locations: { canManage: boolean }[] }>;
    patch({ getInventoryScope: async () => ({ ...(await real()), locations: (await real()).locations.map((l) => ({ ...l, canManage: false })) }) });
    const user = userEvent.setup();
    renderScreen();
    const dlg = await openOrder(user, 1042);
    await within(dlg).findByRole("table", { name: "Склад замовлення №1042" });
    expect(within(dlg).queryByRole("button", { name: "Видати" })).toBeNull();
    expect(within(dlg).queryByRole("button", { name: "Скасувати" })).toBeNull();
  });

  it("неоплачене: «Видати» немає, «Скасувати» є", async () => {
    const user = userEvent.setup();
    renderScreen();
    const dlg = await openOrder(user, 1040);
    await within(dlg).findByRole("table", { name: "Склад замовлення №1040" });
    expect(within(dlg).queryByRole("button", { name: "Видати" })).toBeNull();
    expect(within(dlg).getByRole("button", { name: "Скасувати" })).toBeInTheDocument();
  });
});

describe("екран замовлень: дії", () => {
  it("«Видати»: підтвердження, результат у aria-live, замовлення зникає з open, лічильник оновлюється", async () => {
    const user = userEvent.setup();
    renderScreen();
    const dlg = await openOrder(user, 1042);
    await user.click(await within(dlg).findByRole("button", { name: "Видати" }));
    expect(within(dlg).getByText(/Підтвердьте, що товар передано клієнту/)).toBeInTheDocument();
    await user.click(within(dlg).getByRole("button", { name: "Підтвердити видачу" }));
    const status = await within(dlg).findByText("Замовлення №1042 видано клієнту.");
    expect(status).toHaveAttribute("role", "status");
    expect(dlg.textContent).toContain("Видано");
    expect(within(dlg).queryByRole("button", { name: "Видати" })).toBeNull();
    await user.click(within(dlg).getByRole("button", { name: "Закрити" }));
    await waitFor(() => expect(rowNumbers()).not.toContain("№1042"));
  });

  it("«Видати» - помилка сервера показується українською (order_closed)", async () => {
    patch({
      fulfillProductOrder: async () => {
        throw new BeautyApiError(409, "order_closed", "x");
      },
    });
    const user = userEvent.setup();
    renderScreen();
    const dlg = await openOrder(user, 1042);
    await user.click(await within(dlg).findByRole("button", { name: "Видати" }));
    await user.click(within(dlg).getByRole("button", { name: "Підтвердити видачу" }));
    expect(await within(dlg).findByRole("alert")).toHaveTextContent("Замовлення вже закрите");
    expect(within(dlg).getByRole("button", { name: "Видати" })).toBeInTheDocument();
  });

  it("«Скасувати» оплаченого: пояснення про повне повернення, причина, підтвердження", async () => {
    const user = userEvent.setup();
    renderScreen();
    const dlg = await openOrder(user, 1042);
    await user.click(await within(dlg).findByRole("button", { name: "Скасувати" }));
    expect(within(dlg).getByText("Кошти 690 ₴ буде повернено клієнту повністю, товар повернеться на склад.")).toBeInTheDocument();
    await user.type(within(dlg).getByLabelText("Причина (необов’язково)"), "Клієнт передумав");
    await user.click(within(dlg).getByRole("button", { name: "Скасувати й повернути 690 ₴" }));
    expect(await within(dlg).findByText(/Клієнту буде повернено 690 ₴, товар повернено на склад/)).toHaveAttribute("role", "status");
    expect(dlg.textContent).toContain("Повернено кошти");
    expect(dlg.textContent).toContain("Клієнт передумав");
    expect(within(dlg).queryByRole("button", { name: "Скасувати" })).toBeNull();
  });

  it("«Скасувати» неоплаченого: інше пояснення; «Назад» повертає до перегляду", async () => {
    const user = userEvent.setup();
    renderScreen();
    const dlg = await openOrder(user, 1040);
    await user.click(await within(dlg).findByRole("button", { name: "Скасувати" }));
    expect(within(dlg).getByText(/резерв товару буде знято/)).toBeInTheDocument();
    expect(within(dlg).getByRole("button", { name: "Скасувати замовлення" })).toBeInTheDocument();
    await user.click(within(dlg).getByRole("button", { name: "Назад" }));
    expect(within(dlg).getByRole("button", { name: "Скасувати" })).toBeInTheDocument();
    await user.click(within(dlg).getByRole("button", { name: "Скасувати" }));
    await user.click(within(dlg).getByRole("button", { name: "Скасувати замовлення" }));
    expect(await within(dlg).findByText("Замовлення скасовано.")).toBeInTheDocument();
    expect(dlg.textContent).toContain("Скасовано");
  });

  it("причина довша за 300 символів блокує підтвердження", async () => {
    const user = userEvent.setup();
    renderScreen();
    const dlg = await openOrder(user, 1042);
    await user.click(await within(dlg).findByRole("button", { name: "Скасувати" }));
    const field = within(dlg).getByLabelText("Причина (необов’язково)");
    await user.click(field);
    await user.paste("я".repeat(301));
    expect(field).toHaveAttribute("aria-invalid", "true");
    expect(within(dlg).getByText("Причина: до 300 символів.")).toBeInTheDocument();
    expect(within(dlg).getByRole("button", { name: "Скасувати й повернути 690 ₴" })).toBeDisabled();
  });

  it("refund_failed: помилка, замовлення лишається оплаченим; повтор успішний", async () => {
    const user = userEvent.setup();
    renderScreen();
    const dlg = await openOrder(user, 1034);
    await user.click(await within(dlg).findByRole("button", { name: "Скасувати" }));
    await user.click(within(dlg).getByRole("button", { name: /Скасувати й повернути 830/ }));
    expect(await within(dlg).findByRole("alert")).toHaveTextContent("Не вдалося повернути кошти");
    expect(dlg.textContent).toContain("Оплачено, до видачі");
    await user.click(within(dlg).getByRole("button", { name: "Скасувати" }));
    await user.click(within(dlg).getByRole("button", { name: /Скасувати й повернути 830/ }));
    expect(await within(dlg).findByText(/Клієнту буде повернено 830 ₴/)).toBeInTheDocument();
    expect(within(dlg).queryByRole("alert")).toBeNull();
  });

  it("«Перевірити оплату»: лише при оплаті в процесі, скасування приховано; результат озвучується", async () => {
    const user = userEvent.setup();
    renderScreen();
    const dlg = await openOrder(user, 1041);
    await within(dlg).findByRole("table", { name: "Склад замовлення №1041" });
    expect(within(dlg).queryByRole("button", { name: "Скасувати" })).toBeNull();
    expect(within(dlg).getByText(/Скасувати замовлення можна після перевірки оплати/)).toBeInTheDocument();
    await user.click(within(dlg).getByRole("button", { name: "Перевірити оплату" }));
    expect(await within(dlg).findByText(/Оплату підтверджено/)).toHaveAttribute("role", "status");
    expect(within(dlg).queryByRole("button", { name: "Перевірити оплату" })).toBeNull();
    expect(within(dlg).getByRole("button", { name: "Видати" })).toBeInTheDocument();
  });

  it("reconcile: помилка nothing_to_reconcile", async () => {
    const real = orderApi().getProductOrder as (id: string) => Promise<unknown>;
    patch({
      getProductOrder: async (id: string) => ({ ...(await real(id)) as object }),
      reconcileProductOrderPayment: async () => {
        throw new BeautyApiError(409, "nothing_to_reconcile", "x");
      },
    });
    const user = userEvent.setup();
    renderScreen();
    const dlg = await openOrder(user, 1041);
    await user.click(await within(dlg).findByRole("button", { name: "Перевірити оплату" }));
    expect(await within(dlg).findByRole("alert")).toHaveTextContent("Перевіряти нічого");
  });
});

describe("навігація складу: чип «Замовлення» з бейджем", () => {
  it("чип у навігації з посиланням; бейдж = counts.paid, підпис для скрінрідера", async () => {
    renderScreen();
    const link = await screen.findByRole("link", { name: /Замовлення/ });
    expect(link).toHaveAttribute("href", "/beauty/inventory/orders");
    expect(link).toHaveAttribute("aria-current", "page");
    const badge = await screen.findByTestId("orders-badge");
    expect(badge.textContent).toBe("2, оплачених до видачі: 2");
  });

  it("«99+» понад 99, точне число для скрінрідера як «понад 99»", async () => {
    patch({ getProductOrderCounts: async () => ({ paid: 250 }) });
    renderScreen();
    const badge = await screen.findByTestId("orders-badge");
    expect(within(badge).getByText("99+")).toHaveAttribute("aria-hidden", "true");
    expect(badge.textContent).toContain("понад 99");
  });

  it("нуль - без бейджа", async () => {
    patch({ getProductOrderCounts: async () => ({ paid: 0 }) });
    renderScreen();
    await screen.findByRole("link", { name: /Замовлення/ });
    await new Promise((r) => setTimeout(r, 20));
    expect(screen.queryByTestId("orders-badge")).toBeNull();
  });

  it("без inventory.view - екрана й запитів немає", async () => {
    const spy = vi.fn(async () => ({ paid: 5 }));
    patch({ getProductOrderCounts: spy });
    auth.user.permissions = { ...ROLE_PRESETS.owner, "inventory.view": "none" };
    renderScreen();
    expect(await screen.findByText(/недоступний/)).toBeInTheDocument();
    expect(spy).not.toHaveBeenCalled();
  });
});
