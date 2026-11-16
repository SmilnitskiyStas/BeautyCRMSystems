import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("./types", async (orig) => ({ ...(await orig<typeof import("./types")>()), USE_MOCK: false }));

import { apiFetch, clearSession, login, onUserChange, reloadMe } from "./session";

const ME = (stamp: string, calendar = "all") => ({
  id: "u1", email: "a@b", fullName: "N", role: "admin", specialistId: null,
  accessVersion: 1, accessStamp: stamp, catalogVersion: 1, modules: ["beauty_booking"], permissions: { "calendar.view": calendar },
});
const json = (body: unknown, status = 200, headers: Record<string, string> = {}) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json", ...headers } });

let paths: string[];
let meStamp: string;
let respond: (path: string) => Response;

beforeEach(() => {
  paths = [];
  meStamp = "1.aaaa0000";
  respond = () => json({ ok: true });
  vi.stubGlobal(
    "fetch",
    vi.fn(async (url: string) => {
      paths.push(url);
      if (url === "/api/session/login") return json({ accessToken: "t", expiresInSeconds: 900, user: { id: "u1", email: "a@b", fullName: "N", role: "admin", specialistId: null } });
      if (url.endsWith("/api/auth/me")) return json(ME(meStamp, meStamp.startsWith("2") ? "own" : "all"));
      return respond(url);
    }),
  );
});
afterEach(() => {
  clearSession(false);
  vi.unstubAllGlobals();
});

const meCalls = () => paths.filter((p) => p.endsWith("/api/auth/me")).length;
const tick = () => new Promise((r) => setTimeout(r, 0));

describe("права з me: login, X-Access-Version, 403", () => {
  it("після login права беруться з GET /api/auth/me", async () => {
    const u = await login({ tenant: "t", email: "a@b", password: "x" });
    expect(meCalls()).toBe(1);
    expect(u.permissions).toEqual({ "calendar.view": "all" });
    expect(u.accessStamp).toBe("1.aaaa0000");
  });

  it("X-Access-Version != accessStamp -> me перечитується, підписники отримують нові права", async () => {
    await login({ tenant: "t", email: "a@b", password: "x" });
    const seen: string[] = [];
    const off = onUserChange((u) => seen.push(u.permissions?.["calendar.view"] ?? "?"));
    meStamp = "2.bbbb0000";
    respond = () => json({ ok: true }, 200, { "X-Access-Version": "2.bbbb0000" });
    await apiFetch("/api/beauty/appointments");
    await tick();
    await tick();
    expect(meCalls()).toBe(2);
    expect(seen).toEqual(["own"]);
    off();
  });

  it("збіг заголовка зі stamp не викликає me", async () => {
    await login({ tenant: "t", email: "a@b", password: "x" });
    respond = () => json({ ok: true }, 200, { "X-Access-Version": "1.aaaa0000" });
    await apiFetch("/api/beauty/appointments");
    await tick();
    expect(meCalls()).toBe(1);
  });

  it("403 forbidden_role з permission перечитує me; без permission (ранг/owner-only) - ні", async () => {
    await login({ tenant: "t", email: "a@b", password: "x" });
    respond = () => json({ code: "forbidden_role", message: "x" }, 403);
    await expect(apiFetch("/api/beauty/x")).rejects.toMatchObject({ status: 403, permission: undefined });
    await tick();
    expect(meCalls()).toBe(1);

    meStamp = "2.bbbb0000";
    respond = () => json({ code: "forbidden_role", message: "x", permission: "revenue.view", required: "all" }, 403);
    await expect(apiFetch("/api/beauty/y")).rejects.toMatchObject({ status: 403, permission: "revenue.view" });
    await tick();
    await tick();
    expect(meCalls()).toBe(2);
  });

  it("403 module_disabled перечитує me", async () => {
    await login({ tenant: "t", email: "a@b", password: "x" });
    meStamp = "2.bbbb0000";
    respond = () => json({ code: "module_disabled", message: "x" }, 403);
    await expect(apiFetch("/api/beauty/z")).rejects.toMatchObject({ code: "module_disabled" });
    await tick();
    await tick();
    expect(meCalls()).toBe(2);
  });

  it("reloadMe single-flight: паралельні виклики дають один запит", async () => {
    await login({ tenant: "t", email: "a@b", password: "x" });
    await Promise.all([reloadMe(), reloadMe(), reloadMe()]);
    expect(meCalls()).toBe(2);
  });
});
