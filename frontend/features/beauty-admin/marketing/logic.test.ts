import { describe, expect, it } from "vitest";
import { BeautyApiError } from "@/features/beauty-auth/errors";
import {
  campaignStatsCsv,
  couponFormErrors,
  dmy,
  fixedLines,
  humanizeMarketingError,
  isMarketingDisabled,
  maskCouponCodes,
  normalizeCode,
  normalizeSegment,
  parseDmy,
  renderTemplate,
  sampleMessage,
  scheduleError,
  serviceList,
  sharedCodeError,
  validateName,
  validateSegment,
  validateTemplate,
} from "./logic";
import type { CampaignStats } from "./types";

const promo = { type: "promo", couponMode: "none" } as const;

describe("§26.7: валідація шаблону (дзеркало invalid_template)", () => {
  it("порожній, задовгий (> 1000 кодових точок) і службові символи", () => {
    expect(validateTemplate("   ", promo)).toMatch(/Введіть текст/);
    expect(validateTemplate("a".repeat(1001), promo)).toMatch(/1000/);
    expect(validateTemplate("a".repeat(1000), promo)).toBeNull();
    expect(validateTemplate("😀".repeat(1000), promo)).toBeNull();
    expect(validateTemplate("a\u0000b", promo)).toMatch(/службові/);
    expect(validateTemplate("a\rb", promo)).toMatch(/службові/);
    expect(validateTemplate("a\nb", promo)).toBeNull();
  });

  it("невідома підстановка; whitelist {name} {discount} {valid_from} {valid_to} {service} {code}", () => {
    expect(validateTemplate("Привіт, {nam}", promo)).toMatch(/Невідома підстановка \{nam\}/);
    expect(validateTemplate("{name} {discount} {service} {valid_from} {valid_to}", promo)).toBeNull();
    expect(validateTemplate("{ name }", promo)).toMatch(/Невідома/);
  });

  it("{code} без купона - помилка; з купоном - дозволено", () => {
    expect(validateTemplate("Код {code}", promo)).toMatch(/потребує купона/);
    expect(validateTemplate("Код {code}", { type: "promo", couponMode: "personal" })).toBeNull();
    expect(validateTemplate("Код {code}", { type: "promo", couponMode: "shared" })).toBeNull();
  });

  it("у новині підстановки акції заборонені", () => {
    for (const p of ["discount", "service", "valid_from", "valid_to"]) {
      expect(validateTemplate(`{${p}}`, { type: "news", couponMode: "none" })).toMatch(/лише в розсилці про акцію/);
    }
    expect(validateTemplate("{name}, новини", { type: "news", couponMode: "none" })).toBeNull();
  });
});

