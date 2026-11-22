import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const replace = vi.hoisted(() => vi.fn());
vi.mock("next/navigation", () => ({
  useRouter: () => ({ replace }),
  useSearchParams: () => new URLSearchParams(""),
}));
vi.mock("next/link", () => ({ default: ({ href, children }: { href: string; children: React.ReactNode }) => <a href={href}>{children}</a> }));
vi.mock("../types", async (orig) => ({ ...(await orig<typeof import("../types")>()), USE_MOCK: false }));

import { clearSession } from "../session";
import { LAST_TENANT_KEY } from "./business-picker";
import { LoginForm } from "./login-form";
import { ForgotPasswordForm } from "./forgot-password-form";
import { ResetPasswordForm } from "./reset-password-form";

const USER = { id: "u1", email: "a@x.test", fullName: "Адмін", role: "admin", specialistId: null };
const SESSION = { accessToken: "jwt", expiresInSeconds: 900, user: USER };
const ME = { ...USER, accessVersion: 1, accessStamp: "1.00", catalogVersion: 1, modules: [], permissions: { "calendar.view": "all" } };
const BUSINESSES = [
  { tenantId: "t-a", name: "Alpha", role: "admin" },
  { tenantId: "t-b", name: "Beta", role: "specialist" },
];
const SELECTION = { selectionRequired: true, ticketExpiresInSeconds: 180, businesses: BUSINESSES };

const json = (body: unknown, status = 200, headers: Record<string, string> = {}) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json", ...headers } });

let calls: { path: string; body: Record<string, unknown> }[];
function mockFetch(handler: (path: string, body: Record<string, unknown>) => Response) {
  calls = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (url: string, init: RequestInit) => {
      const body = (init?.body ? JSON.parse(String(init.body)) : {}) as Record<string, unknown>;
      calls.push({ path: url, body });
      if (url.endsWith("/api/auth/me")) return json(ME);
      return handler(url, body);
    }),
  );
}

function fillLogin(email = "a@x.test", password = "secret-pass") {
  fireEvent.change(screen.getByLabelText("Пошта"), { target: { value: email } });
  fireEvent.change(screen.getByLabelText("Пароль"), { target: { value: password } });
  fireEvent.click(screen.getByRole("button", { name: "Увійти" }));
}

beforeEach(() => {
  replace.mockReset();
  window.localStorage.clear();
});
afterEach(() => {
  clearSession(false);
  vi.unstubAllGlobals();
});

describe("LoginForm (§31.2)", () => {
  it("немає поля «Код бізнесу»; у запиті лише пошта й пароль", async () => {
    mockFetch(() => json(SESSION));
    render(<LoginForm />);
    expect(screen.queryByLabelText(/Код бізнесу/)).not.toBeInTheDocument();
    fillLogin();
    await waitFor(() => expect(replace).toHaveBeenCalledWith("/beauty"));
    expect(calls[0]).toEqual({ path: "/api/session/login", body: { email: "a@x.test", password: "secret-pass" } });
  });

  it("валідує пошту й пароль до запиту", async () => {
    mockFetch(() => json(SESSION));
    render(<LoginForm />);
    fillLogin("не-пошта", "");
    expect(await screen.findByText("Вкажіть коректну пошту")).toBeInTheDocument();
    expect(screen.getByText("Введіть пароль")).toBeInTheDocument();
    expect(calls).toHaveLength(0);
  });

  it.each([
    [401, "invalid_credentials", "Невірна пошта або пароль"],
    [423, "account_locked", "Забагато невдалих спроб. Спробуйте за 15 хвилин або скиньте пароль"],
    [429, "rate_limited", "Забагато спроб. Спробуйте пізніше"],
  ])("%s %s -> текст із §31.11", async (status, code, text) => {
    mockFetch(() => json({ code, message: "english" }, status));
    render(<LoginForm />);
    fillLogin();
    expect(await screen.findByRole("alert")).toHaveTextContent(text);
    expect(screen.queryByText("english")).not.toBeInTheDocument();
    expect(replace).not.toHaveBeenCalled();
  });

  it("є посилання «Забули пароль?»", () => {
    render(<LoginForm />);
    expect(screen.getByRole("link", { name: "Забули пароль?" })).toHaveAttribute("href", "/forgot-password");
  });
});

