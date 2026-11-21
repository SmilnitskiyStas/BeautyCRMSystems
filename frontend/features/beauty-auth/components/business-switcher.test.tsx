import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const replace = vi.hoisted(() => vi.fn());
vi.mock("next/navigation", () => ({ useRouter: () => ({ replace }) }));
vi.mock("../types", async (orig) => ({ ...(await orig<typeof import("../types")>()), USE_MOCK: false }));
vi.mock("./auth-provider", () => ({ useAuth: () => ({ user: { id: "m1" } }) }));

import { clearSession, login } from "../session";
import { BusinessSwitcher } from "./business-switcher";

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
const U1 = { id: "m1", email: "a@x", fullName: "N", role: "admin", specialistId: null };
const U2 = { id: "m2", email: "a@x", fullName: "N", role: "specialist", specialistId: "s1" };
const meOf = (u: { id: string; email: string; fullName: string; role: string; specialistId: string | null }) => ({ ...u, accessVersion: 1, accessStamp: `1.${u.id}`, catalogVersion: 1, modules: [], permissions: { "calendar.view": "all" } });

const SEEDED_KEY = ["calendar", "t-a"];
let qc: QueryClient;
let switchResponse: () => Response;
let meSnapshots: { auth: string | null; seededInCache: boolean }[];

beforeEach(async () => {
  meSnapshots = [];
  switchResponse = () => json({ accessToken: "t2", expiresInSeconds: 900, user: U2 });
  let current: typeof U1 | typeof U2 = U1;
  vi.stubGlobal(
    "fetch",
    vi.fn(async (url: string, init: RequestInit) => {
      if (url === "/api/session/login") return json({ accessToken: "t1", expiresInSeconds: 900, user: U1 });
      if (url === "/api/session/switch") {
        const res = switchResponse();
        if (res.ok) current = U2;
        return res;
      }
      if (url.endsWith("/api/auth/me")) {
        const headers = new Headers(init?.headers);
        meSnapshots.push({ auth: headers.get("Authorization"), seededInCache: qc.getQueryData(SEEDED_KEY) !== undefined });
        return json(meOf(current));
      }
      if (url.endsWith("/api/auth/memberships")) {
        return json([
          { tenantId: "t-a", name: "Alpha", role: "admin", current: true },
          { tenantId: "t-b", name: "Beta", role: "specialist", current: false },
        ]);
      }
      return json({}, 404);
    }),
  );
  qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  await login({ email: "a@x", password: "p" });
  meSnapshots = [];
  qc.setQueryData(SEEDED_KEY, { secret: "tenant A" });
  replace.mockClear();
});
afterEach(() => {
  clearSession(false);
  vi.unstubAllGlobals();
});

async function renderAndSwitch() {
  render(
    <QueryClientProvider client={qc}>
      <BusinessSwitcher />
    </QueryClientProvider>,
  );
  const select = await screen.findByLabelText("Бізнес");
  fireEvent.change(select, { target: { value: "t-b" } });
}

describe("BusinessSwitcher: ізоляція кешу tenant (R-1, §31.11, §31.15 п.29)", () => {
  it("кеш порожній у момент першого apiFetch з новим токеном", async () => {
    await renderAndSwitch();
    await waitFor(() => expect(replace).toHaveBeenCalled());
    const first = meSnapshots[0];
    expect(first.auth).toBe("Bearer t2");
    expect(first.seededInCache).toBe(false);
    expect(qc.getQueryData(SEEDED_KEY)).toBeUndefined();
  });

  it("при помилці switch кеш НЕ очищається", async () => {
    switchResponse = () => json({ code: "membership_not_found", message: "x" }, 404);
    await renderAndSwitch();
    await screen.findByRole("alert");
    expect(qc.getQueryData(SEEDED_KEY)).toEqual({ secret: "tenant A" });
    expect(replace).not.toHaveBeenCalled();
  });
});
