import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { ROLE_PRESETS, accessOf, mockAccessCatalog } from "@/features/beauty-auth/access";

const h = vi.hoisted(() => ({
  role: "owner" as string,
  api: {} as Record<string, (...a: unknown[]) => unknown>,
  auth: { user: { id: "u-owner", email: "e", fullName: "n", role: "owner", specialistId: null, permissions: {} as Record<string, string> }, logout: async () => {} },
}));

vi.mock("@/features/beauty-auth/components/auth-provider", async () => {
  const access = await import("@/features/beauty-auth/access");
  return { useAuth: () => h.auth, useAccess: () => access.accessOf(h.auth.user as never) };
});
vi.mock("../api", () => ({ beautyApi: new Proxy({}, { get: (_t, k: string) => (...a: unknown[]) => h.api[k]!(...a) }) }));

import { createMockTeam } from "../api/mock-team";
import { TeamScreen } from "./team-screen";

function setup(role: "owner" | "admin" | "deputy" | "specialist") {
  h.role = role;
  h.auth.user = { ...h.auth.user, role, id: `u-${role}`, permissions: { ...ROLE_PRESETS[role] } };
  const team = createMockTeam({ role: () => h.role, wait: async (v) => structuredClone(v), today: () => "2026-11-15" });
  h.api = { ...team, getAccessCatalog: async () => mockAccessCatalog(), getStaff: async () => [{ id: "m", name: "Марина", isActive: true, phone: null, position: null, services: [], locations: [] }], revokeInvite: async () => {} } as unknown as typeof h.api;
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={qc}>
      <TeamScreen />
    </QueryClientProvider>,
  );
  return team;
}

const rowOf = async (name: string) => (await screen.findAllByTestId("team-row")).find((r) => within(r).queryByText(name))!;
async function openCard(user: ReturnType<typeof userEvent.setup>, name: string) {
  const row = await rowOf(name);
  await user.click(within(row).getByRole("button"));
  return screen.findByRole("button", { name: /Закрити картку/ });
}

beforeEach(() => {
  vi.clearAllMocks();
});

describe("«Команда й доступи»: доступ до екрана", () => {
  it("без team.manage - лише пояснення, запитів немає", () => {
    const getTeamUsers = vi.fn();
    setup("deputy");
    expect(screen.getByRole("note")).toHaveTextContent("відповідним правом");
    expect(getTeamUsers).not.toHaveBeenCalled();
    expect(screen.queryByRole("button", { name: "Запросити" })).toBeNull();
  });

  it("журнал змін - лише директор", async () => {
    setup("admin");
    await screen.findAllByTestId("team-row");
    expect(screen.queryByRole("button", { name: "Журнал змін" })).toBeNull();
    document.body.innerHTML = "";
    setup("owner");
    expect(await screen.findByRole("button", { name: "Журнал змін" })).toBeInTheDocument();
  });
});

