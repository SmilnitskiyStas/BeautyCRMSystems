import { describe, expect, it } from "vitest";
import { toSlots } from "./api/http-client";
import { describeCancellation, estimateRefundPercent, hoursLabel } from "./content";
import { dateOptions, todayIn } from "./dates";
import { humanizeBookingError } from "./errors";
import { createKeyHolder } from "./idempotency";
import { appointmentHref, resolveTenant } from "./tenant";
import { BookingApiError } from "./types";

const terms = { windowHours: 12, refundPercentInWindow: 50, refundPercentOutside: 100, deductFee: false, feePercent: 0 };

describe("Idempotency-Key", () => {
  it("повтор того самого тіла використовує той самий ключ, інше тіло - новий", () => {
    let n = 0;
    const h = createKeyHolder(() => `key-${++n}`);
    expect(h.keyFor({ a: 1 })).toBe("key-1");
    expect(h.keyFor({ a: 1 })).toBe("key-1");
    expect(h.keyFor({ a: 2 })).toBe("key-2");
    h.clear();
    expect(h.keyFor({ a: 2 })).toBe("key-3");
  });
});

describe("умови скасування беруться з API", () => {
  it("будує текст із полів", () => {
    expect(describeCancellation(terms)).toEqual([
      "За 12 годин і менше до візиту повертається 50%.",
      "Раніше за цей строк повертається 100%.",
    ]);
    expect(describeCancellation({ ...terms, windowHours: 24, refundPercentInWindow: 0, deductFee: true, feePercent: 10 })).toEqual([
      "За 24 години і менше до візиту повертається 0%.",
      "Раніше за цей строк повертається 100%.",
      "З суми повернення утримується комісія 10%.",
    ]);
  });
  it("без умов - нейтральний текст, без зашитих відсотків", () => {
    expect(describeCancellation(undefined)).toEqual(["Умови скасування уточнюйте в закладі."]);
  });
  it("відмінювання годин", () => {
    expect([1, 2, 5, 11, 21, 22].map(hoursLabel)).toEqual(["1 годину", "2 години", "5 годин", "11 годин", "21 годину", "22 години"]);
  });
  it("орієнтовний % повернення: вікно включне, комісія віднімається", () => {
    const now = Date.parse("2026-10-10T10:00:00Z");
    expect(estimateRefundPercent(terms, "2026-10-10T22:00:00Z", now)).toBe(50);
    expect(estimateRefundPercent(terms, "2026-10-10T22:00:01Z", now)).toBe(100);
    expect(estimateRefundPercent({ ...terms, deductFee: true, feePercent: 10 }, "2026-10-12T10:00:00Z", now)).toBe(90);
  });
});

describe("слоти", () => {
  it("дедуплікує однаковий час різних майстрів і сортує", () => {
    const rows = [
      { specialistId: "b", startsAt: "2026-10-10T12:00:00+03:00", endsAt: "", label: "12:00", cancellation: null },
      { specialistId: "a", startsAt: "2026-10-10T09:00:00+03:00", endsAt: "", label: "09:00", cancellation: terms },
      { specialistId: "a", startsAt: "2026-10-10T12:00:00+03:00", endsAt: "", label: "12:00", cancellation: null },
    ];
    const out = toSlots(rows);
    expect(out.map((s) => s.label)).toEqual(["09:00", "12:00"]);
    expect(out[0].cancellation).toEqual(terms);
  });
});

describe("tenant і посилання", () => {
  it("slug: маршрут важливіший за env; невалідний ігнорується", () => {
    expect(resolveTenant("Salon-1", "env-slug")).toBe("salon-1");
    expect(resolveTenant("x", "env-slug")).toBe("env-slug");
    expect(resolveTenant("a/../b", "")).toBeNull();
  });
  it("токен у шляху; tenant у query лише коли відрізняється від env", () => {
    expect(appointmentHref("env-slug", "tok", "env-slug")).toBe("/book/appointment/tok");
    expect(appointmentHref("other", "tok", "env-slug")).toBe("/book/appointment/tok?tenant=other");
  });
});

describe("дати", () => {
  it("7 днів, починаючи з календарного 'сьогодні' закладу", () => {
    const now = new Date("2026-10-10T22:30:00Z"); // у Києві вже 11 жовтня
    expect(todayIn("Europe/Kyiv", now)).toBe("2026-10-11");
    const d = dateOptions("Europe/Kyiv", 7, now);
    expect(d).toHaveLength(7);
    expect(d[0]).toMatchObject({ iso: "2026-10-11", label: "Сьогодні" });
    expect(d[6].iso).toBe("2026-10-17");
  });
});

describe("помилки людською мовою", () => {
  it("429, 422, 409", () => {
    expect(humanizeBookingError(new BookingApiError(429, "rate_limited"))).toMatch(/Забагато спроб/);
    expect(humanizeBookingError(new BookingApiError(422, "booking_limit_reached"))).toMatch(/забагато записів/);
    expect(humanizeBookingError(new BookingApiError(422, "unknown_code"))).toBe("Перевірте введені дані.");
    expect(humanizeBookingError(new BookingApiError(409, "slot_unavailable"))).toMatch(/зайняли/);
    expect(humanizeBookingError(new BookingApiError(0, "api_unreachable"))).toMatch(/Сервер недоступний/);
  });
});