describe("назва, сегмент, розклад, код", () => {
  it("назва 1..200", () => {
    expect(validateName("  ")).not.toBeNull();
    expect(validateName("a".repeat(201))).not.toBeNull();
    expect(validateName("Осінь")).toBeNull();
  });

  it("сегмент: межі параметрів §26.5; зайві параметри відкидаються (CHECK-и)", () => {
    expect(validateSegment({ kind: "lapsed", days: 0 })).not.toBeNull();
    expect(validateSegment({ kind: "lapsed", days: 731 })).not.toBeNull();
    expect(validateSegment({ kind: "lapsed", days: 60 })).toBeNull();
    expect(validateSegment({ kind: "frequent", minVisits: 3 })).not.toBeNull();
    expect(validateSegment({ kind: "frequent", minVisits: 101, periodDays: 90 })).not.toBeNull();
    expect(validateSegment({ kind: "frequent", minVisits: 3, periodDays: 90 })).toBeNull();
    expect(validateSegment({ kind: "new" })).toBeNull();
    expect(normalizeSegment({ kind: "new", days: 5, serviceId: "x", minVisits: 2, periodDays: 3 })).toEqual({ kind: "new" });
    expect(normalizeSegment({ kind: "lapsed", days: 5, minVisits: 2 })).toEqual({ kind: "lapsed", days: 5 });
    expect(normalizeSegment({ kind: "frequent", days: 5, serviceId: "x", minVisits: 2, periodDays: 3 })).toEqual({ kind: "frequent", minVisits: 2, periodDays: 3 });
  });

  it("розклад: до 5 хв у минулому й не далі 30 днів", () => {
    const now = Date.parse("2026-10-26T10:00:00Z");
    expect(scheduleError(undefined, now)).toBeNull();
    expect(scheduleError("2026-10-26T09:57:00Z", now)).toBeNull();
    expect(scheduleError("2026-10-26T09:50:00Z", now)).toMatch(/минув/);
    expect(scheduleError("2026-11-25T10:00:00Z", now)).toBeNull();
    expect(scheduleError("2026-11-26T10:00:00Z", now)).toMatch(/30 днів/);
    expect(scheduleError("не дата", now)).not.toBeNull();
  });

  it("загальний код 4..20 [A-Za-z0-9]; нормалізація як на сервері", () => {
    expect(sharedCodeError("abc")).not.toBeNull();
    expect(sharedCodeError("AUTUMN-15")).not.toBeNull();
    expect(sharedCodeError("a".repeat(21))).not.toBeNull();
    expect(sharedCodeError("AUTUMN15")).toBeNull();
    expect(normalizeCode(" ab-o1 il ")).toBe("AB011" + "1");
  });

  it("форма коду акції: код, ліміт, строк", () => {
    expect(couponFormErrors({ code: "AB", maxUses: "0", from: "", to: "" })).toEqual({
      code: expect.any(String),
      maxUses: expect.any(String),
      period: "Вкажіть строк дії коду.",
    });
    expect(couponFormErrors({ code: "AUTUMN15", maxUses: "", from: "2026-10-01", to: "2026-09-01" }).period).toMatch(/не раніше початку/);
    expect(couponFormErrors({ code: "AUTUMN15", maxUses: "50", from: "2026-10-01", to: "2026-10-31" })).toEqual({});
    expect(parseDmy("07.10.2026")).toBe("2026-10-07");
    expect(parseDmy("31.02.2026")).toBe("");
    expect(parseDmy("abc")).toBe("");
  });
});

describe("§26.10: приклад повідомлення", () => {
  it("підстановки, послуги (≤3 + «та ще N»), відкритий кінець, санітизація", () => {
    expect(serviceList([])).toBe("усі послуги");
    expect(serviceList(["А", "Б", "В"])).toBe("А, Б, В");
    expect(serviceList(["А", "Б", "В", "Г", "Д"])).toBe("А, Б, В та ще 2");
    expect(renderTemplate("{name}: -{discount} на {service} {valid_to}", { name: "Олена", discount: "15%", services: ["Манікюр"] })).toBe("Олена: -15% на Манікюр без обмеження строку");
    expect(renderTemplate("{x} {name}", { name: "Ол\nена" })).toBe("{x} Ол ена");
  });

  it("додає незмінні рядки: умови (лише акція), код (лише з купоном і без {code}), відписка", () => {
    const ctx = { discount: "15%", services: ["Манікюр"], validFrom: "01.10.2026", validTo: "31.10.2026" };
    const withCode = fixedLines({ type: "promo", couponMode: "personal", template: "Привіт" }, ctx);
    expect(withCode).toHaveLength(3);
    expect(withCode[0]).toBe("Умови: знижка 15% на Манікюр, діє з 01.10.2026 по 31.10.2026. Одна знижка на запис, не сумується з іншими акціями.");
    expect(withCode[1]).toMatch(/^Ваш код: /);
    expect(withCode[2]).toBe("Щоб не отримувати акції, напишіть «Стоп» або натисніть кнопку «Не надсилати акції».");
    expect(fixedLines({ type: "promo", couponMode: "personal", template: "Код {code}" }, ctx)).toHaveLength(2);
    expect(fixedLines({ type: "news", couponMode: "none", template: "Новини" }, {})).toHaveLength(1);
    const full = sampleMessage({ type: "promo", couponMode: "shared", template: "{name}, код {code}" }, { ...ctx, code: "AUTUMN15" });
    expect(full.startsWith("Олена, код AUTUMN15\n\nУмови:")).toBe(true);
    expect(full).not.toContain("Ваш код:");
  });

  it("dmy: кінець діапазону виключний (valid_to − 1 с)", () => {
    expect(dmy("2026-10-31T22:00:00Z", "Europe/Kyiv")).toBe("01.11.2026");
    expect(dmy("2026-10-31T22:00:00Z", "Europe/Kyiv", true)).toBe("31.10.2026");
    expect(dmy("не дата")).toBe("");
  });
});

