import { describe, expect, it } from "vitest";
import { canOpen, navFor, safeNext } from "./permissions";

describe("safeNext", () => {
  it("пропускає відносні шляхи сайту з query і hash", () => {
    expect(safeNext("/beauty/calendar")).toBe("/beauty/calendar");
    expect(safeNext("/beauty/staff/1?tab=absence#top")).toBe("/beauty/staff/1?tab=absence#top");
  });
  it("порожнє значення й не-шлях -> fallback", () => {
    expect(safeNext(null)).toBe("/beauty");
    expect(safeNext("")).toBe("/beauty");
    expect(safeNext("beauty")).toBe("/beauty");
    expect(safeNext("https://evil.com", "/x")).toBe("/x");
  });
  it("відсікає protocol-relative і зворотний слеш", () => {
    expect(safeNext("//evil.com")).toBe("/beauty");
    expect(safeNext("/\\evil.com")).toBe("/beauty");
    expect(safeNext("/\\/evil.com")).toBe("/beauty");
  });
  it("відсікає керівні символи (браузер прибирає tab/newline усередині URL)", () => {
    expect(safeNext("/\t/evil.com")).toBe("/beauty");
    expect(safeNext("/\n/evil.com")).toBe("/beauty");
    expect(safeNext("/\r/evil.com")).toBe("/beauty");
    expect(safeNext("/beauty\u0000")).toBe("/beauty");
    expect(safeNext("/beauty\u007f")).toBe("/beauty");
  });
  it("нормалізація, що дає //host, відхиляється", () => {
    expect(safeNext("/..//evil.com")).toBe("/beauty");
    expect(safeNext("/./\\/evil.com")).toBe("/beauty");
  });
});

describe("рольова навігація", () => {
  it("specialist бачить лише календар і власний профіль", () => {
    expect(navFor("specialist").map((n) => n.href)).toEqual(["/beauty/calendar", "/beauty/staff"]);
    expect(navFor("specialist").find((n) => n.href === "/beauty/staff")?.label).toBe("Мій профіль");
    expect(canOpen("specialist", "/beauty/staff/abc")).toBe(true);
    expect(canOpen("specialist", "/beauty/clients")).toBe(false);
  });
  it("owner/admin бачать усе", () => {
    expect(canOpen("owner", "/beauty/analytics")).toBe(true);
    expect(canOpen("admin", "/beauty/channels")).toBe(true);
  });
});
