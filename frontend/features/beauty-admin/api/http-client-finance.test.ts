import { beforeEach, describe, expect, it, vi } from "vitest";

const apiJson = vi.fn<(path: string, init?: { method?: string; body?: string; cache?: string }) => Promise<unknown>>();
vi.mock("@/features/beauty-auth/session", () => ({
  apiJson: (path: string, init?: { method?: string; body?: string; cache?: string }) => apiJson(path, init),
  getUser: () => ({ id: "u", role: "owner", specialistId: null }),
}));

import { httpBeautyApi } from "./http-client";

let calls: { path: string; init?: { method?: string; body?: string; cache?: string } }[] = [];
let respond: (path: string) => unknown;
const last = () => calls.at(-1)!;

beforeEach(() => {
  calls = [];
  respond = () => ({});
  apiJson.mockReset().mockImplementation(async (path, init) => {
    calls.push({ path: decodeURIComponent(path), init });
    return respond(path);
  });
});

const item = {
  appointmentId: "a1", localDate: "2026-10-24", localTime: "10:00", location: { id: "L1", name: "Центр" }, service: { id: "s1", name: "Манікюр" },
  specialist: { id: "m1", name: "Марина" }, priceOriginal: 900, discount: 0, priceFinal: 900, cost: 220, masterPercent: 40, masterPayout: 360, net: 320, costMissing: false,
};
const summary = { appointments: 3, completed: 2, cancelled: 1, noShow: 0, revenue: 1800, discountTotal: 0, cost: 220, masterPayout: 360, netIncome: 1220, marginPercent: 67.8, withoutCost: 1, revenueWithoutCost: 900 };

describe("§24.6.3: GET /analytics/network/details", () => {
  it("день: date; тиждень: from+to; locationId, cursor, take (≤ 200); no-store", async () => {
    respond = () => ({ fromDate: "2026-10-24", toDate: "2026-10-24", timezone: "Europe/Kyiv", summary, locations: [], items: [], hasMore: false });
    await httpBeautyApi.getNetworkDetails({ date: "2026-10-24" });
    expect(last().path).toBe("/api/beauty/analytics/network/details?date=2026-10-24");
    expect(last().init).toMatchObject({ cache: "no-store" });
    await httpBeautyApi.getNetworkDetails({ from: "2026-10-26", to: "2026-11-01", locationId: "L1", cursor: "abc", take: 900 });
    expect(last().path).toBe("/api/beauty/analytics/network/details?from=2026-10-26&to=2026-11-01&locationId=L1&cursor=abc&take=200");
  });

  it("мапер лишає тільки поля контракту: ім'я, телефон, clientId клієнта відкидаються", async () => {
    respond = () => ({
      fromDate: "2026-10-24", toDate: "2026-10-24", timezone: "Europe/Kyiv",
      summary: { ...summary, clientName: "Олена" },
      locations: [{ locationId: "L1", name: "Центр", timezone: "Europe/Kyiv", completed: 2, revenue: 1800, cost: 220, masterPayout: 360, netIncome: 1220, marginPercent: 67.8, withoutCost: 1, phone: "+380501112233" }],
      items: [{ ...item, clientId: "c-1", clientName: "Олена Кравченко", phone: "+380501112233", email: "o@x.ua", note: "алергія", cancelReason: "x" }],
      hasMore: true,
      nextCursor: "next",
    });
    const d = await httpBeautyApi.getNetworkDetails({ date: "2026-10-24" });
    const json = JSON.stringify(d);
    expect(json).not.toMatch(/Олена|380501112233|o@x\.ua|алергія|clientId|clientName|cancelReason/);
    expect(d.items[0]).toEqual(item);
    expect(d).toMatchObject({ hasMore: true, nextCursor: "next" });
  });

  it("costMissing: cost/payout = null, навіть якщо сервер віддав числа; без hasMore немає nextCursor", async () => {
    respond = () => ({ fromDate: "a", toDate: "a", timezone: "UTC", summary, locations: [], items: [{ ...item, costMissing: true, cost: 5, masterPayout: 5, masterPercent: 5 }], hasMore: false, nextCursor: "zzz" });
    const d = await httpBeautyApi.getNetworkDetails({ date: "2026-10-24" });
    expect(d.items[0]).toMatchObject({ cost: null, masterPayout: null, masterPercent: null, costMissing: true });
    expect(d.nextCursor).toBeUndefined();
  });
});

