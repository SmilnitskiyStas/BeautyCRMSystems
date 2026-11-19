import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { BeautyApiError } from "@/features/beauty-auth/errors";

const h = vi.hoisted(() => ({ send: vi.fn() }));
vi.mock("../api", () => ({ beautyApi: { sendFeedback: (...a: unknown[]) => h.send(...a) } }));

import { FeedbackDialog } from "./feedback-dialog";

const onClose = vi.fn();
function renderDialog() {
  const qc = new QueryClient({ defaultOptions: { mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <FeedbackDialog onClose={onClose} />
    </QueryClientProvider>,
  );
}
const area = () => screen.getByLabelText(/Опишіть деталі/) as HTMLTextAreaElement;
const submit = () => fireEvent.click(screen.getByRole("button", { name: "Надіслати" }));
const type = (t: string) => fireEvent.change(area(), { target: { value: t } });
const VALID = "Не відкривається календар майстра";

beforeEach(() => {
  h.send.mockReset();
  onClose.mockReset();
  window.history.pushState({}, "", "/beauty/calendar?week=3#top");
});

describe("форма звернення до розробників", () => {
  it("діалог із назвою, трьома типами, за замовчуванням «Покращити», критичності немає", () => {
    renderDialog();
    expect(screen.getByRole("dialog", { name: "Звернення до розробників" })).toBeInTheDocument();
    expect(screen.getByRole("radio", { name: /Співпраця/ })).not.toBeChecked();
    expect(screen.getByRole("radio", { name: /Покращити/ })).toBeChecked();
    expect(screen.getByRole("radio", { name: /Не працює/ })).not.toBeChecked();
    expect(screen.queryByLabelText("Наскільки це критично")).not.toBeInTheDocument();
  });

  it("«Не працює» показує критичність зі значенням «Звичайна» за замовчуванням", () => {
    renderDialog();
    fireEvent.click(screen.getByRole("radio", { name: /Не працює/ }));
    const sel = screen.getByLabelText("Наскільки це критично") as HTMLSelectElement;
    expect(sel.value).toBe("normal");
    expect(screen.getAllByRole("option")).toHaveLength(4);
  });

  it("лічильник символів і межа 2000", () => {
    renderDialog();
    expect(screen.getByTestId("feedback-counter")).toHaveTextContent("0 / 2000");
    type("привітсвіт");
    expect(screen.getByTestId("feedback-counter")).toHaveTextContent("10 / 2000");
    type("x".repeat(2001));
    expect(screen.getByTestId("feedback-counter")).toHaveTextContent("2001 / 2000");
  });

  it("примітка про дані - явний плейсхолдер Q8", () => {
    renderDialog();
    const note = screen.getByTestId("feedback-data-notice");
    expect(note).toHaveAttribute("data-placeholder", "q8");
    expect(note).toHaveTextContent("[ПЛЕЙСХОЛДЕР Q8]");
  });

  it("коротке повідомлення: помилка, фокус на полі, aria-invalid, запиту немає", () => {
    renderDialog();
    type("коротко");
    submit();
    const msg = screen.getByText(/щонайменше 10/);
    expect(area()).toHaveAttribute("aria-invalid", "true");
    expect(area()).toHaveFocus();
    expect(area().getAttribute("aria-describedby")).toContain(msg.id);
    expect(h.send).not.toHaveBeenCalled();
  });

  it("202: запит з pageUrl без query/fragment, форма очищується, статус оголошується", async () => {
    h.send.mockResolvedValue({ id: "f1" });
    renderDialog();
    fireEvent.click(screen.getByRole("radio", { name: /Не працює/ }));
    fireEvent.change(screen.getByLabelText("Наскільки це критично"), { target: { value: "critical" } });
    type(`  ${VALID}  `);
    submit();
    await waitFor(() => expect(screen.getByRole("status")).toHaveTextContent("Звернення надіслано"));
    expect(h.send).toHaveBeenCalledWith({ type: "bug", severity: "critical", message: VALID, pageUrl: "/beauty/calendar" });
    expect(area().value).toBe("");
    expect(screen.getByRole("status")).toHaveAttribute("aria-live", "polite");
  });

  it("не bug: критичність normal у запиті", async () => {
    h.send.mockResolvedValue({ id: "f1" });
    renderDialog();
    fireEvent.click(screen.getByRole("radio", { name: /Співпраця/ }));
    type("Хочемо запропонувати співпрацю");
    submit();
    await waitFor(() => expect(h.send).toHaveBeenCalled());
    expect(h.send.mock.calls[0][0]).toMatchObject({ type: "cooperation", severity: "normal" });
  });

  it("429 з Retry-After: текст із часом, введений текст збережено", async () => {
    h.send.mockRejectedValue(new BeautyApiError(429, "rate_limited", "x", 1800));
    renderDialog();
    type(VALID);
    submit();
    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent("30 хв");
    expect(area().value).toBe(VALID);
  });

  it.each([
    [403, "tenant_suspended", "призупинено"],
    [422, "invalid_type", "тип звернення"],
    [422, "invalid_severity", "критичність"],
    [422, "invalid_message", "від 10 до 2000"],
    [422, "invalid_page_url", "сторінку"],
    [401, "invalid_token", "Увійдіть знову"],
  ])("%i %s -> зрозумілий текст", async (status, code, part) => {
    h.send.mockRejectedValue(new BeautyApiError(status, code, "x"));
    renderDialog();
    type(VALID);
    submit();
    expect(await screen.findByRole("alert")).toHaveTextContent(part);
    expect(area().value).toBe(VALID);
  });

  it("мережева помилка", async () => {
    h.send.mockRejectedValue(new TypeError("Failed to fetch"));
    renderDialog();
    type(VALID);
    submit();
    expect(await screen.findByRole("alert")).toHaveTextContent("Сервер недоступний");
  });

  it("повторна відправка після помилки успішна й прибирає банер", async () => {
    h.send.mockRejectedValueOnce(new TypeError("x")).mockResolvedValueOnce({ id: "f" });
    renderDialog();
    type(VALID);
    submit();
    await screen.findByRole("alert");
    submit();
    await waitFor(() => expect(screen.getByRole("status")).toHaveTextContent("Звернення надіслано"));
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });

  it("під час відправки подвійний клік шле один запит", async () => {
    let resolve!: (v: unknown) => void;
    h.send.mockReturnValue(new Promise((r) => (resolve = r)));
    renderDialog();
    type(VALID);
    const btn = screen.getByRole("button", { name: "Надіслати" });
    fireEvent.click(btn);
    fireEvent.click(btn);
    await waitFor(() => expect(h.send).toHaveBeenCalledTimes(1));
    expect(h.send).toHaveBeenCalledTimes(1);
    resolve({ id: "f" });
    await waitFor(() => expect(screen.getByRole("status")).toHaveTextContent("Звернення надіслано"));
  });

  it("Esc і «Закрити» закривають", () => {
    renderDialog();
    fireEvent.keyDown(screen.getByRole("dialog"), { key: "Escape" });
    expect(onClose).toHaveBeenCalledTimes(1);
    fireEvent.click(screen.getByRole("button", { name: "Закрити" }));
    expect(onClose).toHaveBeenCalledTimes(2);
  });

  it("фокус на заголовку діалогу при відкритті", () => {
    renderDialog();
    expect(screen.getByRole("heading", { name: "Звернення до розробників" })).toHaveFocus();
  });
});
