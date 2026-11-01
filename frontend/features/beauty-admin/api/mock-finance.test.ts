import { describe, expect, it } from "vitest";
import { BeautyApiError } from "@/features/beauty-auth/errors";
import { CLIENT_SEED, DEMO_LOCATIONS, STAFF, absencesSeed, appointmentsSeed, closuresSeed, costSnapshotFor, historySeed } from "../../beauty-shared/demo-seed";
import { toCents } from "../analytics/finance";
import { createMockAnalytics } from "./mock-analytics";
import { createMockCosts, COST_WRITES_PER_MINUTE } from "./mock-costs";
import { SERVICES } from "./mock-data";
import { createMockPhones } from "./mock-phone";
import type { AnalyticsDetails, DetailsItem } from "../types";

// Середа 14.10.2026, 12:00 за Києвом.
const TODAY = "2026-10-14";
const NOW = new Date("2026-10-14T09:00:00Z");
const appts = [...historySeed(TODAY, NOW), ...appointmentsSeed(TODAY, NOW)];
const api = createMockAnalytics({
  appointments: appts,
  locations: DEMO_LOCATIONS,
  staff: STAFF,
  absences: absencesSeed(TODAY),
  closures: closuresSeed(TODAY),
  today: TODAY,
  snapshotOf: (a) => costSnapshotFor(a, TODAY),
});

async function code(fn: () => unknown): Promise<{ status: number; code: string }> {
  try {
    await fn();
  } catch (e) {
    const err = e as BeautyApiError;
    return { status: err.status, code: err.code };
  }
  throw new Error("очікувалась помилка");
}
const cents = (v: number) => toCents(v);

/** Усі сторінки деталей; перевіряє відсутність дублів. */
function allItems(q: Parameters<typeof api.details>[0], take = 50): { items: DetailsItem[]; pages: AnalyticsDetails[] } {
  const pages: AnalyticsDetails[] = [];
  let cursor: string | undefined;
  do {
    const p = api.details({ ...q, take, ...(cursor ? { cursor } : {}) });
    pages.push(p);
    cursor = p.nextCursor;
  } while (cursor);
  const items = pages.flatMap((p) => p.items);
  expect(new Set(items.map((i) => i.appointmentId)).size).toBe(items.length);
  return { items, pages };
}

const RANGE = { from: "2026-09-14", to: "2026-10-14" }; // 31 день

