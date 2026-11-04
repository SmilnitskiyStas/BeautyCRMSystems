import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const replace = vi.hoisted(() => vi.fn());
vi.mock("next/navigation", () => ({ useRouter: () => ({ replace }) }));
vi.mock("../types", async (orig) => ({ ...(await orig<typeof import("../types")>()), USE_MOCK: false }));

import { OwnerInviteScreen } from "./owner-invite-screen";

const TOKEN = "A".repeat(20) + "b_-" + "9".repeat(20); // 43 символи base64url
const PASSWORD = "dovha-fraza-2026";
const SESSION = {
  accessToken: "access-jwt",
  expiresInSeconds: 900,
  user: { id: "u1", email: "owner@x.test", fullName: "Власник", role: "owner", specialistId: null },
};
const INFO = { tenantName: "Salon X", email: "o***@x.test", expiresAt: "2026-11-07T10:00:00Z" };

function json(body: unknown, status = 200, headers: Record<string, string> = {}) {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json", ...headers } });
}

type Handler = (path: string, body: Record<string, unknown>) => Response | Promise<Response>;
let calls: { path: string; body: Record<string, unknown> }[];

function mockFetch(handler: Handler) {
  calls = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (url: string, init: RequestInit) => {
      const body = JSON.parse(String(init.body)) as Record<string, unknown>;
      calls.push({ path: url, body });
      return handler(url, body);
    }),
  );
}

const happy: Handler = (path) => (path.endsWith("/inspect") ? json(INFO) : json(SESSION));

function openWithHash(hash: string) {
  window.history.replaceState(null, "", `/invite${hash}`);
}

async function fillAndSubmit(password = PASSWORD, confirm = password) {
  const pw = await screen.findByLabelText("Пароль");
  fireEvent.change(pw, { target: { value: password } });
  fireEvent.change(screen.getByLabelText("Повторіть пароль"), { target: { value: confirm } });
  fireEvent.click(screen.getByRole("button", { name: /Встановити пароль/ }));
}

beforeEach(() => {
  replace.mockReset();
  window.localStorage.clear();
  window.sessionStorage.clear();
});
afterEach(() => {
  vi.unstubAllGlobals();
  window.history.replaceState(null, "", "/");
});