describe("§24.6.2: фінансові поля network / series / locations", () => {
  it("проходять лише відомі числові поля; відсутні лишаються відсутніми", async () => {
    respond = (p) =>
      p.includes("/series")
        ? { fromDate: "a", toDate: "b", timezone: "UTC", granularity: "day", points: [{ from: "a", to: "a", appointments: 1, completed: 1, cancelled: 0, noShow: 0, revenue: 100, cost: 10, masterPayout: 20, netIncome: 70, withoutCost: 0, secret: "x" }, { from: "b", to: "b", appointments: 0, completed: 0, cancelled: 0, noShow: 0, revenue: 0 }] }
        : { appointments: 1, completed: 1, cancelled: 0, noShow: 0, revenue: 100, averageCheck: 100, cancellationRate: 0, newClients: 0, cost: 10, masterPayout: 20, netIncome: 70, marginPercent: null, withoutCost: 0, revenueWithoutCost: 0 };
    const s = await httpBeautyApi.getNetworkSeries({ from: "a", to: "b" });
    expect(s.points[0]).toEqual({ from: "a", to: "a", appointments: 1, completed: 1, cancelled: 0, noShow: 0, revenue: 100, cost: 10, masterPayout: 20, netIncome: 70, withoutCost: 0 });
    expect(s.points[1].netIncome).toBeUndefined();
    const n = await httpBeautyApi.getNetworkAnalytics({ from: "2026-10-01", to: "2026-10-02", compare: false });
    expect(n.totals).toMatchObject({ cost: 10, masterPayout: 20, netIncome: 70, marginPercent: null, withoutCost: 0, revenueWithoutCost: 0 });
  });

  it("locations: cost/netIncome/marginPercent/withoutCost у рядку й previous", async () => {
    const m = { appointments: 1, completed: 1, cancelled: 0, revenue: 100, cost: 10, masterPayout: 20, netIncome: 70, marginPercent: 70, withoutCost: 0 };
    respond = () => [{ locationId: "L1", name: "Центр", ...m, previous: { ...m, netIncome: 50, marginPercent: 50 } }];
    const rows = await httpBeautyApi.getLocationsAnalytics({ from: "2026-10-01", to: "2026-10-02", compare: true });
    expect(rows[0]).toMatchObject({ netIncome: 70, marginPercent: 70, withoutCost: 0, previous: { netIncome: 50, marginPercent: 50 } });
  });
});

describe("§24.6.1: собівартість", () => {
  it("GET /service-costs, GET/PUT/DELETE /services/{id}/costs", async () => {
    respond = (p) =>
      p.includes("/service-costs")
        ? { items: [{ serviceId: "s1", serviceName: "Манікюр", isActive: true, network: { costAmount: 220, masterPercent: 40, updatedAt: "2026-10-01T00:00:00Z", updatedBy: { id: "u", name: "Світлана", email: "x@y" } }, locations: [] }] }
        : { costAmount: 100, masterPercent: null, updatedAt: "2026-10-26T00:00:00Z", locationId: "L1" };
    const rows = await httpBeautyApi.getServiceCosts();
    expect(last().path).toBe("/api/beauty/service-costs?includeInactive=false");
    expect(last().init).toMatchObject({ cache: "no-store" });
    expect(rows[0].network).toEqual({ costAmount: 220, masterPercent: 40, updatedAt: "2026-10-01T00:00:00Z", updatedBy: { id: "u", name: "Світлана" } });

    const saved = await httpBeautyApi.setServiceCost("s1", { locationId: "L1", costAmount: 100 });
    expect(last().path).toBe("/api/beauty/services/s1/costs");
    expect(last().init).toMatchObject({ method: "PUT" });
    expect(JSON.parse(last().init!.body!)).toEqual({ locationId: "L1", costAmount: 100, masterPercent: null });
    expect(saved).toMatchObject({ costAmount: 100, locationId: "L1" });

    await httpBeautyApi.deleteServiceCost("s1", "L1");
    expect(last().path).toBe("/api/beauty/services/s1/costs?locationId=L1");
    expect(last().init).toMatchObject({ method: "DELETE" });
    await httpBeautyApi.deleteServiceCost("s1", null);
    expect(last().path).toBe("/api/beauty/services/s1/costs");

    respond = () => ({ serviceId: "s1", locations: [{ locationId: "L1", locationName: "Центр", costAmount: 5, masterPercent: 1, updatedAt: "t" }] });
    expect((await httpBeautyApi.getServiceCostDetail("s1")).locations[0]).toMatchObject({ locationName: "Центр", costAmount: 5 });
    expect(last().path).toBe("/api/beauty/services/s1/costs");
  });
});

