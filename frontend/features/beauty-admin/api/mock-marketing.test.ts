import { beforeEach, describe, expect, it } from "vitest";
import { BeautyApiError } from "@/features/beauty-auth/errors";
import { CATALOG, DEMO_LOCATIONS, appointmentsSeed } from "../../beauty-shared/demo-seed";
import { CAMPAIGNS_SEED, CLIENT_COUPONS_SEED, CONSENT_SEED, PROMOTIONS_SEED, SHARED_COUPONS_SEED } from "../../beauty-shared/marketing-seed";
import { createMockMarketing, estimateAudience, type MarketingDeps } from "./mock-marketing";
import type { CampaignRequest } from "../types";

const NOW = Date.parse("2026-10-26T10:00:00Z");
let role = "owner";
let enabled = true;
let grant = true;
let clock = NOW;
let override: MarketingDeps["audience"];

function make() {
  return createMockMarketing({
    role: () => role,
    now: () => clock,
    wait: false,
    marketingEnabled: () => enabled,
    adminGrantEnabled: () => grant,
    services: () => CATALOG.map((s) => ({ id: s.id, name: s.name })),
    locations: () => DEMO_LOCATIONS,
    appointments: () => appointmentsSeed(),
    audience: (q) => (override ? override(q) : estimateAudience(q)),
  });
}

const code = async (p: Promise<unknown>) => {
  try {
    await p;
    return null;
  } catch (e) {
    return e instanceof BeautyApiError ? { status: e.status, code: e.code } : e;
  }
};

const publicReq = (over: Partial<CampaignRequest> = {}): CampaignRequest => ({
  name: "Тест", type: "promo", promotionId: "autumn", segment: { kind: "lapsed", days: 60 }, template: "{name}, знижка {discount} до {valid_to}", couponMode: "none", ...over,
});
const codeOnlyReq = (over: Partial<CampaignRequest> = {}): CampaignRequest =>
  publicReq({ promotionId: "vip15", couponMode: "personal", couponValidTo: new Date(NOW + 10 * 86_400_000).toISOString(), template: "{code} {discount}", ...over });

let api: ReturnType<typeof make>;
beforeEach(() => {
  role = "owner";
  enabled = true;
  grant = true;
  clock = NOW;
  override = undefined;
  api = make();
});

describe("mock §26: ролі, модуль, ліміти", () => {
  it("specialist -> 403 forbidden_role на всі розсилкові й купонні методи та згоду", async () => {
    role = "specialist";
    for (const call of [
      () => api.getCampaigns(), () => api.getCampaign("k1"), () => api.createCampaign(publicReq()), () => api.previewAudience({ type: "news", segment: { kind: "new" } }),
      () => api.getCoupons(), () => api.getClientCoupons("oc"), () => api.getMarketingConsent("oc"), () => api.setMarketingConsent("oc", { channel: "telegram", level: "none" }), () => api.getPromotions(),
    ]) {
      expect(await code(call())).toEqual({ status: 403, code: "forbidden_role" });
    }
  });

  it("marketing_disabled: кампанії й купони недоступні, згода працює", async () => {
    enabled = false;
    expect(await code(api.getCampaigns())).toEqual({ status: 403, code: "marketing_disabled" });
    expect(await code(api.getCoupons())).toEqual({ status: 403, code: "marketing_disabled" });
    expect(await code(api.getClientCoupons("oc"))).toEqual({ status: 403, code: "marketing_disabled" });
    await expect(api.getMarketingConsent("oc")).resolves.toBeTruthy();
    await expect(api.setMarketingConsent("oc", { channel: "telegram", level: "none" })).resolves.toBeTruthy();
  });

  it("429 після 30 записів на хвилину і 20 переглядів аудиторії", async () => {
    for (let i = 0; i < 20; i++) await api.previewAudience({ type: "news", segment: { kind: "new" } });
    expect(await code(api.previewAudience({ type: "news", segment: { kind: "new" } }))).toEqual({ status: 429, code: "rate_limited" });
    api = make();
    for (let i = 0; i < 30; i++) await api.setMarketingConsent("oc", { channel: "telegram", level: i % 2 ? "full" : "none", basis: "written", basisNote: "n" });
    expect(await code(api.setMarketingConsent("oc", { channel: "telegram", level: "none" }))).toEqual({ status: 429, code: "rate_limited" });
  });
});