describe("§24.5: інваріанти фінансів у mock", () => {
  it("Σ точок series = network за грошовими полями й withoutCost", () => {
    const net = api.network({ ...RANGE, compare: false });
    const s = api.series(RANGE);
    const sum = (k: "revenue" | "cost" | "masterPayout" | "netIncome") => s.points.reduce((a, p) => a + cents(p[k] ?? 0), 0);
    expect(sum("revenue")).toBe(cents(net.totals.revenue));
    expect(sum("cost")).toBe(cents(net.totals.cost!));
    expect(sum("masterPayout")).toBe(cents(net.totals.masterPayout!));
    expect(sum("netIncome")).toBe(cents(net.totals.netIncome!));
    expect(s.points.reduce((a, p) => a + (p.withoutCost ?? 0), 0)).toBe(net.totals.withoutCost);
    expect(cents(net.totals.netIncome!)).toBe(cents(net.totals.revenue) - cents(net.totals.cost!) - cents(net.totals.masterPayout!));
  });

  it("Σ закладів = network; marginPercent = round(net / revenue × 100, 1)", () => {
    const net = api.network({ ...RANGE, compare: false }).totals;
    const locs = api.locations({ ...RANGE, compare: false });
    expect(locs.reduce((a, l) => a + cents(l.netIncome!), 0)).toBe(cents(net.netIncome!));
    expect(locs.reduce((a, l) => a + cents(l.cost!), 0)).toBe(cents(net.cost!));
    expect(locs.reduce((a, l) => a + (l.withoutCost ?? 0), 0)).toBe(net.withoutCost);
    expect(net.marginPercent).toBe(Math.round((net.netIncome! / net.revenue) * 1000) / 10);
  });

  it("є і записи з собівартістю, і без (демо попередження); різні заклади", () => {
    const net = api.network({ from: "2026-07-01", to: "2026-10-14", compare: false }).totals;
    expect(net.withoutCost).toBeGreaterThan(0);
    expect(net.completed - net.withoutCost!).toBeGreaterThan(0);
    expect(net.revenueWithoutCost).toBeGreaterThan(0);
    expect(api.details({ from: RANGE.from, to: RANGE.to }).locations.length).toBeGreaterThan(1);
  });

  it("details.summary за день = точка series; тиждень/місяць = Σ днів; Σ закладів = summary", () => {
    const days = api.series(RANGE).points;
    const day = days.find((p) => p.completed > 1)!;
    const d = api.details({ date: day.from });
    expect(d.summary.completed).toBe(day.completed);
    expect(cents(d.summary.revenue)).toBe(cents(day.revenue));
    expect(cents(d.summary.netIncome)).toBe(cents(day.netIncome!));
    expect(cents(d.summary.cost)).toBe(cents(day.cost!));
    expect(d.summary.withoutCost).toBe(day.withoutCost);
    expect(d.locations.reduce((a, l) => a + cents(l.netIncome), 0)).toBe(cents(d.summary.netIncome));
    expect(d.locations.reduce((a, l) => a + cents(l.revenue), 0)).toBe(cents(d.summary.revenue));

    const week = api.series({ from: "2026-07-20", to: "2026-10-14" }).points.find((p) => p.from === "2026-09-21" && p.to === "2026-09-27")!;
    const w = api.details({ from: week.from, to: week.to });
    expect(cents(w.summary.netIncome)).toBe(cents(week.netIncome!));
    expect(w.summary.withoutCost).toBe(week.withoutCost);

    const month = api.details(RANGE).summary;
    expect(cents(month.netIncome)).toBe(cents(api.network({ ...RANGE, compare: false }).totals.netIncome!));
  });

  it("Σ net записів по всіх сторінках = summary.netIncome; сторінки без дублів; hasMore/nextCursor", () => {
    const { items, pages } = allItems(RANGE, 50);
    expect(pages.length).toBeGreaterThan(1);
    expect(pages[0].hasMore).toBe(true);
    expect(pages.at(-1)!.hasMore).toBe(false);
    expect(pages.at(-1)!.nextCursor).toBeUndefined();
    expect(items.reduce((a, i) => a + cents(i.net), 0)).toBe(cents(pages[0].summary.netIncome));
    expect(items).toHaveLength(pages[0].summary.completed);
    const keys = items.map((i) => `${i.localDate}T${i.localTime}`);
    expect(keys).toEqual([...keys].sort());
  });

  it("запис без собівартості: cost/payout = null, net = ціна продажу; з собівартістю: формули §24.5", () => {
    const { items } = allItems(RANGE, 200);
    const missing = items.find((i) => i.costMissing)!;
    expect(missing).toMatchObject({ cost: null, masterPercent: null, masterPayout: null });
    expect(missing.net).toBe(missing.priceFinal);
    const full = items.find((i) => !i.costMissing && i.masterPayout! > 0)!;
    expect(cents(full.net)).toBe(cents(full.priceFinal) - cents(full.cost!) - cents(full.masterPayout!));
    expect(full.discount).toBe(full.priceOriginal - full.priceFinal);
    const promo = items.find((i) => i.promotion)!;
    expect(promo.discount).toBeGreaterThan(0);
  });

  it("override закладу діє на весь рядок: Печерськ без відсотка майстру (мережевих 40% не успадковує)", () => {
    const { items } = allItems({ from: "2026-08-20", to: "2026-09-19" }, 200);
    const atK = items.find((i) => i.service.id === "mn" && i.location.id === "k" && !i.costMissing)!;
    const atC = items.find((i) => i.service.id === "mn" && i.location.id === "c" && !i.costMissing)!;
    expect(atK).toMatchObject({ cost: 260, masterPercent: null, masterPayout: 0 });
    expect(atC).toMatchObject({ cost: 220, masterPercent: 40 });
  });

  it("записи до розгортання (давніші за 45 днів) - без собівартості", () => {
    const old = api.details({ from: "2026-05-01", to: "2026-05-31" });
    expect(old.summary.completed).toBeGreaterThan(0);
    expect(old.summary.withoutCost).toBe(old.summary.completed);
    expect(old.summary.cost).toBe(0);
  });

  it("порожній період: суми 0, marginPercent = null, withoutCost = 0", () => {
    const d = api.details({ date: "2026-10-18" }); // майбутній день без завершених
    expect(d.summary).toMatchObject({ completed: 0, revenue: 0, cost: 0, masterPayout: 0, netIncome: 0, marginPercent: null, withoutCost: 0 });
    expect(d.items).toEqual([]);
    expect(d.hasMore).toBe(false);
  });

  it("DST/межі: localDate і localTime беруться з локального часу закладу", () => {
    const d = api.details({ date: api.series(RANGE).points.find((p) => p.completed > 0)!.from });
    for (const i of d.items) {
      expect(i.localDate).toMatch(/^\d{4}-\d{2}-\d{2}$/);
      expect(i.localTime).toMatch(/^\d{2}:\d{2}$/);
    }
  });
});

