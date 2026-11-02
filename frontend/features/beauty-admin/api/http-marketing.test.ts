import { beforeEach, describe, expect, it, vi } from "vitest";

type Init = { method?: string; body?: string; cache?: string; headers?: Record<string, string> };
const apiJson = vi.fn<(path: string, init?: Init) => Promise<unknown>>();
vi.mock("@/features/beauty-auth/session", () => ({
  apiJson: (path: string, init?: Init) => apiJson(path, init),
  getUser: () => ({ id: "u", role: "owner", specialistId: null }),
}));

import { httpBeautyApi } from "./http-client";

beforeEach(() => {
  apiJson.mockReset();
  apiJson.mockResolvedValue(undefined);
});
const last = () => apiJson.mock.calls[apiJson.mock.calls.length - 1];

describe("http §26: шляхи, методи, заголовки", () => {
  it("список кампаній: статус, курсор, take ≤ 50, no-store", async () => {
    apiJson.mockResolvedValueOnce({ items: [{ id: "k", name: "N", type: "promo", status: "done", sent: 3, createdAt: "x", secret: "y" }], nextCursor: "50" });
    const r = await httpBeautyApi.getCampaigns({ status: "done", cursor: "5", take: 500 });
    expect(last()[0]).toBe("/api/beauty/campaigns?status=done&cursor=5&take=50");
    expect(last()[1]).toMatchObject({ cache: "no-store" });
    expect(r).toEqual({ items: [{ id: "k", name: "N", type: "promo", status: "done", sent: 3, createdAt: "x" }], nextCursor: "50" });
  });

  it("підтвердження: POST з Idempotency-Key і {revision, scheduledAt}", async () => {
    apiJson.mockResolvedValueOnce({ id: "k1", status: "scheduled", revision: 2 });
    await httpBeautyApi.confirmCampaign("k1", { revision: 2, scheduledAt: "2026-10-27T10:00:00Z" }, "key-12345678");
    expect(last()[0]).toBe("/api/beauty/campaigns/k1/confirm");
    expect(last()[1]).toMatchObject({ method: "POST", headers: { "Idempotency-Key": "key-12345678" } });
    expect(JSON.parse(last()[1]!.body!)).toEqual({ revision: 2, scheduledAt: "2026-10-27T10:00:00Z" });
    await httpBeautyApi.confirmCampaign("k1", { revision: 2 }, "key-12345678");
    expect(JSON.parse(last()[1]!.body!)).toEqual({ revision: 2 });
  });

  it("CRUD кампаній, скасування, перегляд аудиторії, DELETE", async () => {
    await httpBeautyApi.createCampaign({ name: "N", type: "news", segment: { kind: "new" }, template: "t", couponMode: "none" });
    expect(last()).toSatisfy((c: unknown[]) => c[0] === "/api/beauty/campaigns" && (c[1] as Init).method === "POST");
    await httpBeautyApi.updateCampaign("a b", { name: "N", type: "news", segment: { kind: "new" }, template: "t", couponMode: "none" });
    expect(last()[0]).toBe("/api/beauty/campaigns/a%20b");
    expect(last()[1]).toMatchObject({ method: "PUT" });
    await httpBeautyApi.cancelCampaign("k1");
    expect(last()[0]).toBe("/api/beauty/campaigns/k1/cancel");
    await httpBeautyApi.deleteCampaign("k1");
    expect(last()[1]).toMatchObject({ method: "DELETE" });
    await httpBeautyApi.previewAudience({ type: "news", segment: { kind: "new" } });
    expect(last()[0]).toBe("/api/beauty/marketing/audience/preview");
  });

  it("статистика: viewed завжди null, skipped лише з безпечними ключами", async () => {
    apiJson.mockResolvedValueOnce({ recipients: 5, byStatus: { sent: 2 }, skipped: { frequency: 1, "<b>": 3 }, viewed: 99, clicks: 1 });
    const s = await httpBeautyApi.getCampaignStats("k");
    expect(s.viewed).toBeNull();
    expect(s.skipped).toEqual({ frequency: 1 });
    expect(s.byStatus.sent).toBe(2);
  });

  it("персональні купони клієнта: повний код ніколи не потрапляє в UI", async () => {
    apiJson.mockResolvedValueOnce([{ id: "p", kind: "personal", codeHint: "XYZW", code: "ABCDE-FGHJK", codeHash: "h", promotion: { id: "a", name: "A" }, validFrom: "a", validTo: "b", status: "active" }]);
    const r = await httpBeautyApi.getClientCoupons("c1");
    expect(last()[0]).toBe("/api/beauty/clients/c1/coupons");
    expect(r[0].codeHint).toBe("YZW");
    expect(JSON.stringify(r)).not.toContain("ABCDE");
    expect(JSON.stringify(r)).not.toContain("codeHash");
  });

  it("загальні коди: GET kind=shared; перевірка - POST /coupons/check; відкликання PATCH", async () => {
    await httpBeautyApi.getCoupons({ promotionId: "p1" });
    expect(last()[0]).toBe("/api/beauty/coupons?kind=shared&promotionId=p1");
    await httpBeautyApi.checkCoupon({ code: "AB12", locationId: "c", serviceId: "mn" });
    expect(last()[0]).toBe("/api/beauty/coupons/check");
    await httpBeautyApi.revokeCoupon("cp1");
    expect(last()[1]).toMatchObject({ method: "PATCH" });
    expect(JSON.parse(last()[1]!.body!)).toEqual({ status: "revoked" });
  });

  it("згода: PUT лише з channel/level; підстава - тільки при підвищенні; маппер відкидає зайве", async () => {
    apiJson.mockResolvedValueOnce({ channels: [{ channel: "telegram", level: "full", state: "set", source: "chat", extra: 1 }], telegramAvailable: true, history: [{ at: "t", levelTo: "full", source: "chat", via: "button", chatId: 123 }] });
    const c = await httpBeautyApi.setMarketingConsent("c1", { channel: "telegram", level: "none" });
    expect(last()[0]).toBe("/api/beauty/clients/c1/marketing-consent");
    expect(JSON.parse(last()[1]!.body!)).toEqual({ channel: "telegram", level: "none" });
    expect(JSON.stringify(c)).not.toContain("chatId");
    expect(c.history[0]).toMatchObject({ levelTo: "full", via: "button" });
    await httpBeautyApi.setMarketingConsent("c1", { channel: "telegram", level: "full", basis: "written", basisNote: "n" });
    expect(JSON.parse(last()[1]!.body!)).toEqual({ channel: "telegram", level: "full", basis: "written", basisNote: "n" });
  });

  it("акція: access у POST /promotions; запис: coupon без повного коду; тіло розмови з кодом маскується", async () => {
    apiJson.mockResolvedValueOnce({ id: "p9" });
    await httpBeautyApi.createPromotion({ name: "N", percent: 10, locationIds: [], serviceIds: [], from: "01.10.2026", to: "02.10.2026", access: "code_only" });
    expect(JSON.parse(last()[1]!.body!).access).toBe("code_only");
    apiJson.mockResolvedValueOnce({
      conversation: { id: "c", channel: { id: "1", type: "telegram", name: "t" }, aiState: "active", title: "T", lastMessageAt: "x", lastMessagePreview: "", lastMessageSenderType: "system", unreadCount: 0, canReply: true },
      messages: [{ id: "m", direction: "outbound", senderType: "system", kind: "text", body: "Ваш код: Q7W2K-9XM3D", status: "sent", createdAt: "t" }],
      upcomingAppointments: [],
    });
    const d = await httpBeautyApi.getConversation("c");
    expect(d.messages[0].body).toBe("Ваш код: •••••-••M3D");
  });
});