describe("mock §26.5: аудиторія", () => {
  it("recipients + Σ excluded = matched; лише кількості", async () => {
    for (const segment of [{ kind: "lapsed", days: 45 }, { kind: "lapsed", days: 45, serviceId: "mn" }, { kind: "frequent", minVisits: 3, periodDays: 90 }, { kind: "new" }, { kind: "all_consented" }] as const) {
      for (const type of ["promo", "news"] as const) {
        const a = await api.previewAudience({ type, segment, ...(type === "promo" ? { promotionId: "autumn" } : {}), locationId: "c" });
        const sum = Object.values(a.excluded).reduce((x, y) => x + y, 0);
        expect(a.recipients + sum).toBe(a.matched);
        expect(Object.keys(a).sort()).toEqual(["excluded", "matched", "recipients"]);
      }
    }
  });

  it("422 invalid_segment і promotion_not_found", async () => {
    expect(await code(api.previewAudience({ type: "news", segment: { kind: "lapsed", days: 0 } }))).toEqual({ status: 422, code: "invalid_segment" });
    expect(await code(api.previewAudience({ type: "promo", promotionId: "zzz", segment: { kind: "new" } }))).toEqual({ status: 422, code: "promotion_not_found" });
  });
});

describe("mock §26.7: життєвий цикл кампанії", () => {
  it("сід: кампанії в усіх п'яти статусах, список відсортовано й пагінується", async () => {
    const all = await api.getCampaigns();
    expect(new Set(all.items.map((c) => c.status))).toEqual(new Set(["draft", "scheduled", "sending", "done", "cancelled"]));
    expect(all.items).toHaveLength(CAMPAIGNS_SEED.length);
    const p1 = await api.getCampaigns({ take: 2 });
    expect(p1.items).toHaveLength(2);
    const p2 = await api.getCampaigns({ take: 2, cursor: p1.nextCursor });
    expect(p2.items[0].id).not.toBe(p1.items[0].id);
    expect((await api.getCampaigns({ status: "done" })).items.map((c) => c.id)).toEqual(["k4"]);
    expect(await code(api.getCampaigns({ cursor: "-5" }))).toEqual({ status: 422, code: "invalid_cursor" });
  });

  it("валідація чернетки: коди 422", async () => {
    const cases: [Partial<CampaignRequest>, string][] = [
      [{ name: " " }, "invalid_name"],
      [{ promotionId: undefined }, "promotion_required"],
      [{ promotionId: "zzz" }, "promotion_not_found"],
      [{ promotionId: "vip15" }, "promotion_must_be_public"],
      [{ promotionId: "autumn", couponMode: "personal", couponValidTo: new Date(NOW + 1e9).toISOString() }, "promotion_not_code_only"],
      [{ segment: { kind: "lapsed", days: 999 } }, "invalid_segment"],
      [{ segment: { kind: "lapsed", days: 30, serviceId: "nope" } }, "service_not_found"],
      [{ locationId: "nope" }, "location_not_found"],
      [{ template: "{bad}" }, "invalid_template"],
      [{ template: "{code}" }, "invalid_template"],
    ];
    for (const [over, c] of cases) expect(await code(api.createCampaign(publicReq(over)))).toEqual({ status: 422, code: c });
    expect(await code(api.createCampaign(codeOnlyReq({ couponValidTo: undefined })))).toEqual({ status: 422, code: "invalid_coupon_mode" });
    expect(await code(api.createCampaign(codeOnlyReq({ couponMode: "shared", sharedCouponId: "nope" })))).toEqual({ status: 422, code: "coupon_not_found" });
    expect(await code(api.createCampaign(codeOnlyReq({ couponValidTo: new Date(NOW + 400 * 86_400_000).toISOString() })))).toEqual({ status: 422, code: "coupon_period_outside_promotion" });
    expect(await code(api.createCampaign({ ...publicReq(), type: "news", promotionId: undefined, template: "{discount}" }))).toEqual({ status: 422, code: "invalid_template" });
    await expect(api.createCampaign(codeOnlyReq({ couponMode: "shared", sharedCouponId: "cp1", template: "{code}" }))).resolves.toMatchObject({ status: "draft", revision: 1 });
  });

  it("PUT збільшує revision; не-draft -> 409; DELETE лише draft", async () => {
    const c = await api.createCampaign(publicReq());
    const u = await api.updateCampaign(c.id, publicReq({ name: "Нова" }));
    expect(u).toMatchObject({ name: "Нова", revision: 2, status: "draft" });
    expect(await code(api.updateCampaign("k2", publicReq()))).toEqual({ status: 409, code: "campaign_not_draft" });
    expect(await code(api.deleteCampaign("k2"))).toEqual({ status: 409, code: "campaign_not_draft" });
    await api.deleteCampaign(c.id);
    expect(await code(api.getCampaign(c.id))).toEqual({ status: 404, code: "campaign_not_found" });
  });

  it("перегляд: приклад тексту з незмінними рядками, кнопки, ліміти, вікно 09-20, попередження", async () => {
    const c = await api.createCampaign(codeOnlyReq());
    const p = await api.getCampaignPreview(c.id);
    expect(p.revision).toBe(1);
    expect(p.sampleText).toContain("Умови: знижка 15% на усі послуги");
    expect(p.sampleText).toContain("ABCDE-FGHJK");
    expect(p.sampleText).toContain("Щоб не отримувати акції, напишіть «Стоп»");
    expect(p.buttons).toEqual(["🎟 Записатися зі знижкою", "🔕 Не надсилати акції"]);
    expect(p.limits).toMatchObject({ dailyLimit: 200, clientIntervalDays: 7 });
    expect(p.window).toEqual({ from: "09:00", to: "20:00" });
    expect(p.recipients + Object.values(p.excluded).reduce((a, b) => a + b, 0)).toBe(p.matched);
    override = () => ({ matched: 900, recipients: 900, excluded: { noTelegram: 0, unsubscribed: 0, noConsent: 0, levelMismatch: 0, frequency: 0 } });
    const big = await api.getCampaignPreview(c.id);
    expect(big.warnings).toContain("exceeds_daily_limit");
    expect(big.limits.estimatedDays).toBe(5);
  });

  it("підтвердження: revision, Idempotency-Key, розклад, порожня й завелика аудиторія", async () => {
    const c = await api.createCampaign(publicReq());
    const key = "key-12345678";
    expect(await code(api.confirmCampaign(c.id, { revision: 1 }, "short"))).toEqual({ status: 400, code: "idempotency_key_required" });
    expect(await code(api.confirmCampaign(c.id, { revision: 9 }, key))).toEqual({ status: 409, code: "campaign_changed" });
    expect(await code(api.confirmCampaign(c.id, { revision: 1, scheduledAt: new Date(NOW + 40 * 86_400_000).toISOString() }, key))).toEqual({ status: 422, code: "invalid_schedule" });
    override = () => ({ matched: 5, recipients: 0, excluded: { noTelegram: 5, unsubscribed: 0, noConsent: 0, levelMismatch: 0, frequency: 0 } });
    expect(await code(api.confirmCampaign(c.id, { revision: 1 }, key))).toEqual({ status: 409, code: "audience_empty" });
    override = () => ({ matched: 6000, recipients: 5001, excluded: { noTelegram: 999, unsubscribed: 0, noConsent: 0, levelMismatch: 0, frequency: 0 } });
    expect(await code(api.confirmCampaign(c.id, { revision: 1 }, key))).toEqual({ status: 409, code: "audience_too_large" });
    override = undefined;
    const when = new Date(NOW + 3_600_000).toISOString();
    const ok = await api.confirmCampaign(c.id, { revision: 1, scheduledAt: when }, key);
    expect(ok).toMatchObject({ status: "scheduled", scheduledAt: when, confirmedBy: { name: expect.any(String) } });
    expect(ok.recipientsTotal).toBeGreaterThan(0);
    // той самий ключ і тіло - те саме без повторного ефекту; інше тіло - 409
    expect(await api.confirmCampaign(c.id, { revision: 1, scheduledAt: when }, key)).toEqual(ok);
    expect(await code(api.confirmCampaign(c.id, { revision: 1 }, key))).toEqual({ status: 409, code: "idempotency_conflict" });
    expect(await code(api.confirmCampaign(c.id, { revision: 1 }, "another-key-1"))).toEqual({ status: 409, code: "campaign_not_draft" });
    expect((await api.getCampaignStats(c.id)).byStatus.pending).toBe(ok.recipientsTotal);
  });

  it("акція неактивна -> 409 promotion_inactive", async () => {
    const c = await api.createCampaign(publicReq({ promotionId: "summer" }));
    expect(await code(api.confirmCampaign(c.id, { revision: 1 }, "key-12345678"))).toEqual({ status: 409, code: "promotion_inactive" });
  });

  it("скасування: ідемпотентне; pending -> skipped:campaign_cancelled; done -> 409", async () => {
    const before = await api.getCampaignStats("k2");
    expect(before.byStatus.pending).toBe(41);
    const c = await api.cancelCampaign("k2");
    expect(c).toMatchObject({ status: "cancelled", cancelledBy: expect.anything() });
    expect(await api.cancelCampaign("k2")).toMatchObject({ status: "cancelled" });
    const after = await api.getCampaignStats("k2");
    expect(after.byStatus).toMatchObject({ pending: 0, skipped: 41 });
    expect(after.skipped.campaign_cancelled).toBe(41);
    expect(await code(api.cancelCampaign("k4"))).toEqual({ status: 409, code: "campaign_finished" });
    expect((await api.cancelCampaign("k1")).status).toBe("cancelled");
  });

  it("статистика: viewed завжди null; є розбивка помилок і пропусків; підсумок купонів без кодів", async () => {
    const s = await api.getCampaignStats("k4");
    expect(s.viewed).toBeNull();
    expect(s.failed).toEqual({ botBlocked: 2, chatNotFound: 1, other: 0 });
    expect(s.skipped).toMatchObject({ unsubscribed: 2 });
    const sum = await api.getCampaignCouponSummary("k4");
    expect(Object.keys(sum).sort()).toEqual(["issued", "redeemedActive", "released", "revoked"]);
    expect((await api.getCampaignStats("k1")).recipients).toBe(0);
  });

  it("activeCampaigns у акціях рахує scheduled|sending", async () => {
    const promos = await api.getPromotions();
    expect(promos.find((p) => p.id === "vip15")!.activeCampaigns).toBe(1);
    expect(promos.find((p) => p.id === "cl20")!.activeCampaigns).toBe(1);
    expect(promos.find((p) => p.id === "autumn")!.activeCampaigns).toBe(0);
    expect(promos.map((p) => p.access)).toEqual(PROMOTIONS_SEED.map((p) => p.access));
  });
});