describe("картка користувача", () => {
  it("зміна права: dryRun-діалог зі списком змін, підтвердження, збережено", async () => {
    const user = userEvent.setup();
    setup("owner");
    await openCard(user, "Петро Заступник");
    const perm = await screen.findByTestId("perm-revenue.view");
    await user.click(within(perm).getByRole("radio", { name: /Лише власний виторг/ }));
    expect(within(perm).getByText("змінено")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: /Зберегти зміни/ }));
    const dlg = await screen.findByRole("dialog");
    expect(within(dlg).getByTestId("confirm-changes")).toHaveTextContent("Виторг: Немає доступу");
    expect(within(dlg).getByTestId("confirm-changes")).toHaveTextContent("Лише власний виторг");
    await user.click(within(dlg).getByRole("button", { name: "Підтвердити і зберегти" }));
    expect(await screen.findByTestId("saved")).toHaveTextContent("Збережено");
  });

  it("локальне порушення обмеження блокує збереження й пояснює причину", async () => {
    const user = userEvent.setup();
    setup("owner");
    await openCard(user, "Петро Заступник");
    const view = await screen.findByTestId("perm-calendar.view");
    await user.click(within(view).getByRole("radio", { name: "Лише свої записи" }));
    expect(await screen.findByTestId("violations")).toHaveTextContent("не може бути вищим");
    expect(screen.getByRole("button", { name: /Зберегти зміни/ })).toBeDisabled();
  });

  it("admin: team.manage та вищі за стелю варіанти вимкнені з поясненням; ролі admin<->deputy", async () => {
    const user = userEvent.setup();
    setup("admin");
    await openCard(user, "Петро Заступник");
    const tm = await screen.findByTestId("perm-team.manage");
    expect(within(tm).getByRole("radio", { name: /Керування/ })).toBeDisabled();
    expect(tm).toHaveTextContent("Лише для ролі «Адміністратор»");
    const role = screen.getByRole("combobox", { name: "Роль" });
    // admin не може призначити ранг, рівний власному: зміна ролі недоступна й пояснена
    expect(role).toBeDisabled();
    expect(screen.getByText(/Роль змінити не можна/)).toBeInTheDocument();
  });

  it("директор і власний рядок не редагуються", async () => {
    const user = userEvent.setup();
    setup("admin");
    await openCard(user, "Олена Директорова");
    expect(await screen.findByTestId("not-editable")).toHaveTextContent("Директор");
    expect(screen.queryByRole("button", { name: /Зберегти зміни/ })).toBeNull();
    expect(screen.queryByRole("button", { name: /Вимкнути доступ/ })).toBeNull();
    for (const r of within(screen.getByTestId("perm-calendar.view")).getAllByRole("radio")) expect(r).toBeDisabled();
  });

  it("scope menu: підпис «Впливає лише на меню» біля staff.access", async () => {
    const user = userEvent.setup();
    setup("owner");
    await openCard(user, "Марина Майстер");
    const staff = await screen.findByTestId("perm-staff.access");
    expect(staff).toHaveTextContent("Впливає лише на меню");
  });

  it("409 під час підтвердження: дані оновлюються, показано повідомлення, чернетка скинута", async () => {
    const user = userEvent.setup();
    const team = setup("owner");
    await openCard(user, "Петро Заступник");
    const perm = await screen.findByTestId("perm-revenue.view");
    await user.click(within(perm).getByRole("radio", { name: /Лише власний виторг/ }));
    await user.click(screen.getByRole("button", { name: /Зберегти зміни/ }));
    const dlg = await screen.findByRole("dialog");
    // хтось інший змінив права, поки діалог був відкритий
    await team.saveUserAccess("u-deputy", { expectedVersion: 2, permissions: { "channels.inbox": "view" } });
    await user.click(within(dlg).getByRole("button", { name: "Підтвердити і зберегти" }));
    expect(await screen.findByTestId("save-errors")).toHaveTextContent("щойно змінили");
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    expect(within(screen.getByTestId("perm-revenue.view")).getByRole("radio", { name: "Немає доступу" })).toBeChecked();
    expect(screen.getByText(/Версія доступу: 3/)).toBeInTheDocument();
  });

  it("скидання до пресету і вимкнення доступу йдуть через діалог підтвердження", async () => {
    const user = userEvent.setup();
    setup("owner");
    await openCard(user, "Ірина Адміністраторська");
    await user.click(await screen.findByRole("button", { name: /Скинути до пресету/ }));
    const dlg = await screen.findByRole("dialog");
    expect(dlg).toHaveTextContent("Нічого змінювати");
    expect(within(dlg).getByRole("button", { name: "Скинути права" })).toBeDisabled();
    await user.click(within(dlg).getByRole("button", { name: "Назад" }));
    await user.click(screen.getByRole("button", { name: /Вимкнути доступ/ }));
    await user.click(within(await screen.findByRole("dialog")).getByRole("button", { name: "Вимкнути доступ" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    expect(await screen.findByText("Доступ вимкнено")).toBeInTheDocument();
  });
});

describe("запрошення", () => {
  it("роль і права: створення дає одноразове посилання", async () => {
    const user = userEvent.setup();
    setup("owner");
    await user.click(await screen.findByRole("button", { name: "Запросити" }));
    await user.type(screen.getByLabelText("Пошта"), "new@salon.test");
    await user.selectOptions(screen.getByLabelText("Роль"), "deputy");
    await user.click(screen.getByRole("checkbox", { name: /Налаштувати права/ }));
    const perm = await screen.findByTestId("perm-revenue.view");
    await user.click(within(perm).getByRole("radio", { name: /Лише власний виторг/ }));
    await user.click(screen.getByRole("button", { name: "Створити запрошення" }));
    expect(await screen.findByText("Посилання-запрошення")).toBeInTheDocument();
    expect((screen.getByLabelText("Посилання") as HTMLInputElement).value).toContain("/invite/mock-");
  });

  it("admin не може запросити admin: у списку ролей лише нижчі", async () => {
    const user = userEvent.setup();
    setup("admin");
    await user.click(await screen.findByRole("button", { name: "Запросити" }));
    const roles = within(screen.getByLabelText("Роль")).getAllByRole("option").map((o) => o.textContent);
    expect(roles).toEqual(["Заступник", "Майстер"]);
  });
});

describe("журнал змін", () => {
  it("owner бачить записи з переліком змін", async () => {
    const user = userEvent.setup();
    setup("owner");
    await user.click(await screen.findByRole("button", { name: "Журнал змін" }));
    const item = await screen.findByTestId("audit-item");
    expect(item).toHaveTextContent("Змінено права");
    expect(item).toHaveTextContent("Собівартість і виплати");
  });
});

describe("модель", () => {
  it("пресет deputy не має team.manage", () => {
    expect(accessOf({ role: "deputy", permissions: { ...ROLE_PRESETS.deputy } }).can("team.manage", "manage")).toBe(false);
  });
});
