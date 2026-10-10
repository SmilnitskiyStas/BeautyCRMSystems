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