describe("mock §26.6/§26.11.4: купони", () => {
  const input = (over = {}) => ({ promotionId: "vip15", code: "NEWCODE1", validFrom: "2026-10-01T00:00:00Z", validTo: "2026-11-01T00:00:00Z", ...over });

  it("створення: валідація кодів і 409 дубль (з нормалізацією); лише code_only", async () => {
    expect(await code(api.createCoupon(input({ code: "ab" })))).toEqual({ status: 422, code: "invalid_code" });
    expect(await code(api.createCoupon(input({ validTo: "2026-09-01T00:00:00Z" })))).toEqual({ status: 422, code: "invalid_period" });
    expect(await code(api.createCoupon(input({ maxUses: 0 })))).toEqual({ status: 422, code: "invalid_max_uses" });
    expect(await code(api.createCoupon(input({ promotionId: "autumn" })))).toEqual({ status: 422, code: "promotion_not_code_only" });
    expect(await code(api.createCoupon(input({ promotionId: "zzz" })))).toEqual({ status: 404, code: "promotion_not_found" });
    expect(await code(api.createCoupon(input({ code: "autumn15" })))).toEqual({ status: 409, code: "coupon_code_taken" });
    const c = await api.createCoupon(input({ code: "newcode1", maxUses: 20 }));
    expect(c).toMatchObject({ code: "NEWCODE1", usesCount: 0, status: "active", maxUses: 20 });
    expect((await api.getCoupons({ promotionId: "vip15" })).length).toBe(SHARED_COUPONS_SEED.length + 1);
  });

  it("відкликання ідемпотентне; перевірка staff: коди відмов і застосування", async () => {
    await api.revokeCoupon("cp1");
    await expect(api.revokeCoupon("cp1")).resolves.toMatchObject({ status: "revoked" });
    expect(await code(api.revokeCoupon("nope"))).toEqual({ status: 404, code: "coupon_not_found" });
    const chk = (c: string) => api.checkCoupon({ code: c, locationId: "c", serviceId: "mn" });
    expect(await code(chk("AUTUMN15"))).toEqual({ status: 409, code: "coupon_revoked" });
    expect(await code(chk("NOPE1234"))).toEqual({ status: 409, code: "coupon_not_found" });
    api = make();
    expect(await chk("autumn-15")).toMatchObject({ outcome: "applied", priceOriginal: 1000, priceFinal: 850 });
  });

  it("персональні купони клієнта: лише codeHint, без повного коду", async () => {
    const list = await api.getClientCoupons("oc");
    expect(list).toHaveLength(1);
    expect(list[0]).toMatchObject({ kind: "personal", codeHint: "M3D" });
    expect(JSON.stringify(list)).not.toMatch(/"code"|hash/i);
    expect(list[0].usedAt).toBeTruthy();
    expect(await code(api.getClientCoupons("nope"))).toEqual({ status: 404, code: "client_not_found" });
    expect(CLIENT_COUPONS_SEED.every((c) => c.codeHint.length === 3)).toBe(true);
  });

  it("запис за купоном: позначка без повного коду; codeHint лише керівникам", () => {
    const appt = appointmentsSeed().find((a) => a.clientId === "oc" && a.status !== "cancelled")!;
    const m = api.couponFor(appt)!;
    expect(m.coupon).toMatchObject({ kind: "personal", outcome: "applied", codeHint: "M3D" });
    role = "specialist";
    const s = api.couponFor(appt)!;
    expect(s.coupon.codeHint).toBeUndefined();
    expect(s.coupon.promotionName).toBeTruthy();
  });
});

