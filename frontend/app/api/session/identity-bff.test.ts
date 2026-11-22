// @vitest-environment node
import { NextRequest } from "next/server";
import { afterEach, describe, expect, it, vi } from "vitest";
import { POST as login } from "./login/route";
import { POST as select } from "./login/select/route";
import { POST as switchTenant } from "./switch/route";

const TICKET = "T".repeat(43);
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

function req(path: string, body: unknown, headers: Record<string, string> = {}) {
  return new NextRequest(`http://localhost:3000${path}`, {
    method: "POST",
    headers: { origin: "http://localhost:3000", host: "localhost:3000", "Content-Type": "application/json", ...headers },
    body: JSON.stringify(body),
  });
}

let backendCalls: { url: string; body: Record<string, unknown>; headers: Record<string, string> }[];
function backend(handler: (url: string) => Response) {
  backendCalls = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (url: string, init: RequestInit) => {
      backendCalls.push({ url, body: JSON.parse(String(init.body)), headers: init.headers as Record<string, string> });
      return handler(url);
    }),
  );
}
afterEach(() => vi.unstubAllGlobals());

describe("BFF /api/session/login (§31.11)", () => {
  it("варіант B: loginTicket лише в cookie beauty_lt (HttpOnly, Strict, Path), не в JSON", async () => {
    backend(() => json({ selectionRequired: true, loginTicket: TICKET, ticketExpiresInSeconds: 180, businesses: [{ tenantId: "t", name: "A", role: "admin" }] }));
    const res = await login(req("/api/session/login", { email: "a@x.test", password: "p" }));
    const body = await res.json();
    expect(JSON.stringify(body)).not.toContain(TICKET);
    expect(body).toMatchObject({ selectionRequired: true, ticketExpiresInSeconds: 180 });
    const cookie = res.headers.get("set-cookie") ?? "";
    expect(cookie).toContain(`beauty_lt=${TICKET}`);
    expect(cookie).toMatch(/HttpOnly/i);
    expect(cookie).toMatch(/SameSite=strict/i);
    expect(cookie).toContain("Path=/api/session/login");
    expect(cookie).toContain("Max-Age=180");
    expect(res.headers.get("cache-control")).toBe("no-store");
  });

  it("tenant пересилається лише непорожнім; без нього тіло без tenant", async () => {
    backend(() => json({ accessToken: "a", expiresInSeconds: 900, refreshToken: "t.r", user: {} }));
    await login(req("/api/session/login", { email: "a@x.test", password: "p", tenant: "  " }));
    expect(backendCalls[0].body).toEqual({ email: "a@x.test", password: "p" });
    await login(req("/api/session/login", { email: "a@x.test", password: "p", tenant: "salon-x" }));
    expect(backendCalls[1].body).toEqual({ email: "a@x.test", password: "p", tenant: "salon-x" });
  });

  it("не-same-origin -> 403", async () => {
    backend(() => json({}));
    const res = await login(req("/api/session/login", { email: "a", password: "p" }, { origin: "http://evil.test" }));
    expect(res.status).toBe(403);
    expect(backendCalls).toHaveLength(0);
  });
});

describe("BFF /api/session/login/select", () => {
  it("без cookie -> 401 login_ticket_invalid без виклику backend", async () => {
    backend(() => json({}));
    const res = await select(req("/api/session/login/select", { tenantId: "t" }));
    expect(res.status).toBe(401);
    expect((await res.json()).code).toBe("login_ticket_invalid");
    expect(backendCalls).toHaveLength(0);
  });

  it.each([
    ["успіх", () => json({ accessToken: "a", expiresInSeconds: 900, refreshToken: "t.r", user: {} })],
    ["помилка", () => json({ code: "login_ticket_invalid", message: "x" }, 401)],
  ])("%s: квиток іде в backend, cookie квитка видаляється", async (_n, handler) => {
    backend(handler);
    const res = await select(req("/api/session/login/select", { tenantId: "t" }, { cookie: `beauty_lt=${TICKET}` }));
    expect(backendCalls[0].body).toEqual({ loginTicket: TICKET, tenantId: "t" });
    const cookies = res.headers.getSetCookie().join("\n");
    expect(cookies).toMatch(/beauty_lt=;[^\n]*Max-Age=0/);
  });
});

describe("BFF /api/session/switch", () => {
  it("додає refresh з cookie й Authorization; 401 прибирає beauty_rt", async () => {
    backend(() => json({ code: "invalid_token", message: "x" }, 401));
    const res = await switchTenant(req("/api/session/switch", { tenantId: "t" }, { authorization: "Bearer jwt", cookie: "beauty_rt=tid.rnd" }));
    expect(backendCalls[0].body).toEqual({ tenantId: "t", refreshToken: "tid.rnd" });
    expect(backendCalls[0].headers.Authorization).toBe("Bearer jwt");
    expect(res.headers.getSetCookie().join("\n")).toMatch(/beauty_rt=;[^\n]*Max-Age=0/);
  });

  it("без refresh-cookie -> 401 invalid_token без виклику backend", async () => {
    backend(() => json({}));
    const res = await switchTenant(req("/api/session/switch", { tenantId: "t" }, { authorization: "Bearer jwt" }));
    expect(res.status).toBe(401);
    expect(backendCalls).toHaveLength(0);
  });

  it("успіх замінює beauty_rt", async () => {
    backend(() => json({ accessToken: "a2", expiresInSeconds: 900, refreshToken: "t2.new", user: {} }));
    const res = await switchTenant(req("/api/session/switch", { tenantId: "t" }, { authorization: "Bearer jwt", cookie: "beauty_rt=tid.rnd" }));
    expect(res.headers.getSetCookie().join("\n")).toContain("beauty_rt=t2.new");
    expect(JSON.stringify(await res.json())).not.toContain("t2.new");
  });
});