describe("вибір бізнесу (§31.3)", () => {
  it("selectionRequired -> екран вибору; вибір викликає login/select лише з tenantId, квитка в JS немає", async () => {
    mockFetch((path) => (path.endsWith("/login/select") ? json(SESSION) : json(SELECTION)));
    const { container } = render(<LoginForm />);
    fillLogin();
    const beta = await screen.findByRole("button", { name: /Beta/ });
    expect(screen.getByRole("button", { name: /Alpha/ })).toBeInTheDocument();
    fireEvent.click(beta);
    await waitFor(() => expect(replace).toHaveBeenCalledWith("/beauty"));
    expect(calls.map((c) => c.path)).toContain("/api/session/login/select");
    const select = calls.find((c) => c.path === "/api/session/login/select")!;
    expect(select.body).toEqual({ tenantId: "t-b" });
    expect(JSON.stringify(calls)).not.toMatch(/loginTicket/);
    expect(container.innerHTML).not.toMatch(/loginTicket/);
    expect(window.localStorage.getItem(LAST_TENANT_KEY)).toBe("t-b");
  });

  it("останній обраний бізнес стоїть першим, підсвічений і отримує фокус", async () => {
    window.localStorage.setItem(LAST_TENANT_KEY, "t-b");
    mockFetch(() => json(SELECTION));
    render(<LoginForm />);
    fillLogin();
    const items = await screen.findAllByRole("listitem");
    expect(items[0]).toHaveTextContent("Beta");
    const first = screen.getAllByRole("button").find((b) => b.textContent?.includes("Beta"))!;
    expect(first).toHaveAttribute("aria-current", "true");
    expect(first).toHaveFocus();
  });

  it("401 login_ticket_invalid -> «Час вибору минув» і повернення до форми", async () => {
    mockFetch((path) => (path.endsWith("/login/select") ? json({ code: "login_ticket_invalid", message: "x" }, 401) : json(SELECTION)));
    render(<LoginForm />);
    fillLogin();
    fireEvent.click(await screen.findByRole("button", { name: /Alpha/ }));
    expect(await screen.findByRole("alert")).toHaveTextContent("Час вибору минув, увійдіть ще раз");
    fireEvent.click(screen.getByRole("button", { name: "Увійти ще раз" }));
    expect(await screen.findByLabelText("Пошта")).toBeInTheDocument();
  });

  it("квиток спливає за ticketExpiresInSeconds", async () => {
    mockFetch(() => json({ ...SELECTION, ticketExpiresInSeconds: 0.05 }));
    render(<LoginForm />);
    fillLogin();
    expect(await screen.findByRole("alert")).toHaveTextContent("Час вибору минув, увійдіть ще раз");
  });

  it("помилка вибору (500) лишає екран і показує повідомлення", async () => {
    mockFetch((path) => (path.endsWith("/login/select") ? json({ code: "", message: "" }, 500) : json(SELECTION)));
    render(<LoginForm />);
    fillLogin();
    fireEvent.click(await screen.findByRole("button", { name: /Alpha/ }));
    expect(await screen.findByRole("alert")).toHaveTextContent("Помилка сервера");
    expect(screen.getByRole("button", { name: /Beta/ })).toBeEnabled();
  });
});

describe("/forgot-password (§31.7.1)", () => {
  it("202 -> нейтральне повідомлення", async () => {
    mockFetch(() => new Response(null, { status: 202 }));
    render(<ForgotPasswordForm />);
    fireEvent.change(screen.getByLabelText("Пошта"), { target: { value: "who@x.test" } });
    fireEvent.click(screen.getByRole("button", { name: "Надіслати посилання" }));
    expect(await screen.findByText("Якщо ця пошта зареєстрована, ми надіслали лист із посиланням")).toBeInTheDocument();
    expect(calls[0]).toEqual({ path: "/api/session/password/forgot", body: { email: "who@x.test" } });
  });

  it("429 -> rate_limited", async () => {
    mockFetch(() => json({ code: "rate_limited", message: "x" }, 429));
    render(<ForgotPasswordForm />);
    fireEvent.change(screen.getByLabelText("Пошта"), { target: { value: "who@x.test" } });
    fireEvent.click(screen.getByRole("button", { name: "Надіслати посилання" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("Забагато спроб. Спробуйте пізніше");
  });
});

describe("/reset-password (§31.7.2)", () => {
  const TOKEN = "A".repeat(20) + "b_-" + "9".repeat(20);
  const PW = "dovha-fraza-2026";
  afterEach(() => window.history.replaceState(null, "", "/"));

  async function submit(pw = PW, confirm = pw) {
    fireEvent.change(await screen.findByLabelText("Пароль"), { target: { value: pw } });
    fireEvent.change(screen.getByLabelText("Повторіть пароль"), { target: { value: confirm } });
    fireEvent.click(screen.getByRole("button", { name: "Змінити пароль" }));
  }

  it("прибирає токен з адреси, 204 -> пароль змінено без автовходу", async () => {
    window.history.replaceState(null, "", `/reset-password#t=${TOKEN}`);
    mockFetch(() => new Response(null, { status: 204 }));
    const { container } = render(<ResetPasswordForm />);
    await submit();
    expect(await screen.findByText("Пароль змінено")).toBeInTheDocument();
    expect(window.location.href).not.toContain(TOKEN);
    expect(container.innerHTML).not.toContain(TOKEN);
    expect(calls[0]).toEqual({ path: "/api/session/password/reset", body: { token: TOKEN, password: PW } });
    expect(replace).not.toHaveBeenCalled();
  });

  it("без токена й на 410 reset_invalid -> «Посилання недійсне або застаріло»", async () => {
    mockFetch(() => json({ code: "reset_invalid", message: "x" }, 410));
    render(<ResetPasswordForm />);
    expect(await screen.findByRole("alert")).toHaveTextContent("Посилання недійсне або застаріло. Запросіть нове");
    expect(calls).toHaveLength(0);
  });

  it("410 від сервера веде на екран недійсного посилання", async () => {
    window.history.replaceState(null, "", `/reset-password#t=${TOKEN}`);
    mockFetch(() => json({ code: "reset_invalid", message: "x" }, 410));
    render(<ResetPasswordForm />);
    await submit();
    expect(await screen.findByRole("link", { name: "Запросити нове посилання" })).toHaveAttribute("href", "/forgot-password");
  });

  it("паролі не збігаються -> запиту немає", async () => {
    window.history.replaceState(null, "", `/reset-password#t=${TOKEN}`);
    mockFetch(() => new Response(null, { status: 204 }));
    render(<ResetPasswordForm />);
    await submit(PW, "інший-пароль-2026");
    expect(await screen.findByText("Паролі не збігаються")).toBeInTheDocument();
    expect(calls).toHaveLength(0);
  });
});
