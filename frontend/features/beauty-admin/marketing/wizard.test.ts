import { describe, expect, it } from "vitest";
import {
  CODE_TEMPLATE,
  NEWS_TEMPLATE,
  PROMO_TEMPLATE,
  couponModeFor,
  dateToIso,
  emptyForm,
  formFromDto,
  isoToDate,
  retemplate,
  scheduleFromInput,
  segmentOf,
  toCampaignRequest,
  validateStep,
} from "./wizard";
import type { CampaignDto, PromotionItem, SharedCoupon } from "./types";

const promo = (over: Partial<PromotionItem> = {}): PromotionItem => ({
  id: "p1", name: "Акція", discountType: "percent", discountValue: 15, startsAt: "2026-10-01T00:00:00.000Z", endsAt: "2026-11-30T00:00:00.000Z",
  isActive: true, access: "public", activeCampaigns: 0, locationIds: [], serviceIds: [], ...over,
});

describe("майстер розсилки: запит", () => {
  it("новина: без акції та купона, незалежно від стану форми", () => {
    const f = { ...emptyForm(), name: " Новини ", type: "news" as const, promotionId: "p1", couponMode: "personal" as const, couponTo: "2026-11-01", sharedCouponId: "c1" };
    expect(toCampaignRequest(f)).toEqual({ name: "Новини", type: "news", segment: { kind: "lapsed", days: 60 }, template: PROMO_TEMPLATE, couponMode: "none" });
  });

  it("персональні коди: дати в ISO, кінець - 23:59:59 локально; без sharedCouponId", () => {
    const f = { ...emptyForm(), name: "N", promotionId: "p1", couponMode: "personal" as const, couponFrom: "2026-10-10", couponTo: "2026-10-31", sharedCouponId: "zzz", kind: "new" as const, locationId: "c" };
    const r = toCampaignRequest(f);
    expect(r).toMatchObject({ type: "promo", promotionId: "p1", couponMode: "personal", segment: { kind: "new" }, locationId: "c" });
    expect(r.couponValidFrom).toBe(dateToIso("2026-10-10", false));
    expect(r.couponValidTo).toBe(dateToIso("2026-10-31", true));
    expect(r.sharedCouponId).toBeUndefined();
    expect(isoToDate(r.couponValidTo)).toBe("2026-10-31");
  });

  it("загальний код: лише sharedCouponId", () => {
    const r = toCampaignRequest({ ...emptyForm(), name: "N", promotionId: "p1", couponMode: "shared", sharedCouponId: "c1", couponTo: "2026-10-31" });
    expect(r.sharedCouponId).toBe("c1");
    expect(r.couponValidTo).toBeUndefined();
  });

  it("форма з DTO та назад зберігає значення", () => {
    const dto: CampaignDto = {
      id: "k", name: "N", type: "promo", promotionId: "p1", segment: { kind: "frequent", minVisits: 4, periodDays: 30 }, locationId: "c", template: "T {name}",
      couponMode: "none", status: "draft", revision: 3, createdBy: { id: "u", name: "U" }, createdAt: "2026-10-01T00:00:00Z",
    };
    const f = formFromDto(dto);
    expect(segmentOf(f)).toEqual({ kind: "frequent", minVisits: 4, periodDays: 30 });
    expect(toCampaignRequest(f)).toMatchObject({ name: "N", promotionId: "p1", locationId: "c", template: "T {name}", couponMode: "none" });
  });
});

