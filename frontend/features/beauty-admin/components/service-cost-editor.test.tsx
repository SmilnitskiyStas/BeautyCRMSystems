import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { BeautyApiError } from "@/features/beauty-auth/errors";
import type { ServiceCostDetail, ServiceCostRow } from "../types";

const role = { current: "owner" };
const getServiceCosts = vi.fn<() => Promise<ServiceCostRow[]>>();
const getServiceCostDetail = vi.fn<(id: string) => Promise<ServiceCostDetail>>();
const setServiceCost = vi.fn();
const deleteServiceCost = vi.fn();
const getPriceList = vi.fn();
const getLocations = vi.fn();
vi.mock("../api", () => ({
  beautyApi: {
    getServiceCosts: () => getServiceCosts(),
    getServiceCostDetail: (id: string) => getServiceCostDetail(id),
    setServiceCost: (...a: unknown[]) => setServiceCost(...a),
    deleteServiceCost: (...a: unknown[]) => deleteServiceCost(...a),
    getPriceList: () => getPriceList(),
    getLocations: () => getLocations(),
  },
}));
vi.mock("@/features/beauty-auth/components/auth-provider", () => ({ useAuth: () => ({ user: { id: "u", email: "e", fullName: "n", role: role.current } }) }));

import { ServicesScreen } from "./services-screen";

const cost = (costAmount: number, masterPercent: number | null) => ({ costAmount, masterPercent, updatedAt: "2026-09-01T10:00:00Z", updatedBy: { id: "u", name: "Світлана Коваленко" } });
const detail = (): ServiceCostDetail => ({
  serviceId: "mn",
  network: cost(220, 40),
  locations: [{ ...cost(260, null), locationId: "k", locationName: "Печерськ" }],
});
const rows = (): ServiceCostRow[] => [
  { ...detail(), serviceName: "Манікюр + гель-лак", isActive: true },
  { serviceId: "br", serviceName: "Чоловіча стрижка", isActive: true, locations: [] },
];

function renderScreen() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <ServicesScreen />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  role.current = "owner";
  getServiceCosts.mockReset().mockImplementation(async () => rows());
  getServiceCostDetail.mockReset().mockImplementation(async () => detail());
  setServiceCost.mockReset().mockResolvedValue({ ...cost(1, null), locationId: null });
  deleteServiceCost.mockReset().mockResolvedValue(undefined);
  getPriceList.mockReset().mockResolvedValue([
    { serviceId: "mn", name: "Манікюр + гель-лак", durationMinutes: 90, price: 900 },
    { serviceId: "br", name: "Чоловіча стрижка", durationMinutes: 45, price: 500 },
  ]);
  getLocations.mockReset().mockResolvedValue([
    { id: "c", name: "Центр", timezone: "Europe/Kyiv" },
    { id: "p", name: "Поділ", timezone: "Europe/Kyiv" },
    { id: "k", name: "Печерськ", timezone: "Europe/Kyiv" },
  ]);
});

async function openEditor(user = userEvent.setup()) {
  renderScreen();
  await user.click(await screen.findByRole("button", { name: "Собівартість: Манікюр + гель-лак" }));
  const net = await screen.findByRole("form", { name: "Для всієї мережі" });
  return { user, net };
}

describe("§24.8.2: список послуг", () => {
  it("колонка «Собівартість»: значення, «не задано», кількість override", async () => {
    renderScreen();
    const list = await screen.findAllByTestId("service-row");
    expect(list).toHaveLength(2);
    expect(within(list[0]).getByTestId("service-cost-cell")).toHaveTextContent("220 ₴ · майстру 40%");
    expect(within(list[0]).getByText("окремо: 1")).toBeInTheDocument();
    expect(within(list[0]).getByText("900 ₴")).toBeInTheDocument();
    expect(within(list[1]).getByTestId("service-cost-cell")).toHaveTextContent("не задано");
  });

  it("specialist не бачить розділ і запитів не робить", async () => {
    role.current = "specialist";
    renderScreen();
    expect(screen.getByRole("note")).toHaveTextContent("лише власнику й адміністратору");
    await Promise.resolve();
    expect(getServiceCosts).not.toHaveBeenCalled();
    expect(getServiceCostDetail).not.toHaveBeenCalled();
    expect(screen.queryByRole("button", { name: /Собівартість:/ })).toBeNull();
  });

  it("admin редагує так само, як owner", async () => {
    role.current = "admin";
    renderScreen();
    expect(await screen.findByRole("button", { name: "Собівартість: Манікюр + гель-лак" })).toBeInTheDocument();
  });
});