describe("§25.7: телефон", () => {
  it("POST /clients/{id}/phone-pending {action}", async () => {
    respond = () => ({ phone: "+380671112233", phoneSource: "telegram_contact", callConsentAt: "2026-10-26T10:00:00Z", phonePending: null, junk: 1 });
    const r = await httpBeautyApi.resolvePhonePending("c1", "accept");
    expect(last().path).toBe("/api/beauty/clients/c1/phone-pending");
    expect(last().init).toMatchObject({ method: "POST" });
    expect(JSON.parse(last().init!.body!)).toEqual({ action: "accept" });
    expect(r).toEqual({ phone: "+380671112233", phoneSource: "telegram_contact", callConsentAt: "2026-10-26T10:00:00Z", phonePending: null });
  });

  it("картка клієнта: джерело, згода, pending, можливий дубль (на верхньому рівні або в client)", async () => {
    respond = () => ({
      client: { id: "c1", fullName: "Олена", phone: "+380501112233", email: null, visits: 1, lastVisitAt: null },
      phoneSource: "manual_chat",
      callConsentAt: "2026-10-20T10:00:00Z",
      phonePending: { phone: "+380671112233", source: "telegram_contact", at: "2026-10-26T09:00:00Z" },
      possibleDuplicate: { id: "c2", name: "Олена К." },
      notes: [],
      history: [],
    });
    const c = (await httpBeautyApi.getClient("c1"))!;
    expect(c.phoneInfo).toEqual({
      phone: "+380501112233",
      phoneSource: "manual_chat",
      callConsentAt: "2026-10-20T10:00:00Z",
      phonePending: { phone: "+380671112233", source: "telegram_contact", at: "2026-10-26T09:00:00Z" },
      possibleDuplicate: { id: "c2", name: "Олена К." },
    });
    respond = () => ({ client: { id: "c1", fullName: "О", phone: null, email: null, visits: 0, lastVisitAt: null, phoneSource: null }, notes: [], history: [] });
    expect((await httpBeautyApi.getClient("c1"))!.phoneInfo).toEqual({ phone: null });
  });

  it("call-tasks: phonePending як рядок або {phone}", async () => {
    const task = (pending: unknown) => ({ id: "t", dueAt: "x", localDueTime: "10:00", status: "open", reason: "reminder", client: { id: "c1", name: "О", phone: "+380501112233", phonePending: pending, junk: 1 }, appointment: {}, location: {} });
    respond = () => ({ items: [task("+380671112233"), task({ phone: "+380991112233", source: "manual_chat" }), task(null)], counts: { open: 3, done: 0, skipped: 0 } });
    const r = await httpBeautyApi.getCallTasks({ day: "today", status: "open" });
    expect(r.items.map((t) => t.client.phonePending)).toEqual(["+380671112233", "+380991112233", undefined]);
    expect(r.items[0].client).not.toHaveProperty("junk");
  });

  it("inbox: у списку й деталях лишається тільки маска (повний номер перемаскується), прапорець pending", async () => {
    const summaryRow = (phoneMasked: string, extra: object = {}) => ({ id: "cv1", channel: {}, title: "x", aiState: "active", lastMessageAt: "t", lastMessagePreview: "p", lastMessageSenderType: "client", unreadCount: 0, client: { id: "c1", name: "О", phoneMasked, phonePending: true, phone: "+380501112233", ...extra } });
    respond = () => ({ items: [summaryRow("+380501112233")], meta: { needsReplyCount: 0 } });
    const list = await httpBeautyApi.getConversations({ filter: "all" });
    expect(list.items[0].client).toEqual({ id: "c1", name: "О", phoneMasked: "+••••••••••33", phonePending: true });
    expect(JSON.stringify(list)).not.toContain("380501112233");

    respond = () => ({ conversation: { ...summaryRow("+••••••••••33"), canReply: true }, messages: [{ id: "m1", direction: "inbound", senderType: "client", kind: "contact", body: "[контакт]", status: "processed", createdAt: "t", contactVerdict: "foreign" }, { id: "m2", direction: "inbound", senderType: "client", kind: "text", body: "x", status: "processed", createdAt: "t", contactVerdict: "own" }], upcomingAppointments: [] });
    const d = await httpBeautyApi.getConversation("cv1");
    expect(d.conversation.client).toMatchObject({ phoneMasked: "+••••••••••33", phonePending: true });
    expect(d.messages[0].contactVerdict).toBe("foreign");
    expect(d.messages[1].contactVerdict).toBeUndefined();
    expect(JSON.stringify(d)).not.toContain("380501112233");
  });
});
