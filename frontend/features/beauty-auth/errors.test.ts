import { describe, expect, it } from "vitest";
import { BeautyApiError, humanizeError, readApiError } from "./errors";

describe("помилки §17 українською", () => {
  it.each(["closure_overlap", "location_closed", "has_appointments_on_closed_days"])("%s", (code) => {
    const text = humanizeError(new BeautyApiError(409, code, "english message"));
    expect(text).toMatch(/[а-яіїєґ]/i);
    expect(text).not.toMatch(/english message/);
    expect(text).not.toBe(humanizeError(new BeautyApiError(409, "unknown_code", "x")));
  });
});

describe("readApiError: conflicts[]", () => {
  const res = (body: unknown, status = 409) =>
    new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

  it("розбирає conflicts без зайвих полів і відкидає сміття", async () => {
    const e = await readApiError(
      res({
        code: "has_appointments_on_closed_days",
        message: "m",
        conflicts: [
          { appointmentId: "a1", startsAt: "2026-10-11T08:00:00Z", serviceName: "Манікюр", specialistName: "Марина", clientName: "Не повинно" },
          { nope: true },
          null,
        ],
      }),
    );
    expect(e.code).toBe("has_appointments_on_closed_days");
    expect(e.conflicts).toEqual([{ appointmentId: "a1", startsAt: "2026-10-11T08:00:00Z", serviceName: "Манікюр", specialistName: "Марина" }]);
  });

  it("без conflicts поле відсутнє", async () => {
    const e = await readApiError(res({ code: "closure_overlap", message: "m" }));
    expect(e.conflicts).toBeUndefined();
  });
});

describe("помилки складу етапу 2a (§32.21)", () => {
  it.each([
    ["purpose_locked", 422, /нормах матеріалів/],
    ["unit_locked", 422, /норми матеріалів/],
    ["product_not_for_salon_use", 422, /лише для продажу/],
    ["materials_not_found", 404, /норм немає/],
    ["alert_not_found", 404, /Сповіщення не знайдено/],
    ["invalid_alert_status", 422, /Технічна помилка/],
    ["invalid_alert_kind", 422, /Технічна помилка/],
  ])("%s -> українською, без англійського message", (code, status, re) => {
    expect(humanizeError(new BeautyApiError(status, code, "english message"))).toMatch(re);
  });
});