describe("§24.8.2: редактор", () => {
  it("підказка про незмінність минулих записів, поточні значення, «Оновлено {ім'я}, {дата}»", async () => {
    const { net } = await openEditor();
    expect(screen.getByText("Діє на записи, завершені після збереження; минулі записи не змінюються.")).toBeInTheDocument();
    expect(within(net).getByLabelText("Собівартість, ₴")).toHaveValue("220");
    expect(within(net).getByLabelText("Відсоток майстру, %")).toHaveValue("40");
    expect(within(net).getByText(/Оновлено Світлана Коваленко, 1 вер/)).toBeInTheDocument();
    const ov = screen.getByRole("form", { name: "Заклад: Печерськ" });
    expect(within(ov).getByLabelText("Собівартість, ₴")).toHaveValue("260");
    expect(within(ov).getByLabelText("Відсоток майстру, %")).toHaveValue("");
  });

  it("клієнтська валідація: помилки під полями, запиту немає", async () => {
    const { user, net } = await openEditor();
    const c = within(net).getByLabelText("Собівартість, ₴");
    const p = within(net).getByLabelText("Відсоток майстру, %");
    await user.clear(c);
    await user.type(c, "1,234");
    await user.click(within(net).getByRole("button", { name: "Зберегти" }));
    expect(within(net).getByText("Не більше 2 знаків після коми.")).toBeInTheDocument();
    expect(c).toHaveAttribute("aria-invalid", "true");
    await user.clear(c);
    await user.type(c, "-5");
    await user.clear(p);
    await user.type(p, "101");
    await user.click(within(net).getByRole("button", { name: "Зберегти" }));
    expect(within(net).getByText("Собівартість не може бути від’ємною.")).toBeInTheDocument();
    expect(within(net).getByText("Відсоток не може перевищувати 100.")).toBeInTheDocument();
    expect(setServiceCost).not.toHaveBeenCalled();
  });

  it("збереження мережі: кома, відсоток; повідомлення про успіх", async () => {
    const { user, net } = await openEditor();
    const c = within(net).getByLabelText("Собівартість, ₴");
    const p = within(net).getByLabelText("Відсоток майстру, %");
    await user.clear(c);
    await user.type(c, "250,50");
    await user.clear(p);
    await user.type(p, "33,33");
    await user.click(within(net).getByRole("button", { name: "Зберегти" }));
    await waitFor(() => expect(setServiceCost).toHaveBeenCalledWith("mn", { locationId: null, costAmount: 250.5, masterPercent: 33.33 }));
    expect(await within(net).findByRole("status")).toHaveTextContent("Збережено. Діє на записи, завершені після цього моменту.");
  });

  it("порожній відсоток = null (без виплати)", async () => {
    const { user, net } = await openEditor();
    await user.clear(within(net).getByLabelText("Відсоток майстру, %"));
    await user.click(within(net).getByRole("button", { name: "Зберегти" }));
    await waitFor(() => expect(setServiceCost).toHaveBeenCalledWith("mn", { locationId: null, costAmount: 220, masterPercent: null }));
  });

  it("422 від сервера показується під відповідним полем", async () => {
    setServiceCost.mockRejectedValueOnce(new BeautyApiError(422, "invalid_master_percent", "x"));
    const { user, net } = await openEditor();
    const p = within(net).getByLabelText("Відсоток майстру, %");
    await user.clear(p);
    await user.type(p, "50");
    await user.click(within(net).getByRole("button", { name: "Зберегти" }));
    const err = await within(net).findByText("Відсоток майстру має бути від 0 до 100, не більше 2 знаків після коми.");
    expect(err).toHaveAttribute("role", "alert");
    expect(p).toHaveAccessibleDescription(/Відсоток майстру має бути/);
  });

  it("403/інша помилка - над кнопками, не під полем", async () => {
    setServiceCost.mockRejectedValueOnce(new BeautyApiError(429, "rate_limited", "x"));
    const { user, net } = await openEditor();
    const c = within(net).getByLabelText("Собівартість, ₴");
    await user.clear(c);
    await user.type(c, "10");
    await user.click(within(net).getByRole("button", { name: "Зберегти" }));
    expect(await within(net).findByText("Забагато запитів. Зачекайте хвилину і спробуйте ще раз.")).toBeInTheDocument();
  });

  it("попередження, якщо собівартість + виплата більші за ціну", async () => {
    const { user, net } = await openEditor();
    expect(within(net).queryByTestId("loss-warning")).toBeNull();
    const c = within(net).getByLabelText("Собівартість, ₴");
    await user.clear(c);
    await user.type(c, "700");
    expect(within(net).getByTestId("loss-warning")).toHaveTextContent("Собівартість і виплата майстру (1 060 ₴) більші за ціну 900 ₴");
  });

  it("override закладу: додати для вільного закладу (мережевий і зайняті не пропонуються)", async () => {
    const { user } = await openEditor();
    await user.click(screen.getByRole("button", { name: "Додати для закладу" }));
    const select = screen.getByLabelText("Заклад");
    expect(within(select).queryByRole("option", { name: "Печерськ" })).toBeNull();
    await user.selectOptions(select, "p");
    const form = await screen.findByRole("form", { name: "Заклад: Поділ" });
    await user.type(within(form).getByLabelText("Собівартість, ₴"), "180");
    await user.type(within(form).getByLabelText("Відсоток майстру, %"), "20");
    await user.click(within(form).getByRole("button", { name: "Зберегти" }));
    await waitFor(() => expect(setServiceCost).toHaveBeenCalledWith("mn", { locationId: "p", costAmount: 180, masterPercent: 20 }));
  });

  it("видалення override - лише після підтвердження; «Скасувати» нічого не видаляє", async () => {
    const { user } = await openEditor();
    const ov = screen.getByRole("form", { name: "Заклад: Печерськ" });
    await user.click(within(ov).getByRole("button", { name: "Видалити: Заклад: Печерськ" }));
    const dlg = within(ov).getByRole("alertdialog", { name: "Видалити значення для закладу?" });
    expect(dlg).toHaveTextContent("Минулі записи не змінюються.");
    await user.click(within(dlg).getByRole("button", { name: "Скасувати" }));
    expect(deleteServiceCost).not.toHaveBeenCalled();
    await user.click(within(ov).getByRole("button", { name: "Видалити: Заклад: Печерськ" }));
    await user.click(within(ov).getByRole("button", { name: "Видалити" }));
    await waitFor(() => expect(deleteServiceCost).toHaveBeenCalledWith("mn", "k"));
  });

  it("очищення мережевого значення теж за підтвердженням", async () => {
    const { user, net } = await openEditor();
    await user.click(within(net).getByRole("button", { name: "Видалити: Для всієї мережі" }));
    expect(within(net).getByRole("alertdialog", { name: "Видалити мережеву собівартість?" })).toBeInTheDocument();
    await user.click(within(net).getByRole("button", { name: "Видалити" }));
    await waitFor(() => expect(deleteServiceCost).toHaveBeenCalledWith("mn", null));
  });

  it("повідомлення «Збережено» не зникає, коли після збереження приходить оновлене серверне значення", async () => {
    let net = cost(220, 40);
    getServiceCostDetail.mockImplementation(async () => ({ ...detail(), network: net }));
    setServiceCost.mockImplementation(async (_id: string, input: { costAmount: number; masterPercent: number | null }) => {
      net = { ...cost(input.costAmount, input.masterPercent), updatedAt: "2026-10-26T10:00:00Z" };
      return { ...net, locationId: null };
    });
    const { user, net: form } = await openEditor();
    const c = within(form).getByLabelText("Собівартість, ₴");
    await user.clear(c);
    await user.type(c, "300");
    await user.click(within(form).getByRole("button", { name: "Зберегти" }));
    expect(await within(form).findByRole("status")).toHaveTextContent("Збережено");
    await waitFor(() => expect(getServiceCostDetail.mock.calls.length).toBeGreaterThan(1));
    expect(within(form).getByRole("status")).toBeInTheDocument();
    expect(within(form).getByLabelText("Собівартість, ₴")).toHaveValue("300");
    expect(within(form).getByRole("button", { name: "Зберегти" })).toBeDisabled(); // змін немає
  });

  it("Закрити повертає фокус на кнопку рядка", async () => {
    const { user } = await openEditor();
    await user.click(screen.getByRole("button", { name: "Закрити" }));
    await waitFor(() => expect(screen.getByRole("button", { name: "Собівартість: Манікюр + гель-лак" })).toHaveFocus());
    expect(screen.queryByRole("form", { name: "Для всієї мережі" })).toBeNull();
  });
});
