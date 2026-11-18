import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("./types", async (orig) => ({ ...(await orig<typeof import("./types")>()), USE_MOCK: false }));

import { BeautyApiError } from "./errors";
import { clearSession, fetchMemberships, getUser, login, switchBusiness } from "./session";

const json = (body: unknown, status = 200, headers: Record<string, string> = {}) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json", ...headers } });
const U1 = { id: "m1", email: "a@x", fullName: "N", role: "admin", specialistId: null };
const U2 = { id: "m2", email: "a@x", fullName: "N", role: "specialist", specialistId: "s1" };
const meOf = (u: { id: string; email: string; fullName: string; role: string; specialistId: string | null }, perm: string) => ({ ...u, accessVersion: 1, accessStamp: `1.${u.id}`, catalogVersion: 1, modules: [], permissions: { "calendar.view": perm } });

let me: object;
let handlers: Record<string, () => Response>;
let sent: { path: string; init: RequestInit }[];

beforeEach(() => {
  sent = [];
  me = meOf(U1, "all");
  handlers = {
    "/api/session/login": () => json({ accessToken: "t1", expiresInSeconds: 900, user: U1 }),
    "/api/session/switch": () => json({ accessToken: "t2", expiresInSeconds: 900, user: U2 }),
  };
  vi.stubGlobal(
    "fetch",
    vi.fn(async (url: string, init: RequestInit) => {
      sent.push({ path: url, init });
      if (url.endsWith("/api/auth/me")) return json(me);
      if (url.endsWith("/api/auth/memberships")) {
        return json([
          { tenantId: "t-a", name: "Alpha", role: "admin", current: true },
          { tenantId: "t-b", name: "Beta", role: "specialist", current: false },
          { bad: true },
        ]);
      }
      return handlers[url]?.() ?? json({}, 404);
    }),
  );
});
afterEach(() => {
  clearSession(false);
  vi.unstubAllGlobals();
});

describe("login: discriminator selectionRequired (§31.2)", () => {
  it("варіант B не читає accessToken і не створює сесію", async () => {
    handlers["/api/session/login"] = () => json({ selectionRequired: true, ticketExpiresInSeconds: 120, businesses: [{ tenantId: "t-a", name: "Alpha", role: "admin" }] });
    const r = await login({ email: "a@x", password: "p" });
    expect(r).toEqual({ kind: "selection", ticketExpiresInSeconds: 120, businesses: [{ tenantId: "t-a", name: "Alpha", role: "admin" }] });
    expect(getUser()).toBeNull();
  });
});

describe("switchBusiness (§31.5, ADR-023)", () => {
  it("надсилає Bearer, нові права беруться з me цілі, а не зливаються зі старими", async () => {
    await login({ email: "a@x", password: "p" });
    expect(getUser()?.permissions).toEqual({ "calendar.view": "all" });
    me = meOf(U2, "own");
    const next = await switchBusiness("t-b");
    const call = sent.find((s) => s.path === "/api/session/switch")!;
    expect((call.init.headers as Record<string, string>).Authorization).toBe("Bearer t1");
    expect(JSON.parse(String(call.init.body))).toEqual({ tenantId: "t-b" });
    expect(next.id).toBe("m2");
    expect(next.permissions).toEqual({ "calendar.view": "own" });
    expect(next.accessStamp).toBe("1.m2");
  });

  it("404 membership_not_found пробрасується, сесія лишається", async () => {
    await login({ email: "a@x", password: "p" });
    handlers["/api/session/switch"] = () => json({ code: "membership_not_found", message: "x" }, 404);
    await expect(switchBusiness("t-b")).rejects.toMatchObject({ status: 404, code: "membership_not_found" });
    expect(getUser()?.id).toBe("m1");
  });

  it("401 завершує сесію", async () => {
    await login({ email: "a@x", password: "p" });
    handlers["/api/session/switch"] = () => json({ code: "invalid_token", message: "x" }, 401);
    await expect(switchBusiness("t-b")).rejects.toBeInstanceOf(BeautyApiError);
    expect(getUser()).toBeNull();
  });
});

describe("fetchMemberships (§31.4)", () => {
  it("розбирає список і відкидає сміття", async () => {
    await login({ email: "a@x", password: "p" });
    expect(await fetchMemberships()).toEqual([
      { tenantId: "t-a", name: "Alpha", role: "admin", current: true },
      { tenantId: "t-b", name: "Beta", role: "specialist", current: false },
    ]);
  });
});