describe("маскування кодів купонів (ПД/коди в DOM)", () => {
  it("лишає останні 3 символи після слова «код» і для формату XXXXX-XXXXX", () => {
    expect(maskCouponCodes("Ваш код: Q7W2K-9XM3D")).toBe("Ваш код: •••••-••M3D");
    expect(maskCouponCodes("напишіть сюди код AUTUMN15 і отримайте")).toBe("напишіть сюди код •••••N15 і отримайте");
    expect(maskCouponCodes("Серед тексту Q7W2K-9XM3D.")).toBe("Серед тексту •••••-••M3D.");
    expect(maskCouponCodes("Код прийнято: 15% на послуги")).toBe("Код прийнято: 15% на послуги");
    expect(maskCouponCodes("Звичайний текст")).toBe("Звичайний текст");
  });
});

describe("помилки без серверних повідомлень", () => {
  const e = (status: number, code: string, retry?: number) => new BeautyApiError(status, code, "SERVER SECRET TEXT", retry);

  it("409/422/429/403 - українські тексти, message сервера не показується", () => {
    for (const [status, code] of [[409, "campaign_changed"], [409, "audience_empty"], [409, "audience_too_large"], [422, "invalid_template"], [422, "invalid_schedule"], [403, "marketing_disabled"], [403, "forbidden_role"], [409, "admin_grant_disabled"], [422, "basis_required"], [409, "promotion_in_campaign"]] as const) {
      const text = humanizeMarketingError(e(status, code));
      expect(text).not.toContain("SERVER SECRET");
      expect(text).toMatch(/[а-яіїєґ]/i);
    }
    expect(humanizeMarketingError(e(429, "rate_limited", 30))).toMatch(/30 с/);
    expect(humanizeMarketingError(e(429, "rate_limited"))).toMatch(/хвилину/);
    expect(humanizeMarketingError(e(409, "idempotency_conflict"))).toMatch(/підтвердження/);
    expect(humanizeMarketingError(e(409, "unknown_x"))).not.toContain("SERVER SECRET");
  });

  it("isMarketingDisabled: marketing_disabled та module_disabled", () => {
    expect(isMarketingDisabled(e(403, "marketing_disabled"))).toBe(true);
    expect(isMarketingDisabled(e(403, "module_disabled"))).toBe(true);
    expect(isMarketingDisabled(e(403, "forbidden_role"))).toBe(false);
    expect(isMarketingDisabled(new Error("x"))).toBe(false);
  });
});

describe("CSV статистики кампанії", () => {
  const stats: CampaignStats = {
    recipients: 85,
    byStatus: { pending: 0, queued: 0, sent: 78, failed: 3, skipped: 4 },
    failed: { botBlocked: 2, chatNotFound: 1, other: 0 },
    skipped: { unsubscribed: 2, frequency: 1, offer_expired: 1 },
    clicks: 27,
    bookings: 11,
    bookingsWithCoupon: 9,
    couponRedemptionsActive: 8,
    revenue: 9450,
    unsubscribedAfter: 2,
    viewed: null,
  };

  it("захист від CSV-injection у назві; BOM; CRLF; без коду купона й GUID", () => {
    const f = campaignStatsCsv({ name: "=HYPERLINK(\"http://x\")", status: "done" }, stats, { issued: 78, redeemedActive: 8, released: 1, revoked: 5 });
    expect(f.content.startsWith("﻿")).toBe(true);
    expect(f.content).toContain("\r\n");
    expect(f.content).toContain("'=HYPERLINK");
    expect(f.content).not.toMatch(/(^|;)=/m);
    expect(f.content).toContain("Переглянуто;недоступно в Telegram");
    expect(f.content).toContain("Пропущено: відписався;2");
    expect(f.content).toContain("Купонів видано;78");
    expect(f.content).not.toMatch(/[0-9a-f]{8}-[0-9a-f]{4}-/);
    for (const name of ["+SUM(1)", "-1+1", "@cmd", "\tx"]) {
      expect(campaignStatsCsv({ name, status: "done" }, stats).content).toContain(`'${name}`);
    }
  });
});
