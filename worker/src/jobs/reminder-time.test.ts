import { describe, expect, it } from "vitest";
import {
  buildReminderText, formatVisitTime, isQuiet, nextQuietEnd, parseClock, sanitizeLine, STOP_LINE, zonedTimeToUtc,
} from "./reminder-time";

const KYIV = "Europe/Kyiv";
const Q = { startMinutes: parseClock("22:00"), endMinutes: parseClock("08:00") };

describe("formatVisitTime (venue zone, never UTC)", () => {
  it("formatVisitTime_returns-local-weekday-date-time_for-kyiv", () => {
    expect(formatVisitTime(new Date("2026-10-30T12:30:00Z"), KYIV)).toBe("пт, 30.10 о 14:30"); // EET (UTC+2) after the 25.10 switch
  });
  it("formatVisitTime_differs-by-zone_for-the-same-instant", () => {
    const t = new Date("2026-10-31T11:30:00Z");
    expect(formatVisitTime(t, KYIV)).toBe("сб, 31.10 о 13:30");
    expect(formatVisitTime(t, "America/New_York")).toBe("сб, 31.10 о 07:30");
  });
  it("formatVisitTime_follows-dst_summer-and-winter-offsets", () => {
    expect(formatVisitTime(new Date("2026-07-10T11:00:00Z"), KYIV)).toBe("пт, 10.07 о 14:00"); // EEST UTC+3
    expect(formatVisitTime(new Date("2026-01-09T11:00:00Z"), KYIV)).toBe("пт, 09.01 о 13:00"); // EET UTC+2
  });
});

describe("quiet hours 22:00-08:00", () => {
  it("isQuiet_boundaries_22-00-is-quiet_08-00-is-not", () => {
    const at = (iso: string) => isQuiet(new Date(iso), KYIV, Q);
    expect(at("2026-03-10T19:59:00Z")).toBe(false); // 21:59 local
    expect(at("2026-03-10T20:00:00Z")).toBe(true); // 22:00
    expect(at("2026-03-11T05:59:00Z")).toBe(true); // 07:59
    expect(at("2026-03-11T06:00:00Z")).toBe(false); // 08:00
  });
  it("isQuiet_supports-non-wrapping-window", () => {
    const w = { startMinutes: 13 * 60, endMinutes: 15 * 60 };
    expect(isQuiet(new Date("2026-03-10T11:30:00Z"), KYIV, w)).toBe(true); // 13:30
    expect(isQuiet(new Date("2026-03-10T13:00:00Z"), KYIV, w)).toBe(false); // 15:00
  });
  it("nextQuietEnd_after-midnight-is-same-day_before-midnight-is-next-day", () => {
    expect(nextQuietEnd(new Date("2026-03-10T21:10:00Z"), KYIV, 480).toISOString()).toBe("2026-03-11T06:00:00.000Z"); // 23:10 -> 08:00
    expect(nextQuietEnd(new Date("2026-03-10T23:30:00Z"), KYIV, 480).toISOString()).toBe("2026-03-11T06:00:00.000Z"); // 01:30 -> 08:00
  });
  it("nextQuietEnd_across-spring-forward_uses-local-08-00-on-the-new-offset", () => {
    // night 28->29.03.2026, Kyiv switches 03:00 -> 04:00; 08:00 local on the 29th is 05:00Z (UTC+3)
    expect(nextQuietEnd(new Date("2026-03-28T22:30:00Z"), KYIV, 480).toISOString()).toBe("2026-03-29T05:00:00.000Z");
  });
  it("nextQuietEnd_across-fall-back_uses-local-08-00-on-the-new-offset", () => {
    // night 24->25.10.2026, Kyiv switches 04:00 -> 03:00; 08:00 local on the 25th is 06:00Z (UTC+2)
    expect(nextQuietEnd(new Date("2026-10-24T21:30:00Z"), KYIV, 480).toISOString()).toBe("2026-10-25T06:00:00.000Z");
  });
});

describe("zonedTimeToUtc", () => {
  it("regular_time_resolves-with-the-offset-of-that-date", () => {
    expect(zonedTimeToUtc(2026, 7, 10, 14, 0, KYIV).toISOString()).toBe("2026-07-10T11:00:00.000Z");
    expect(zonedTimeToUtc(2026, 1, 9, 14, 0, KYIV).toISOString()).toBe("2026-01-09T12:00:00.000Z");
  });
  it("nonexistent-time-in-the-spring-gap_moves-forward-and-stays-deterministic", () => {
    // 29.03.2026 03:30 Kyiv does not exist (03:00 -> 04:00): becomes 04:30 local = 01:30Z
    expect(zonedTimeToUtc(2026, 3, 29, 3, 30, KYIV).toISOString()).toBe("2026-03-29T01:30:00.000Z");
  });
  it("ambiguous-time-in-the-fall-overlap_resolves-to-one-instant", () => {
    const t = zonedTimeToUtc(2026, 10, 25, 3, 30, KYIV);
    expect(["2026-10-25T00:30:00.000Z", "2026-10-25T01:30:00.000Z"]).toContain(t.toISOString());
  });
});

describe("buildReminderText", () => {
  const base = { service: "Манікюр", startsAt: new Date("2026-10-30T12:30:00Z"), timeZone: KYIV, locationName: "Салон", locationAddress: "вул. Хрещатик, 1" };
  it("contains-local-time_venue_and-stop-line_and-no-utc-marker", () => {
    const text = buildReminderText(base);
    expect(text).toContain("Нагадуємо про ваш візит: Манікюр, пт, 30.10 о 14:30.");
    expect(text).toContain("Заклад: Салон, вул. Хрещатик, 1.");
    expect(text).toContain("Скасувати чи перенести — напишіть сюди.");
    expect(text).toContain(STOP_LINE);
    expect(STOP_LINE).toBe("Щоб не отримувати нагадування, напишіть «Стоп».");
    expect(text).not.toMatch(/\d{4}-\d{2}-\d{2}|T\d{2}:\d{2}|Z\b/);
  });
  it("legacy-option_adds-relative-lead", () => {
    expect(buildReminderText({ ...base, legacyOption: "1h" })).toContain("(через 1 годину)");
    expect(buildReminderText({ ...base, legacyOption: "2h" })).toContain("(через 2 години)");
  });
  it("sanitizes-names_one-line-no-control-characters-max-80", () => {
    const text = buildReminderText({ ...base, service: "A\nB\u0007C" + "x".repeat(200), locationAddress: null });
    expect(text.split("\n")).toHaveLength(4);
    expect(text).not.toContain("\u0007");
    expect(sanitizeLine("a".repeat(200))).toHaveLength(80);
    expect(sanitizeLine("  a \n\t b  ")).toBe("a b");
  });
});