describe("§24.6.3: валідація й курсор", () => {
  it("кодові помилки", async () => {
    expect(await code(() => api.details({}))).toEqual({ status: 422, code: "invalid_range" });
    expect(await code(() => api.details({ date: "2026-10-01", from: "2026-10-01" }))).toEqual({ status: 422, code: "invalid_range" });
    expect(await code(() => api.details({ date: "2026-02-30" }))).toEqual({ status: 422, code: "invalid_date" });
    expect(await code(() => api.details({ date: "1999-12-31" }))).toEqual({ status: 422, code: "invalid_date" });
    expect(await code(() => api.details({ date: "2026-10-01T10:00" }))).toEqual({ status: 422, code: "invalid_date" });
    expect(await code(() => api.details({ from: "2026-09-01", to: "2026-10-02" }))).toEqual({ status: 422, code: "invalid_range" }); // 32 дні
    expect(await code(() => api.details({ from: "2026-10-10T00:00", to: "2026-10-12" }))).toEqual({ status: 422, code: "invalid_range" });
    expect(await code(() => api.details({ from: "2026-10-12", to: "2026-10-10" }))).toEqual({ status: 422, code: "invalid_range" });
    expect(await code(() => api.details({ from: "2026-10-01" }))).toEqual({ status: 422, code: "invalid_range" });
    expect(await code(() => api.details({ date: "2026-10-01", locationId: "nope" }))).toEqual({ status: 404, code: "location_not_found" });
  });

  it("рівно 31 день дозволено; take обмежується 1..200", () => {
    expect(() => api.details({ from: "2026-09-14", to: "2026-10-14" })).not.toThrow();
    expect(api.details({ ...RANGE, take: 500 }).items.length).toBeLessThanOrEqual(200);
    expect(api.details({ ...RANGE, take: 0 }).items.length).toBeGreaterThanOrEqual(1);
  });

  it("курсор нерозбірний або від інших параметрів -> 422 invalid_cursor", async () => {
    const p = api.details({ ...RANGE, take: 5 });
    expect(p.nextCursor).toBeTruthy();
    expect(await code(() => api.details({ ...RANGE, cursor: "###" }))).toEqual({ status: 422, code: "invalid_cursor" });
    expect(await code(() => api.details({ ...RANGE, locationId: "c", cursor: p.nextCursor }))).toEqual({ status: 422, code: "invalid_cursor" });
    expect(await code(() => api.details({ from: "2026-09-15", to: "2026-10-14", cursor: p.nextCursor }))).toEqual({ status: 422, code: "invalid_cursor" });
  });

  it("locationId звужує підсумок і записи; location у відповіді", () => {
    const all = api.details(RANGE).summary;
    const c = api.details({ ...RANGE, locationId: "c", take: 200 });
    expect(c.location).toMatchObject({ id: "c", name: "Центр" });
    expect(c.items.every((i) => i.location.id === "c")).toBe(true);
    expect(c.summary.completed).toBeLessThan(all.completed);
    expect(c.locations.map((l) => l.locationId)).toEqual(["c"]);
  });
});

