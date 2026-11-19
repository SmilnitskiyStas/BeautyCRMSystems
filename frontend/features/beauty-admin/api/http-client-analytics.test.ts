import { beforeEach, describe, expect, it, vi } from "vitest";

const apiJson = vi.fn<(path: string, init?: { method?: string; body?: string }) => Promise<unknown>>();
vi.mock("@/features/beauty-auth/session", () => ({
  apiJson: (path: string, init?: { method?: string; body?: string }) => apiJson(path, init),
  getUser: () => ({ id: "u", role: "owner", specialistId: null }),
}));

import { httpBeautyApi } from "./http-client";

let paths: string[] = [];
const totals = { appointments: 4, completed: 2, cancelled: 1, noShow: 1, revenue: 1500, averageCheck: 750, cancellationRate: 0.25, newClients: 1 };

beforeEach(() => {
  paths = [];
  apiJson.mockReset().mockImplementation(async (path) => {
    paths.push(decodeURIComponent(path));
    if (path.includes("/analytics/network/series")) return { fromDate: "2026-10-01", toDate: "2026-10-24", timezone: "Europe/Kyiv", granularity: "day", points: [] };
    if (path.includes("/analytics/network")) return { ...totals, fromDate: "2026-10-01", toDate: "2026-10-24", timezone: "Europe/Kyiv", previous: { ...totals, fromDate: "2026-09-07", toDate: "2026-09-30" } };
    if (path.includes("/breakdown")) return { locationId: "L1", name: "Центр", timezone: "Europe/Kyiv", fromDate: "2026-10-01", toDate: "2026-10-24", busyMinutes: 60, availableMinutes: 0, specialists: [{ specialistId: "s", name: "М", isActive: true, appointments: 1, completed: 1, cancelled: 0, noShow: 0, revenue: 100, busyMinutes: 60, availableMinutes: 0 }], services: [] };
    if (path.includes("/analytics/locations"))
      return [{ locationId: "L1", name: "Центр", appointments: 3, completed: 2, cancelled: 1, revenue: 900, timezone: "Europe/Kyiv", isActive: true, load: 0.5, busyMinutes: 60, availableMinutes: 120, noShow: 0, previous: { appointments: 1, completed: 1, cancelled: 0, noShow: 0, revenue: 300, busyMinutes: 0, availableMinutes: 0, load: null } }];
    if (path.includes("/analytics/promotions")) return [{ promotionId: "p1", name: "Осінь", uses: 2, discountTotal: 100, revenue: 800, previous: { uses: 1, discountTotal: 50, revenue: 400 } }];
    if (path.includes("/promotions")) return [{ id: "p1", name: "Осінь", startsAt: null, endsAt: null, locationIds: [] }];
    if (path.includes("/locations")) return [{ id: "L1", name: "Центр", timezone: "Europe/Kyiv", isActive: true }];
    throw new Error(`unexpected ${path}`);
  });
});

describe("§19: реальний клієнт - датний режим", () => {
  it("network: from/to як yyyy-MM-dd (не ISO з часом), compare=true лише на запит", async () => {
    await httpBeautyApi.getNetworkAnalytics({ from: "2026-10-01", to: "2026-10-24", compare: false });
    expect(paths).toEqual(["/api/beauty/analytics/network?from=2026-10-01&to=2026-10-24"]);
    paths = [];
    const n = await httpBeautyApi.getNetworkAnalytics({ from: "2026-10-01", to: "2026-10-24", compare: true });
    expect(paths).toEqual(["/api/beauty/analytics/network?from=2026-10-01&to=2026-10-24&compare=true"]);
    expect(n.timezone).toBe("Europe/Kyiv");
    expect(n.totals.revenue).toBe(1500);
    expect(n.previous).toMatchObject({ fromDate: "2026-09-07", toDate: "2026-09-30", revenue: 1500 });
  });

  it("без compare previous = null, навіть якщо сервер його віддав", async () => {
    expect((await httpBeautyApi.getNetworkAnalytics({ from: "2026-10-01", to: "2026-10-24", compare: false })).previous).toBeNull();
  });

  it("series: один запит замість 8, без compare", async () => {
    const s = await httpBeautyApi.getNetworkSeries({ from: "2026-10-01", to: "2026-10-24" });
    expect(paths).toEqual(["/api/beauty/analytics/network/series?from=2026-10-01&to=2026-10-24"]);
    expect(s.granularity).toBe("day");
  });

  it("locations: load/noShow/previous; відсутній load = null; жодного /appointments", async () => {
    const rows = await httpBeautyApi.getLocationsAnalytics({ from: "2026-10-01", to: "2026-10-24", compare: true });
    expect(rows[0]).toMatchObject({ load: 0.5, availableMinutes: 120, noShow: 0, isActive: true, previous: { revenue: 300, load: null } });
    expect(paths.every((p) => p.startsWith("/api/beauty/analytics/locations?"))).toBe(true);
    expect(paths[0]).toContain("&compare=true");
  });

  it("breakdown: окремий ендпоінт, відсутній load = null, без /appointments", async () => {
    const b = await httpBeautyApi.getLocationBreakdown("L1", { from: "2026-10-01", to: "2026-10-24" });
    expect(paths).toEqual(["/api/beauty/analytics/locations/L1/breakdown?from=2026-10-01&to=2026-10-24"]);
    expect(b.load).toBeNull();
    expect(b.specialists[0].load).toBeNull();
  });

  it("promotions: previous лише з compare", async () => {
    const withCmp = await httpBeautyApi.getPromotionAnalytics({ from: "2026-10-01", to: "2026-10-24", compare: true });
    expect(withCmp.rows[0]).toMatchObject({ uses: 2, previous: { uses: 1, revenue: 400 } });
    expect(paths.find((p) => p.includes("/analytics/promotions"))).toContain("&compare=true");
    const no = await httpBeautyApi.getPromotionAnalytics({ from: "2026-10-01", to: "2026-10-24", compare: false });
    expect(no.rows[0].previous).toBeNull();
  });

  it("422 invalid_range від сервера пробрасується", async () => {
    apiJson.mockRejectedValueOnce(Object.assign(new Error("x"), { status: 422, code: "invalid_range" }));
    await expect(httpBeautyApi.getNetworkAnalytics({ from: "2026-10-30", to: "2026-10-01", compare: false })).rejects.toMatchObject({ code: "invalid_range" });
  });
});
