import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { BeautyApiError } from "@/features/beauty-auth/errors";
import type { BeautyLocation, ServiceCostRow, ServiceCreateInput, ServiceItem, StaffMember } from "../types";

const auth = { user: { id: "u", email: "e", fullName: "n", role: "owner", specialistId: null as string | null } };
vi.mock("@/features/beauty-auth/components/auth-provider", () => ({ useAuth: () => auth }));

const getServiceCosts = vi.fn<() => Promise<ServiceCostRow[]>>();
const getPriceList = vi.fn();
const getLocations = vi.fn<() => Promise<BeautyLocation[]>>();
const getStaff = vi.fn<() => Promise<StaffMember[]>>();
const getStaffProfile = vi.fn<(id: string) => Promise<StaffMember | null>>();
const createService = vi.fn<(i: ServiceCreateInput) => Promise<ServiceItem>>();
const setServicePrice = vi.fn<(id: string, p: number, l?: string | null) => Promise<void>>();
const setStaffServices = vi.fn<(id: string, ids: string[]) => Promise<void>>();
vi.mock("../api", () => ({
  beautyApi: {
    getServiceCosts: () => getServiceCosts(),
    getPriceList: () => getPriceList(),
    getLocations: () => getLocations(),
    getStaff: () => getStaff(),
    getStaffProfile: (id: string) => getStaffProfile(id),
    createService: (i: ServiceCreateInput) => createService(i),
    setServicePrice: (id: string, p: number, l?: string | null) => setServicePrice(id, p, l),
    setStaffServices: (id: string, ids: string[]) => setStaffServices(id, ids),
  },
}));

import { ServicesScreen } from "./services-screen";

const loc = (id: string, name: string): BeautyLocation => ({ id, name, address: null, phone: null, timezone: "Europe/Kyiv", isActive: true });
const master = (id: string, services: string[]): StaffMember => ({ id, name: `Майстер ${id}`, phone: null, position: null, isActive: true, services: services.map((s) => ({ id: s, name: s })), locations: [] });

function renderScreen() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <ServicesScreen />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  auth.user.role = "owner";
  getServiceCosts.mockReset().mockResolvedValue([]);
  getPriceList.mockReset().mockResolvedValue([]);
  getLocations.mockReset().mockResolvedValue([loc("L1", "Центр"), loc("L2", "Лівий берег")]);
  getStaff.mockReset().mockResolvedValue([master("a", ["s1"]), master("b", [])]);
  getStaffProfile.mockReset().mockImplementation(async (id) => (id === "a" ? master("a", ["s1"]) : master("b", [])));
  createService.mockReset().mockResolvedValue({ id: "new", name: "Стрижка", category: null, durationMinutes: 45 });
  setServicePrice.mockReset().mockResolvedValue();
  setStaffServices.mockReset().mockResolvedValue();
});

async function open() {
  const user = userEvent.setup();
  renderScreen();
  await user.click(await screen.findByRole("button", { name: "Нова послуга" }));
  return user;
}