describe("§24.9: у деталях немає даних клієнтів", () => {
  it("відповідь не містить імен, телефонів, clientId, нотаток", () => {
    const { pages } = allItems(RANGE, 200);
    const json = JSON.stringify(pages);
    for (const c of CLIENT_SEED) {
      expect(json).not.toContain(c.name);
      expect(json).not.toContain(c.phone);
      expect(json).not.toContain(`"${c.id}"`);
    }
    expect(json).not.toMatch(/clientId|clientName|phone|email|note|reason/i);
  });
});

describe("§24.6.1: mock собівартості", () => {
  const make = (role = "owner", t = { now: Date.parse("2026-10-14T09:00:00Z") }) => ({
    api: createMockCosts({ role: () => role, services: () => SERVICES, locations: () => DEMO_LOCATIONS, wait: false, now: () => t.now }),
    t,
  });

  it("список: мережа + override закладів; мережевого значення може не бути", async () => {
    const { api: c } = make();
    const rows = await c.getServiceCosts();
    const mn = rows.find((r) => r.serviceId === "mn")!;
    expect(mn.network).toMatchObject({ costAmount: 220, masterPercent: 40 });
    expect(mn.locations).toEqual([expect.objectContaining({ locationId: "k", locationName: "Печерськ", costAmount: 260, masterPercent: null })]);
    expect(rows.find((r) => r.serviceId === "br")!.network).toBeUndefined();
  });

  it("specialist -> 403 на всі методи", async () => {
    const { api: c } = make("specialist");
    expect(await code(() => c.getServiceCosts())).toEqual({ status: 403, code: "forbidden_role" });
    expect(await code(() => c.getServiceCostDetail("mn"))).toEqual({ status: 403, code: "forbidden_role" });
    expect(await code(() => c.setServiceCost("mn", { locationId: null, costAmount: 1 }))).toEqual({ status: 403, code: "forbidden_role" });
    expect(await code(() => c.deleteServiceCost("mn", null))).toEqual({ status: 403, code: "forbidden_role" });
  });

  it("PUT/DELETE: upsert, 404-и, 422 з межами", async () => {
    const { api: c } = make("admin");
    const saved = await c.setServiceCost("br", { locationId: null, costAmount: 99.5, masterPercent: 25 });
    expect(saved).toMatchObject({ costAmount: 99.5, masterPercent: 25, locationId: null, updatedBy: { name: "Світлана Коваленко" } });
    await c.setServiceCost("br", { locationId: "p", costAmount: 120 });
    expect((await c.getServiceCostDetail("br")).locations).toEqual([expect.objectContaining({ locationId: "p", masterPercent: null })]);
    await c.deleteServiceCost("br", "p");
    expect((await c.getServiceCostDetail("br")).locations).toEqual([]);
    expect(await code(() => c.deleteServiceCost("br", "p"))).toEqual({ status: 404, code: "cost_not_found" });
    expect(await code(() => c.setServiceCost("zzz", { locationId: null, costAmount: 1 }))).toEqual({ status: 404, code: "service_not_found" });
    expect(await code(() => c.setServiceCost("br", { locationId: "nope", costAmount: 1 }))).toEqual({ status: 404, code: "location_not_found" });
    for (const bad of [-1, 1_000_000.01, 1.234, Number.NaN, undefined as unknown as number]) {
      expect(await code(() => c.setServiceCost("br", { locationId: null, costAmount: bad }))).toEqual({ status: 422, code: "invalid_cost" });
    }
    for (const bad of [101, -0.01, 1.234]) {
      expect(await code(() => c.setServiceCost("br", { locationId: null, costAmount: 1, masterPercent: bad }))).toEqual({ status: 422, code: "invalid_master_percent" });
    }
    await expect(c.setServiceCost("br", { locationId: null, costAmount: 1_000_000, masterPercent: 100 })).resolves.toBeTruthy();
  });

  it("без змін - 200 без оновлення updatedAt", async () => {
    const { api: c, t } = make();
    const a = await c.setServiceCost("br", { locationId: null, costAmount: 10, masterPercent: 5 });
    t.now += 5000;
    const b = await c.setServiceCost("br", { locationId: null, costAmount: 10, masterPercent: 5 });
    expect(b.updatedAt).toBe(a.updatedAt);
    t.now += 5000;
    const d = await c.setServiceCost("br", { locationId: null, costAmount: 11, masterPercent: 5 });
    expect(d.updatedAt).not.toBe(a.updatedAt);
  });

  it("429 rate_limited після ліміту записів за хвилину; вікно зсувається", async () => {
    const { api: c, t } = make();
    for (let i = 0; i < COST_WRITES_PER_MINUTE; i++) await c.setServiceCost("br", { locationId: null, costAmount: i });
    const e = await c.setServiceCost("br", { locationId: null, costAmount: 1 }).catch((x: BeautyApiError) => x);
    expect(e).toMatchObject({ status: 429, code: "rate_limited", retryAfterSeconds: 30 });
    t.now += 61_000;
    await expect(c.setServiceCost("br", { locationId: null, costAmount: 1 })).resolves.toBeTruthy();
  });

  it("зміна собівартості не міняє знімки завершених записів (аналітика незмінна)", async () => {
    const before = api.details(RANGE).summary;
    const { api: c } = make();
    await c.setServiceCost("mn", { locationId: null, costAmount: 999, masterPercent: 90 });
    expect(api.details(RANGE).summary).toEqual(before);
  });
});

