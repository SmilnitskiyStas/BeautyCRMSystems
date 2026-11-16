import { beforeEach, describe, expect, it, vi } from "vitest";

type Init = { method?: string; body?: string };
const apiJson = vi.fn<(path: string, init?: Init) => Promise<unknown>>();
vi.mock("@/features/beauty-auth/session", () => ({ apiJson: (p: string, i?: Init) => apiJson(p, i) }));

import { httpTeamApi } from "./http-team";

const last = () => apiJson.mock.calls[apiJson.mock.calls.length - 1]!;
beforeEach(() => apiJson.mockReset());

describe("http-клієнт «Команда й доступи»", () => {
  it("список: manageable лише за явним true (fail-closed)", async () => {
    apiJson.mockResolvedValueOnce([{ id: "1", email: "a", fullName: "A", role: "admin", isActive: true, accessVersion: 3 }, { id: "2", role: "deputy", manageable: true }]);
    const u = await httpTeamApi.getTeamUsers();
    expect(u.map((x) => x.manageable)).toEqual([false, true]);
    expect(last()[0]).toBe("/api/users");
  });

  it("доступ: editable лише за true; assignable/locked розбираються", async () => {
    apiJson.mockResolvedValueOnce({ userId: "u", role: "deputy", accessVersion: 4, granted: { "calendar.view": "all" }, assignable: { "revenue.view": ["none", "own"], bad: 1 }, locked: ["x"] });
    const a = await httpTeamApi.getUserAccess("u");
    expect(a.editable).toBe(false);
    expect(a.assignable["revenue.view"]).toEqual(["none", "own"]);
    expect(a.locked).toEqual(["x"]);
  });

  it("PUT: expectedVersion, лише змінені права, dryRun", async () => {
    apiJson.mockResolvedValue({ userId: "u", changes: [{ permission: "revenue.view", from: "none", to: "own" }], roleChange: { from: "deputy", to: "admin" }, clamped: [{ permission: "ai.settings", requested: "edit", applied: "none", reason: "locked" }] });
    const r = await httpTeamApi.saveUserAccess("u 1", { expectedVersion: 4, role: "admin", permissions: { "revenue.view": "own" }, dryRun: true });
    expect(last()[0]).toBe("/api/users/u%201/access");
    expect(last()[1]!.method).toBe("PUT");
    expect(JSON.parse(last()[1]!.body!)).toEqual({ expectedVersion: 4, role: "admin", permissions: { "revenue.view": "own" }, dryRun: true });
    expect(r.roleChange).toEqual({ from: "deputy", to: "admin" });
    expect(r.clamped[0]!.reason).toBe("locked");
  });

  it("reset, status, invite, audit, me/revenue: шляхи й тіла", async () => {
    apiJson.mockResolvedValue({});
    await httpTeamApi.resetUserAccess("u", { expectedVersion: 2 });
    expect([last()[0], JSON.parse(last()[1]!.body!)]).toEqual(["/api/users/u/access/reset", { expectedVersion: 2, dryRun: false }]);
    await httpTeamApi.setUserActive("u", false, 5);
    expect([last()[1]!.method, JSON.parse(last()[1]!.body!)]).toEqual(["PATCH", { isActive: false, expectedVersion: 5 }]);
    apiJson.mockResolvedValueOnce({ token: "t", invite: { id: "i", email: "e", role: "deputy" }, clamped: [{ permission: "k", requested: "all", applied: "own", reason: "ceiling" }] });
    const inv = await httpTeamApi.createTeamInvite({ email: " e@x.ua ", role: "deputy", permissions: { "revenue.view": "own" } });
    expect(JSON.parse(last()[1]!.body!)).toEqual({ email: "e@x.ua", role: "deputy", permissions: { "revenue.view": "own" } });
    expect(inv.token).toBe("t");
    expect(inv.clamped).toHaveLength(1);
    apiJson.mockResolvedValueOnce({ items: [{ id: "a", action: "invited", actor: null, changes: [] }], nextCursor: "c" });
    const page = await httpTeamApi.getAccessAudit({ userId: "u", cursor: "x" });
    expect(last()[0]).toBe("/api/users/access-audit?userId=u&cursor=x&take=50");
    expect(page.nextCursor).toBe("c");
    expect(page.items[0]!.actor).toBeNull();
    await httpTeamApi.getMyRevenue({ from: "2026-11-01", to: "2026-11-15" });
    expect(last()[0]).toBe("/api/beauty/me/revenue?from=2026-11-01&to=2026-11-15");
  });
});