describe("mock §26.4: згода", () => {
  it("сід: різні рівні й джерела; журнал новими першими", async () => {
    const lv = async (id: string) => (await api.getMarketingConsent(id)).channels[0];
    expect(await lv("oc")).toMatchObject({ level: "full", state: "set", source: "chat", textVersion: "mc-v1" });
    expect(await lv("kl")).toMatchObject({ level: "selective", source: "admin" });
    expect(await lv("tg")).toMatchObject({ level: "none", source: "stop" });
    expect(await lv("mp")).toMatchObject({ level: "none", source: "chat" });
    expect(await lv("sm")).toMatchObject({ level: "none", state: "unset" });
    const h = (await api.getMarketingConsent("kl")).history;
    expect(h.map((e) => e.levelTo)).toEqual(["selective", "full"]);
    expect(CONSENT_SEED.length).toBe(5);
    expect((await api.getMarketingConsent("mp")).telegramAvailable).toBe(false);
  });

  it("зниження - завжди; підвищення - з підставою й нотаткою та за прапорцем", async () => {
    const set = (level: "full" | "selective" | "none", extra = {}) => api.setMarketingConsent("oc", { channel: "telegram", level, ...extra });
    const down = await set("selective");
    expect(down.channels[0]).toMatchObject({ level: "selective", source: "admin" });
    expect(down.history[0]).toMatchObject({ levelFrom: "full", levelTo: "selective", source: "admin", via: "api" });
    await set("none");
    expect(await code(set("full"))).toEqual({ status: 422, code: "basis_required" });
    expect(await code(set("full", { basis: "x" }))).toEqual({ status: 422, code: "invalid_basis" });
    expect(await code(set("full", { basis: "written" }))).toEqual({ status: 422, code: "invalid_basis_note" });
    expect(await code(set("full", { basis: "written", basisNote: "x".repeat(201) }))).toEqual({ status: 422, code: "invalid_basis_note" });
    grant = false;
    expect(await code(set("full", { basis: "written", basisNote: "Підписана анкета" }))).toEqual({ status: 409, code: "admin_grant_disabled" });
    expect((await api.getMarketingConsent("oc")).adminGrantEnabled).toBe(false);
    grant = true;
    const up = await set("full", { basis: "in_person", basisNote: "Усно на ресепшені" });
    expect(up.channels[0].level).toBe("full");
    expect(up.history[0]).toMatchObject({ levelFrom: "none", levelTo: "full", basis: "in_person", basisNote: "Усно на ресепшені" });
  });

  it("без змін - ідемпотентно (журнал не росте); push і невідомий рівень відхиляються", async () => {
    const n = (await api.getMarketingConsent("oc")).history.length;
    await api.setMarketingConsent("oc", { channel: "telegram", level: "full" });
    expect((await api.getMarketingConsent("oc")).history).toHaveLength(n);
    expect(await code(api.setMarketingConsent("oc", { channel: "push" as never, level: "none" }))).toEqual({ status: 422, code: "channel_unsupported" });
    expect(await code(api.setMarketingConsent("oc", { channel: "telegram", level: "x" as never }))).toEqual({ status: 422, code: "invalid_level" });
    expect(await code(api.setMarketingConsent("nope", { channel: "telegram", level: "none" }))).toEqual({ status: 404, code: "client_not_found" });
  });

  it("basisNote бачить лише owner", async () => {
    await api.setMarketingConsent("sm", { channel: "telegram", level: "selective", basis: "other", basisNote: "Домовились по телефону" });
    expect((await api.getMarketingConsent("sm")).history[0].basisNote).toBe("Домовились по телефону");
    role = "admin";
    const h = (await api.getMarketingConsent("sm")).history[0];
    expect(h.basis).toBe("other");
    expect(h.basisNote).toBeUndefined();
  });
});
