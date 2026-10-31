import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { BeautyApiError } from "@/features/beauty-auth/errors";
import { DEMO_LOCATIONS, STAFF, appointmentsSeed } from "../../beauty-shared/demo-seed";
import { INITIAL_CHANNELS } from "../api/mock-data";
import { createMockInbox } from "../api/mock-inbox";

const h = vi.hoisted(() => ({
  api: {} as Record<string, (...a: unknown[]) => unknown>,
  auth: { user: { id: "u", email: "e", fullName: "n", role: "owner" } },
}));

vi.mock("@/features/beauty-auth/components/auth-provider", () => ({ useAuth: () => h.auth }));
vi.mock("../api", () => ({
  beautyApi: new Proxy({}, { get: (_t, k: string) => (...a: unknown[]) => h.api[k](...a) }),
}));

import { CallsScreen } from "./calls-screen";

type Spies = Record<string, ReturnType<typeof vi.fn>>;
let spies: Spies;

function setup(role = "owner") {
  h.auth.user.role = role;
  const appts = appointmentsSeed();
  const real = createMockInbox({
    role: () => role,
    wait: false,
    locations: () => DEMO_LOCATIONS,
    appointments: () => appts,
    channels: () => structuredClone(INITIAL_CHANNELS),
    staffName: (id) => STAFF.find((m) => m.id === id)?.name ?? "",
  });
  spies = Object.fromEntries(["getCallTasks", "updateCallTask"].map((k) => [k, vi.fn(real[k as "getCallTasks"] as (...a: unknown[]) => unknown)]));
  spies.getLocations = vi.fn(async () => structuredClone(DEMO_LOCATIONS));
  h.api = spies as unknown as typeof h.api;
}

function renderCalls() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <CallsScreen />
    </QueryClientProvider>,
  );
}

beforeEach(() => setup());

