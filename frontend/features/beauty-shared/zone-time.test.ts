import { describe, expect, it } from "vitest";
import { addDaysYmd, instantToLocal, localToUtcIso, mondayOfYmd, todayInZone, weekInZone } from "./zone-time";

const KYIV = "Europe/Kyiv";

describe("F2: час запису в зоні закладу (UTC -> локальний)", () => {
  it("зима (UTC+2): 08:00Z = 10:00 за Києвом", () => {
    expect(instantToLocal("2026-10-26T08:00:00+00:00", KYIV)).toBe("2026-10-26T10:00");
    expect(instantToLocal("2026-01-15T08:00:00Z", KYIV)).toBe("2026-01-15T10:00");
  });

  it("літо (UTC+3): 07:00Z = 10:00 за Києвом", () => {
    expect(instantToLocal("2026-07-15T07:00:00+00:00", KYIV)).toBe("2026-07-15T10:00");
  });

  it("межа доби: пізній вечір UTC - уже наступна доба закладу (літо й зима)", () => {
    expect(instantToLocal("2026-10-25T22:30:00Z", KYIV)).toBe("2026-10-26T00:30"); // зима
    expect(instantToLocal("2026-10-24T21:30:00Z", KYIV)).toBe("2026-10-25T00:30"); // літо, ніч переходу
    expect(instantToLocal("2026-07-15T21:00:00Z", KYIV)).toBe("2026-07-16T00:00"); // північ без «24:00»
  });

  it("зсув Z інакше, ніж зона закладу: Нью-Йорк і Окленд", () => {
    expect(instantToLocal("2026-10-26T12:00:00Z", "America/New_York")).toBe("2026-10-26T08:00");
    expect(instantToLocal("2026-10-26T20:00:00Z", "Pacific/Auckland")).toBe("2026-10-27T09:00");
  });

  it("рядок без зсуву вже локальний і не змінюється; невалідна зона не падає", () => {
    expect(instantToLocal("2026-10-26T10:00", KYIV)).toBe("2026-10-26T10:00");
    expect(instantToLocal("2026-10-26T08:00:00Z", "Not/AZone")).toMatch(/^2026-10-26T\d{2}:00$/);
  });
});

describe("F2: перенос - UTC-момент з локального часу закладу", () => {
  it("зима: 14:00 Київ = 12:00Z; літо: 14:00 Київ = 11:00Z", () => {
    expect(localToUtcIso("2026-10-26T14:00:00", KYIV)).toBe("2026-10-26T12:00:00Z");
    expect(localToUtcIso("2026-07-15T14:00", KYIV)).toBe("2026-07-15T11:00:00Z");
  });

  it("межа доби й переходу на зимовий час (25.10.2026 о 04:00 -> 03:00)", () => {
    expect(localToUtcIso("2026-10-26T00:30", KYIV)).toBe("2026-10-25T22:30:00Z");
    expect(localToUtcIso("2026-10-25T00:30", KYIV)).toBe("2026-10-24T21:30:00Z"); // ще літо
    expect(localToUtcIso("2026-10-25T12:00", KYIV)).toBe("2026-10-25T10:00:00Z"); // уже зима
  });

  it("round-trip: показ і перенос дають той самий момент", () => {
    for (const iso of ["2026-03-29T05:30:00Z", "2026-10-25T09:15:00Z", "2026-12-31T21:45:00Z", "2026-06-01T00:00:00Z"]) {
      expect(localToUtcIso(instantToLocal(iso, KYIV), KYIV)).toBe(iso);
    }
  });
});

describe("тиждень і «сьогодні» в зоні закладу", () => {
  it("«сьогодні» Києва вже завтра для UTC", () => {
    const now = new Date("2026-10-25T22:30:00Z");
    expect(todayInZone(KYIV, now)).toBe("2026-10-26");
    expect(todayInZone("UTC", now)).toBe("2026-10-25");
  });

  it("понеділок тижня: неділя 25.10 за UTC, але понеділок 26.10 за Києвом -> інший тиждень", () => {
    const now = new Date("2026-10-25T22:30:00Z");
    expect(weekInZone(KYIV, now).monday).toBe("2026-10-26");
    expect(weekInZone("UTC", now).monday).toBe("2026-10-19");
  });

  it("межі тижня - моменти півночі закладу (зимовий час 26.10-02.11)", () => {
    const w = weekInZone(KYIV, new Date("2026-10-28T10:00:00Z"));
    expect(w.sunday).toBe("2026-11-01");
    expect(w.from.toISOString()).toBe("2026-10-25T22:00:00.000Z");
    expect(w.to.toISOString()).toBe("2026-11-01T22:00:00.000Z");
  });

  it("тиждень із переходом на зимовий час має 7 діб + 1 година", () => {
    const w = weekInZone(KYIV, new Date("2026-10-21T10:00:00Z")); // пн 19.10 .. нд 25.10
    expect(w.monday).toBe("2026-10-19");
    expect((w.to.getTime() - w.from.getTime()) / 3_600_000).toBe(7 * 24 + 1);
  });

  it("допоміжні дати", () => {
    expect(addDaysYmd("2026-10-31", 1)).toBe("2026-11-01");
    expect(mondayOfYmd("2026-10-25")).toBe("2026-10-19");
    expect(mondayOfYmd("2026-10-26")).toBe("2026-10-26");
  });
});