describe("§25.7: mock телефонів і phone-pending", () => {
  const make = (role = "owner") => createMockPhones({ role: () => role, wait: false, now: () => NOW.getTime() });

  it("стан із сіду: джерело, згода, pending і можливий дубль", () => {
    const p = make();
    expect(p.info("oc")).toMatchObject({ phone: "+380 50 000 00 01", phoneSource: "telegram_contact" });
    expect(p.info("oc")!.callConsentAt).toBeTruthy();
    expect(p.info("kl")!.phonePending).toMatchObject({ phone: "+380 67 111 22 33", source: "telegram_contact" });
    expect(p.info("mp")!.possibleDuplicate).toEqual({ id: "sm", name: "Софія Мартин" });
    expect(p.info("sm")!.phonePending).toBeUndefined();
  });

  it("accept: номер, джерело й згода переходять з pending; pending очищується; повтор -> 409 no_pending_phone", async () => {
    const p = make("admin");
    const r = await p.resolvePhonePending("kl", "accept");
    expect(r).toMatchObject({ phone: "+380 67 111 22 33", phoneSource: "telegram_contact", phonePending: null });
    expect(p.info("kl")).toMatchObject({ phone: "+380 67 111 22 33", phoneSource: "telegram_contact" });
    expect(p.info("kl")!.phonePending).toBeUndefined();
    expect(p.phoneOf("kl")).toBe("+380 67 111 22 33");
    expect(await code(() => p.resolvePhonePending("kl", "accept"))).toEqual({ status: 409, code: "no_pending_phone" });
  });

  it("reject: поточний номер лишається, pending очищується", async () => {
    const p = make();
    await p.resolvePhonePending("kl", "reject");
    expect(p.phoneOf("kl")).toBe("+380 50 000 00 03");
    expect(p.pendingOf("kl")).toBeUndefined();
  });

  it("коди помилок: 404, 422, 403 для specialist", async () => {
    expect(await code(() => make().resolvePhonePending("zzz", "accept"))).toEqual({ status: 404, code: "client_not_found" });
    expect(await code(() => make().resolvePhonePending("kl", "merge" as never))).toEqual({ status: 422, code: "invalid_action" });
    expect(await code(() => make("specialist").resolvePhonePending("kl", "accept"))).toEqual({ status: 403, code: "forbidden_role" });
  });

  it("для inbox віддається лише маска", () => {
    const p = make();
    expect(p.maskedOf("oc")).toBe("+••••••••••01");
    expect(p.maskedOf("oc")).not.toContain("380");
  });
});