describe("/beauty/calls", () => {
  it("за замовчуванням сьогодні й «очікують»: час, клієнт, телефон tel:, візит, заклад, причина", async () => {
    renderCalls();
    const rows = await screen.findAllByTestId("call-task");
    expect(spies.getCallTasks).toHaveBeenCalledWith(expect.objectContaining({ day: "today", status: "open" }));
    const failed = rows.find((r) => r.textContent?.includes("Не доставлено в Telegram"))!;
    expect(failed).toBeTruthy();
    const phone = within(failed).getByRole("link", { name: /\+380/ });
    expect(phone.getAttribute("href")).toMatch(/^tel:\+380\d+$/);
    expect(within(failed).getByRole("link", { name: "Тетяна Гончар" })).toHaveAttribute("href", "/beauty/clients/tg");
    expect(within(failed).getByText("Очікує дзвінка")).toBeInTheDocument();
    expect(screen.getByRole("status")).toHaveTextContent(/очікують \d+ · зроблено 0 · пропущено 0/);
  });

  it("«Зателефонував»: задача зникає зі списку «очікують», у «Зроблено» видно хто і коли й нотатку; «Повернути» відкриває знову", async () => {
    renderCalls();
    const first = (await screen.findAllByTestId("call-task"))[0];
    const name = within(first).getAllByRole("link")[0].textContent!;
    await userEvent.type(within(first).getByLabelText("Нотатка (необов’язково)"), "Лишила голосове");
    await userEvent.click(within(first).getByRole("button", { name: "Зателефонував" }));
    await waitFor(() => expect(spies.updateCallTask).toHaveBeenCalledWith(expect.any(String), { status: "done", note: "Лишила голосове" }));
    await waitFor(() => expect(screen.queryByText(name)).not.toBeInTheDocument());

    await userEvent.selectOptions(screen.getByLabelText("Статус"), "done");
    const done = (await screen.findAllByTestId("call-task"))[0];
    expect(done).toHaveAttribute("data-status", "done");
    expect(done).toHaveTextContent("Зробив(ла): Світлана Коваленко");
    expect(done).toHaveTextContent("Нотатка: Лишила голосове");
    await userEvent.click(within(done).getByRole("button", { name: "Повернути" }));
    await waitFor(() => expect(spies.updateCallTask).toHaveBeenLastCalledWith(expect.any(String), { status: "open" }));
    await waitFor(() => expect(screen.queryByTestId("call-task")).not.toBeInTheDocument());
    expect(screen.getByText(/За цими фільтрами задач немає/)).toBeInTheDocument();
  });

  it("«Пропустити» ставить статус skipped", async () => {
    renderCalls();
    const first = (await screen.findAllByTestId("call-task"))[0];
    await userEvent.click(within(first).getByRole("button", { name: "Пропустити" }));
    await waitFor(() => expect(spies.updateCallTask).toHaveBeenCalledWith(expect.any(String), { status: "skipped" }));
    await userEvent.selectOptions(screen.getByLabelText("Статус"), "skipped");
    expect((await screen.findAllByTestId("call-task"))[0]).toHaveTextContent("Пропустив(ла)");
  });

  it("409 task_already_closed: показано, хто закрив, рядок оновлюється", async () => {
    renderCalls();
    const first = (await screen.findAllByTestId("call-task"))[0];
    spies.updateCallTask.mockRejectedValueOnce(
      new BeautyApiError(409, "task_already_closed", "closed", undefined, undefined, undefined, { id: "x", status: "done", completedBy: { id: "o", name: "Ірина Адміністратор" } }),
    );
    await userEvent.click(within(first).getByRole("button", { name: "Зателефонував" }));
    expect(await within(first).findByRole("alert")).toHaveTextContent("Цю задачу вже закрито: Ірина Адміністратор (зроблено). Список оновлено.");
    await waitFor(() => expect(spies.getCallTasks.mock.calls.length).toBeGreaterThan(1));
  });

  it("інша помилка (422 invalid_note) - український текст без серверного message", async () => {
    renderCalls();
    const first = (await screen.findAllByTestId("call-task"))[0];
    spies.updateCallTask.mockRejectedValueOnce(new BeautyApiError(422, "invalid_note", "note too long: секрет"));
    await userEvent.click(within(first).getByRole("button", { name: "Пропустити" }));
    const alert = await within(first).findByRole("alert");
    expect(alert).toHaveTextContent("Нотатка задовга: максимум 300 символів.");
    expect(alert).not.toHaveTextContent("секрет");
  });

  it("фільтри: «Завтра», дата, заклад передаються в API", async () => {
    renderCalls();
    await screen.findAllByTestId("call-task");
    await userEvent.click(screen.getByRole("button", { name: "Завтра" }));
    await waitFor(() => expect(spies.getCallTasks).toHaveBeenLastCalledWith(expect.objectContaining({ day: "tomorrow" })));
    await userEvent.selectOptions(screen.getByLabelText("Заклад"), "c");
    await waitFor(() => expect(spies.getCallTasks).toHaveBeenLastCalledWith(expect.objectContaining({ day: "tomorrow", locationId: "c" })));
    await userEvent.click(screen.getByRole("button", { name: "Сьогодні" }));
    await waitFor(() => expect(spies.getCallTasks).toHaveBeenLastCalledWith(expect.objectContaining({ day: "today", locationId: "c" })));
  });

  it("порожній день: пояснення й посилання на клієнтів", async () => {
    renderCalls();
    await screen.findAllByTestId("call-task");
    // Демо-записи відносні до «сьогодні» (залежать від дня тижня): шукаємо день і заклад без задач, а не фіксуємо.
    const call = spies.getCallTasks as unknown as (q: unknown) => Promise<{ items: unknown[] }>;
    let found: { day: "today" | "tomorrow"; id: string } | null = null;
    for (const day of ["today", "tomorrow"] as const) {
      for (const id of ["c", "p", "k"]) {
        if (!found && (await call({ day, status: "open", locationId: id })).items.length === 0) found = { day, id };
      }
    }
    expect(found).not.toBeNull();
    if (found!.day === "tomorrow") await userEvent.click(screen.getByRole("button", { name: "Завтра" }));
    await userEvent.selectOptions(screen.getByLabelText("Заклад"), found!.id);
    expect(await screen.findByText(/На цей день немає дзвінків, що очікують/)).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "До клієнтів" })).toHaveAttribute("href", "/beauty/clients");
  });

  it("specialist: пояснення, без викликів API і без ПД (телефонів, імен)", async () => {
    setup("specialist");
    const { container } = renderCalls();
    expect(screen.getByRole("note")).toHaveTextContent("лише власнику й адміністратору");
    await act(async () => {
      await new Promise((r) => setTimeout(r, 30));
    });
    expect(spies.getCallTasks).not.toHaveBeenCalled();
    expect(container.textContent).not.toMatch(/\+380|Гончар|Ляшко|Кравченко/);
  });
});