describe("режим купона й стандартний текст", () => {
  it("публічна акція й новина - без купона; code_only - персональний за замовчуванням", () => {
    expect(couponModeFor("news", undefined, "personal")).toBe("none");
    expect(couponModeFor("promo", promo(), "personal")).toBe("none");
    expect(couponModeFor("promo", promo({ access: "code_only" }), "none")).toBe("personal");
    expect(couponModeFor("promo", promo({ access: "code_only" }), "shared")).toBe("shared");
  });

  it("стандартний текст підміняється, текст менеджера - ні", () => {
    const base = emptyForm();
    expect(retemplate(base, { ...base, couponMode: "personal" }).template).toBe(CODE_TEMPLATE);
    expect(retemplate(base, { ...base, type: "news" }).template).toBe(NEWS_TEMPLATE);
    const own = { ...base, template: "Мій текст {name}" };
    expect(retemplate(own, { ...own, couponMode: "personal" }).template).toBe("Мій текст {name}");
    expect(retemplate({ ...base, template: "" }, { ...base, template: "", type: "news" }).template).toBe(NEWS_TEMPLATE);
  });
});

describe("перевірка кроків", () => {
  const f = { ...emptyForm(), name: "N", promotionId: "p1" };

  it("крок 1: назва, акція для promo, активність", () => {
    expect(validateStep(1, { ...f, name: "" }, promo(), undefined)).toMatch(/назву/);
    expect(validateStep(1, { ...f, promotionId: "" }, undefined, undefined)).toMatch(/Оберіть акцію/);
    expect(validateStep(1, f, promo({ isActive: false }), undefined)).toMatch(/вимкнена/);
    expect(validateStep(1, f, promo(), undefined)).toBeNull();
    expect(validateStep(1, { ...f, type: "news", promotionId: "" }, undefined, undefined)).toBeNull();
  });

  it("крок 2: параметри сегмента", () => {
    expect(validateStep(2, { ...f, days: "" }, promo(), undefined)).toMatch(/днів/);
    expect(validateStep(2, { ...f, days: "800" }, promo(), undefined)).not.toBeNull();
    expect(validateStep(2, f, promo(), undefined)).toBeNull();
  });

  it("крок 3: шаблон; {code} без купона відхиляється", () => {
    expect(validateStep(3, { ...f, template: "{code}" }, promo(), undefined)).toMatch(/купона/);
    expect(validateStep(3, { ...f, template: "{code}", couponMode: "personal" }, promo({ access: "code_only" }), undefined)).toBeNull();
  });

  it("крок 4: персональні - кінець у межах акції; загальні - активний код", () => {
    const p = promo({ access: "code_only" });
    const personal = { ...f, couponMode: "personal" as const };
    expect(validateStep(4, personal, p, undefined)).toMatch(/до якої дати/);
    expect(validateStep(4, { ...personal, couponTo: "2027-01-01" }, p, undefined)).toMatch(/довше за акцію/);
    expect(validateStep(4, { ...personal, couponFrom: "2026-09-01", couponTo: "2026-10-31" }, p, undefined)).toMatch(/раніше за початок/);
    expect(validateStep(4, { ...personal, couponFrom: "2026-10-05", couponTo: "2026-10-31" }, p, undefined)).toBeNull();
    const shared = { ...f, couponMode: "shared" as const, sharedCouponId: "c1" };
    const coupon = (status: "active" | "revoked"): SharedCoupon => ({ id: "c1", kind: "shared", promotionId: "p1", code: "AUTUMN15", validFrom: "", validTo: "", usesCount: 0, status });
    expect(validateStep(4, { ...shared, sharedCouponId: "" }, p, [coupon("active")])).toMatch(/Оберіть загальний код/);
    expect(validateStep(4, shared, p, [coupon("revoked")])).toMatch(/відкликано/);
    expect(validateStep(4, shared, p, [coupon("active")])).toBeNull();
  });

  it("розклад: зараз - без помилки; пізніше - лише валідна дата", () => {
    const now = Date.parse("2026-10-26T10:00:00Z");
    expect(scheduleFromInput("now", "", now)).toEqual({ error: null });
    expect(scheduleFromInput("later", "", now).error).toMatch(/Вкажіть/);
    const ok = scheduleFromInput("later", "2026-10-27T12:00", now);
    expect(ok.error).toBeNull();
    expect(ok.iso).toBe(new Date("2026-10-27T12:00").toISOString());
    expect(scheduleFromInput("later", "2026-01-01T12:00", now).error).toMatch(/минув/);
  });
});