describe("екран послуг: створення", () => {
  it("owner і admin бачать кнопку «Нова послуга»", async () => {
    renderScreen();
    expect(await screen.findByRole("button", { name: "Нова послуга" })).toBeInTheDocument();
    auth.user.role = "admin";
    renderScreen();
    expect((await screen.findAllByRole("button", { name: "Нова послуга" })).length).toBeGreaterThan(0);
  });

  it("specialist кнопки не бачить", () => {
    auth.user.role = "specialist";
    renderScreen();
    expect(screen.queryByRole("button", { name: "Нова послуга" })).toBeNull();
  });

  it("валідація: порожня форма не викликає API й показує помилки", async () => {
    const user = await open();
    await user.click(screen.getByRole("button", { name: "Створити послугу" }));
    expect(await screen.findByText("Вкажіть назву послуги.")).toBeInTheDocument();
    expect(screen.getByText("Вкажіть тривалість у хвилинах.")).toBeInTheDocument();
    expect(screen.getByText("Вкажіть мережеву ціну.")).toBeInTheDocument();
    expect(createService).not.toHaveBeenCalled();
  });

  it("валідація: дробова тривалість, нечислова ціна, некоректна ціна закладу", async () => {
    const user = await open();
    await user.type(screen.getByLabelText(/^Назва/), "Стрижка");
    await user.type(screen.getByLabelText(/^Тривалість/), "4.5");
    await user.type(screen.getByLabelText(/^Мережева ціна/), "abc");
    await user.type(await screen.findByLabelText(/Ціна: Центр/), "-5");
    await user.click(screen.getByRole("button", { name: "Створити послугу" }));
    expect(await screen.findByText(/Тривалість — ціле число/)).toBeInTheDocument();
    expect(screen.getAllByText(/Ціна — число від 0/).length).toBe(2);
    expect(createService).not.toHaveBeenCalled();
  });

  it("успіх: ціна з комою, лише заповнені ціни закладів, merge послуг майстра", async () => {
    const user = await open();
    await user.type(screen.getByLabelText(/^Назва/), "Стрижка");
    await user.type(screen.getByLabelText(/^Категорія/), "Волосся");
    await user.type(screen.getByLabelText(/^Тривалість/), "45");
    await user.type(screen.getByLabelText(/^Мережева ціна/), "499,50");
    await user.type(await screen.findByLabelText(/Ціна: Лівий берег/), "550");
    await user.click(await screen.findByRole("checkbox", { name: /Майстер a/ }));
    await user.click(screen.getByRole("button", { name: "Створити послугу" }));

    await waitFor(() => expect(setStaffServices).toHaveBeenCalledWith("a", ["s1", "new"]));
    expect(createService).toHaveBeenCalledWith({ name: "Стрижка", description: "", category: "Волосся", durationMinutes: 45, isActive: true });
    expect(setServicePrice.mock.calls).toEqual([
      ["new", 499.5, undefined],
      ["new", 550, "L2"],
    ]);
    expect(setStaffServices).toHaveBeenCalledTimes(1);
    expect(await screen.findByText("Послугу «Стрижка» створено.")).toBeInTheDocument();
    expect(screen.queryByRole("form", { name: "Нова послуга" })).toBeNull();
  });

  it("часткова помилка: повідомлення «Послугу створено, але…», повтор не дублює створення", async () => {
    setServicePrice.mockRejectedValueOnce(new BeautyApiError(500, "", "x"));
    const user = await open();
    await user.type(screen.getByLabelText(/^Назва/), "Стрижка");
    await user.type(screen.getByLabelText(/^Тривалість/), "45");
    await user.type(screen.getByLabelText(/^Мережева ціна/), "500");
    await user.click(screen.getByRole("button", { name: "Створити послугу" }));
    expect(await within(screen.getByRole("form", { name: "Нова послуга" })).findByRole("status")).toHaveTextContent(/Послугу створено, але не вдалося зберегти мережеву ціну/);

    await user.click(screen.getByRole("button", { name: "Повторити" }));
    expect(await screen.findByText("Послугу «Стрижка» створено.")).toBeInTheDocument();
    expect(createService).toHaveBeenCalledTimes(1);
    expect(setServicePrice).toHaveBeenCalledTimes(2);
  });

  it("помилка створення: українське повідомлення, форма лишається", async () => {
    createService.mockRejectedValueOnce(new BeautyApiError(403, "forbidden_role", "x"));
    const user = await open();
    await user.type(screen.getByLabelText(/^Назва/), "Стрижка");
    await user.type(screen.getByLabelText(/^Тривалість/), "45");
    await user.type(screen.getByLabelText(/^Мережева ціна/), "500");
    await user.click(screen.getByRole("button", { name: "Створити послугу" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("У вас немає прав на цю дію.");
    expect(setServicePrice).not.toHaveBeenCalled();
  });

  it("немає закладів: пояснення, мережева ціна все одно зберігається", async () => {
    getLocations.mockResolvedValue([]);
    const user = await open();
    expect(await screen.findByTestId("no-locations")).toBeInTheDocument();
    await user.type(screen.getByLabelText(/^Назва/), "Стрижка");
    await user.type(screen.getByLabelText(/^Тривалість/), "30");
    await user.type(screen.getByLabelText(/^Мережева ціна/), "100");
    await user.click(screen.getByRole("button", { name: "Створити послугу" }));
    await waitFor(() => expect(setServicePrice).toHaveBeenCalledWith("new", 100, undefined));
  });

  async function fillAll(user: ReturnType<typeof userEvent.setup>) {
    await user.type(screen.getByLabelText(/^Назва/), "Стрижка");
    await user.type(screen.getByLabelText(/^Тривалість/), "45");
    await user.type(screen.getByLabelText(/^Мережева ціна/), "500");
  }
  const formStatus = () => within(screen.getByRole("form", { name: "Нова послуга" })).getByRole("status");

  it("межі тривалості 5..720: 4 і 721 відхиляються з повідомленням", async () => {
    const user = await open();
    await user.type(screen.getByLabelText(/^Назва/), "Стрижка");
    await user.type(screen.getByLabelText(/^Мережева ціна/), "100");
    await user.type(screen.getByLabelText(/^Тривалість/), "4");
    await user.click(screen.getByRole("button", { name: "Створити послугу" }));
    expect(await screen.findByText("Тривалість — ціле число хвилин від 5 до 720.")).toBeInTheDocument();
    await user.clear(screen.getByLabelText(/^Тривалість/));
    await user.type(screen.getByLabelText(/^Тривалість/), "721");
    await user.click(screen.getByRole("button", { name: "Створити послугу" }));
    expect(await screen.findByText("Тривалість — ціле число хвилин від 5 до 720.")).toBeInTheDocument();
    expect(createService).not.toHaveBeenCalled();
  });

  it("невалідна форма: фокус на першому невалідному полі, підсумок role=alert", async () => {
    const user = await open();
    await user.click(screen.getByRole("button", { name: "Створити послугу" }));
    expect(await screen.findByRole("alert")).toHaveTextContent(/виправте поля з помилками \(3\)/);
    await waitFor(() => expect(screen.getByLabelText(/^Назва/)).toHaveFocus());
  });

  it("після часткового збою поля й майстри заблоковані, фокус на status", async () => {
    setServicePrice.mockRejectedValueOnce(new BeautyApiError(500, "", "x"));
    const user = await open();
    await fillAll(user);
    await user.click(await screen.findByRole("checkbox", { name: /Майстер a/ }));
    await user.click(screen.getByRole("button", { name: "Створити послугу" }));
    await waitFor(() => expect(formStatus()).toHaveTextContent(/Послугу створено, але/));
    expect(formStatus()).toHaveFocus();
    expect(screen.getByLabelText(/^Мережева ціна/)).toBeDisabled();
    expect(screen.getByLabelText(/Ціна: Центр/)).toBeDisabled();
    expect(screen.getByRole("checkbox", { name: /Майстер a/ })).toBeDisabled();
    await user.type(screen.getByLabelText(/^Мережева ціна/), "999");
    expect(screen.getByLabelText(/^Мережева ціна/)).toHaveValue("500");
  });

  it("подвійний submit під час виконання не дублює POST", async () => {
    let release!: () => void;
    createService.mockReset().mockImplementation(() => new Promise((res) => (release = () => res({ id: "new", name: "Стрижка", category: null, durationMinutes: 45 }))));
    const user = await open();
    await fillAll(user);
    await user.click(screen.getByRole("button", { name: "Створити послугу" }));
    const pending = await screen.findByRole("button", { name: "Зберігаємо…" });
    expect(pending).toHaveAttribute("aria-disabled", "true");
    expect(pending).not.toBeDisabled();
    await user.click(pending);
    await user.click(pending);
    release();
    expect(await screen.findByText("Послугу «Стрижка» створено.")).toBeInTheDocument();
    expect(createService).toHaveBeenCalledTimes(1);
  });

  it("«Закрити» після часткового збою просить підтвердження; повернення зберігає стан", async () => {
    setServicePrice.mockRejectedValueOnce(new BeautyApiError(500, "", "x"));
    const user = await open();
    await fillAll(user);
    await user.click(screen.getByRole("button", { name: "Створити послугу" }));
    await user.click(await screen.findByRole("button", { name: "Закрити" }));
    expect(screen.getByRole("form", { name: "Нова послуга" })).toBeInTheDocument();
    expect(screen.getByText(/створить дубль/)).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Повернутися до форми" }));
    expect(screen.queryByText(/створить дубль/)).toBeNull();
    await user.click(screen.getByRole("button", { name: "Закрити" }));
    await user.click(screen.getByRole("button", { name: "Так, закрити" }));
    expect(screen.queryByRole("form", { name: "Нова послуга" })).toBeNull();
  });

  it("403 forbidden_role на майстрі: зрозумілий текст, при повторі майстра пропущено", async () => {
    setStaffServices.mockRejectedValueOnce(new BeautyApiError(403, "forbidden_role", "x"));
    const user = await open();
    await fillAll(user);
    await user.click(await screen.findByRole("checkbox", { name: /Майстер a/ }));
    await user.click(screen.getByRole("button", { name: "Створити послугу" }));
    await waitFor(() => expect(formStatus()).toHaveTextContent(/змінює лише власник/));
    await user.click(screen.getByRole("button", { name: "Повторити" }));
    expect(await screen.findByText("Послугу «Стрижка» створено.")).toBeInTheDocument();
    expect(setStaffServices).toHaveBeenCalledTimes(1);
    expect(createService).toHaveBeenCalledTimes(1);
  });

  it("заклади з однаковою назвою розрізняються адресою або номером; підпис містить «необов’язково»", async () => {
    getLocations.mockResolvedValue([{ ...loc("L1", "Центр"), address: "вул. Перша, 1" }, loc("L2", "Центр")]);
    await open();
    expect(await screen.findByLabelText("Ціна: Центр (вул. Перша, 1), ₴ (необов’язково)")).toBeInTheDocument();
    expect(screen.getByLabelText("Ціна: Центр (№2), ₴ (необов’язково)")).toBeInTheDocument();
  });

  it("контейнер role=status для успіху змонтований завжди", async () => {
    renderScreen();
    await screen.findByRole("button", { name: "Нова послуга" });
    expect(screen.getAllByRole("status").some((el) => el.textContent === "")).toBe(true);
  });
});