describe("OwnerInviteScreen", () => {
  it("прибирає токен з адреси, показує бізнес і маскований email, токена в DOM немає", async () => {
    openWithHash(`#t=${TOKEN}`);
    const replaceState = vi.spyOn(window.history, "replaceState");
    mockFetch(happy);
    const { container } = render(<OwnerInviteScreen />);

    expect(await screen.findByText("Salon X")).toBeInTheDocument();
    expect(screen.getByText("o***@x.test")).toBeInTheDocument();
    expect(replaceState).toHaveBeenCalled();
    expect(window.location.hash).toBe("");
    expect(window.location.href).not.toContain(TOKEN);
    expect(container.innerHTML).not.toContain(TOKEN);
    expect(document.body.innerHTML).not.toContain(TOKEN);
    expect(calls).toHaveLength(1);
    expect(calls[0]).toEqual({ path: "/api/session/owner-invite/inspect", body: { token: TOKEN } });
    expect(String(calls[0].path)).not.toContain("?");
    replaceState.mockRestore();
  });

  it("повний потік: пароль -> accept -> редірект у адмінку; токен не в сховищах і не в DOM", async () => {
    openWithHash(`#t=${TOKEN}`);
    mockFetch(happy);
    render(<OwnerInviteScreen />);
    await fillAndSubmit();

    await waitFor(() => expect(replace).toHaveBeenCalledWith("/beauty"));
    expect(calls[1]).toEqual({ path: "/api/session/owner-invite/accept", body: { token: TOKEN, password: PASSWORD } });
    expect(JSON.stringify({ ...window.localStorage })).not.toContain(TOKEN);
    expect(JSON.stringify({ ...window.sessionStorage })).not.toContain(TOKEN);
    expect(document.body.innerHTML).not.toContain(TOKEN);
    expect(document.body.innerHTML).not.toContain("access-jwt");
  });

  it("поля пароля: autocomplete=new-password, показ/приховування", async () => {
    openWithHash(`#t=${TOKEN}`);
    mockFetch(happy);
    render(<OwnerInviteScreen />);
    const pw = await screen.findByLabelText("Пароль");
    expect(pw).toHaveAttribute("autocomplete", "new-password");
    expect(screen.getByLabelText("Повторіть пароль")).toHaveAttribute("autocomplete", "new-password");
    expect(pw).toHaveAttribute("type", "password");
    const toggle = screen.getAllByRole("button", { name: /Показати/ })[0];
    fireEvent.click(toggle);
    expect(pw).toHaveAttribute("type", "text");
    expect(toggle).toHaveAttribute("aria-pressed", "true");
  });

  it("клієнтська валідація: короткий пароль і неспівпадіння не викликають accept", async () => {
    openWithHash(`#t=${TOKEN}`);
    mockFetch(happy);
    render(<OwnerInviteScreen />);
    await fillAndSubmit("short", "short");
    expect(await screen.findByText("Щонайменше 12 символів.")).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText("Пароль"), { target: { value: PASSWORD } });
    fireEvent.click(screen.getByRole("button", { name: /Встановити пароль/ }));
    expect(await screen.findByText("Паролі не збігаються.")).toBeInTheDocument();
    expect(screen.getByLabelText("Повторіть пароль")).toHaveAttribute("aria-invalid", "true");
    expect(calls.filter((c) => c.path.endsWith("/accept"))).toHaveLength(0);
  });

  it("422 weak_password: помилка біля поля, токен лишається придатним для повтору", async () => {
    openWithHash(`#t=${TOKEN}`);
    let accepts = 0;
    mockFetch((path) => {
      if (path.endsWith("/inspect")) return json(INFO);
      accepts++;
      return accepts === 1 ? json({ code: "weak_password" }, 422) : json(SESSION);
    });
    render(<OwnerInviteScreen />);
    await fillAndSubmit();
    expect(await screen.findByText(/Пароль не підходить/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: /Встановити пароль/ }));
    await waitFor(() => expect(replace).toHaveBeenCalledWith("/beauty"));
    expect(calls[2].body.token).toBe(TOKEN);
  });

  it("410 на inspect: один екран «недійсне або застаріле»", async () => {
    openWithHash(`#t=${TOKEN}`);
    mockFetch(() => json({ code: "invite_invalid" }, 410));
    render(<OwnerInviteScreen />);
    expect(await screen.findByRole("heading", { name: "Посилання недійсне або застаріле" })).toBeInTheDocument();
    expect(screen.getByText(/Попросіть нове запрошення/)).toBeInTheDocument();
  });

  it("немає токена або некоректний формат: той самий екран, без запитів", async () => {
    for (const hash of ["", "#t=short", "#x=" + TOKEN, `#t=${TOKEN}!`]) {
      openWithHash(hash);
      mockFetch(happy);
      const { unmount } = render(<OwnerInviteScreen />);
      expect(await screen.findByRole("heading", { name: "Посилання недійсне або застаріле" })).toBeInTheDocument();
      expect(calls).toHaveLength(0);
      expect(window.location.hash).toBe("");
      unmount();
    }
  });

  it("410 на accept (використано/прострочено) веде на той самий екран", async () => {
    openWithHash(`#t=${TOKEN}`);
    mockFetch((path) => (path.endsWith("/inspect") ? json(INFO) : json({ code: "invite_invalid" }, 410)));
    render(<OwnerInviteScreen />);
    await fillAndSubmit();
    expect(await screen.findByRole("heading", { name: "Посилання недійсне або застаріле" })).toBeInTheDocument();
    expect(replace).not.toHaveBeenCalled();
  });

  it("403 tenant_suspended: окремий нейтральний екран (inspect і accept)", async () => {
    openWithHash(`#t=${TOKEN}`);
    mockFetch(() => json({ code: "tenant_suspended" }, 403));
    const { unmount } = render(<OwnerInviteScreen />);
    expect(await screen.findByRole("heading", { name: "Зараз це недоступно" })).toBeInTheDocument();
    unmount();

    openWithHash(`#t=${TOKEN}`);
    mockFetch((path) => (path.endsWith("/inspect") ? json(INFO) : json({ code: "tenant_suspended" }, 403)));
    render(<OwnerInviteScreen />);
    await fillAndSubmit();
    expect(await screen.findByRole("heading", { name: "Зараз це недоступно" })).toBeInTheDocument();
  });

  it("429 на inspect: час очікування з Retry-After і повтор без втрати токена", async () => {
    openWithHash(`#t=${TOKEN}`);
    let n = 0;
    mockFetch(() => (++n === 1 ? json({ code: "rate_limited" }, 429, { "Retry-After": "120" }) : json(INFO)));
    render(<OwnerInviteScreen />);
    expect(await screen.findByText(/Зачекайте 2 хв/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Спробувати ще раз" }));
    expect(await screen.findByText("Salon X")).toBeInTheDocument();
    expect(calls[1].body.token).toBe(TOKEN);
  });

  it("429 на accept: повідомлення у формі (aria-live), форма лишається", async () => {
    openWithHash(`#t=${TOKEN}`);
    mockFetch((path) => (path.endsWith("/inspect") ? json(INFO) : json({ code: "rate_limited" }, 429, { "Retry-After": "30" })));
    render(<OwnerInviteScreen />);
    await fillAndSubmit();
    expect(await screen.findByText(/Зачекайте менше хвилини/)).toBeInTheDocument();
    expect(screen.getByLabelText("Пароль")).toBeInTheDocument();
  });

  it("мережева помилка на inspect: екран з повтором", async () => {
    openWithHash(`#t=${TOKEN}`);
    vi.stubGlobal("fetch", vi.fn(async () => Promise.reject(new TypeError("net"))));
    render(<OwnerInviteScreen />);
    expect(await screen.findByRole("heading", { name: "Не вдалося перевірити посилання" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Повторити" })).toBeInTheDocument();
  });
});
