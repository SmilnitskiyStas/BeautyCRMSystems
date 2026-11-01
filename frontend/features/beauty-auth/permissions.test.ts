import { describe, expect, it } from "vitest";
import { canEditAiSettings, canEditCosts, canSeeFinance, canSeeFullPhone, canEditReminderSettings, canManageAiKey, canManageReminderPrefs, canOpen, canUseCalls, canUseInbox, navFor, NAV_GROUPS, safeNext } from "./permissions";

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
  it("«Заклади» бачать owner і admin, specialist - ні (і не відкриє за URL)", () => {
    expect(navFor("owner").some((n) => n.href === "/beauty/locations" && n.label === "Заклади")).toBe(true);
    expect(navFor("admin").some((n) => n.href === "/beauty/locations")).toBe(true);
    expect(navFor("specialist").some((n) => n.href === "/beauty/locations")).toBe(false);
    expect(canOpen("specialist", "/beauty/locations")).toBe(false);
    expect(canOpen("admin", "/beauty/locations")).toBe(true);
  });
  it("сторінка конкретного закладу відкрита для specialist (перегляд), список і вкладені шляхи - ні", () => {
    expect(canOpen("specialist", "/beauty/locations/abc")).toBe(true);
    expect(canOpen("specialist", "/beauty/locations/abc/")).toBe(true);
    expect(canOpen("specialist", "/beauty/locations/abc/x")).toBe(false);
    expect(canOpen("specialist", "/beauty/locations/")).toBe(false);
  });
  it("owner/admin бачать усе", () => {
    expect(canOpen("owner", "/beauty/analytics")).toBe(true);
    expect(canOpen("admin", "/beauty/channels")).toBe(true);
  });
  it("аналітика (§19) - лише owner/admin; specialist не бачить ні пункт меню, ні сторінку", () => {
    expect(canOpen("admin", "/beauty/analytics")).toBe(true);
    expect(canOpen("specialist", "/beauty/analytics")).toBe(false);
    expect(navFor("specialist").some((n) => n.href === "/beauty/analytics")).toBe(false);
    expect(navFor("owner").some((n) => n.href === "/beauty/analytics")).toBe(true);
  });
});

describe("налаштування ШІ (§20.9)", () => {
  it("ключ - лише owner; решта налаштувань - owner і admin; specialist - нічого", () => {
    expect((["owner", "admin", "specialist"] as const).map((r) => canManageAiKey(r))).toEqual([true, false, false]);
    expect((["owner", "admin", "specialist"] as const).map((r) => canEditAiSettings(r))).toEqual([true, true, false]);
    expect(canOpen("specialist", "/beauty/settings")).toBe(false);
  });
});

describe("розмови, дзвінки, нагадування (§22.10)", () => {
  it("«Розмови» і «Дзвінки» бачать owner/admin; specialist - ні пункту, ні сторінки", () => {
    for (const role of ["owner", "admin"] as const) {
      expect(navFor(role).find((n) => n.href === "/beauty/inbox")?.label).toBe("Розмови");
      expect(navFor(role).find((n) => n.href === "/beauty/calls")?.label).toBe("Дзвінки");
      expect(canOpen(role, "/beauty/inbox")).toBe(true);
      expect(canOpen(role, "/beauty/calls")).toBe(true);
    }
    expect(navFor("specialist").some((n) => n.href === "/beauty/inbox" || n.href === "/beauty/calls")).toBe(false);
    expect(canOpen("specialist", "/beauty/inbox")).toBe(false);
    expect(canOpen("specialist", "/beauty/calls")).toBe(false);
  });
  it("права на inbox / дзвінки / вподобання - owner і admin; налаштування нагадувань змінює лише owner", () => {
    const roles = ["owner", "admin", "specialist"] as const;
    expect(roles.map(canUseInbox)).toEqual([true, true, false]);
    expect(roles.map(canUseCalls)).toEqual([true, true, false]);
    expect(roles.map(canManageReminderPrefs)).toEqual([true, true, false]);
    expect(roles.map(canEditReminderSettings)).toEqual([true, false, false]);
  });
});

describe("§24.9/§25.9: фінанси, собівартість і повний телефон - лише owner/admin", () => {
  it("ролі", () => {
    for (const fn of [canSeeFinance, canEditCosts, canSeeFullPhone]) {
      expect(fn("owner")).toBe(true);
      expect(fn("admin")).toBe(true);
      expect(fn("specialist")).toBe(false);
    }
  });
  it("розділ «Послуги» у меню owner/admin, не в меню specialist; маршрут specialist закритий", () => {
    expect(navFor("owner").find((n) => n.href === "/beauty/services")?.label).toBe("Послуги");
    expect(navFor("admin").some((n) => n.href === "/beauty/services")).toBe(true);
    expect(navFor("specialist").some((n) => n.href === "/beauty/services")).toBe(false);
    expect(canOpen("specialist", "/beauty/services")).toBe(false);
    expect(canOpen("owner", "/beauty/services")).toBe(true);
  });
});

describe("групи меню (TASK-796)", () => {
  it("4 блоки в заданому порядку з тими самими маршрутами", () => {
    expect(NAV_GROUPS.map((g) => g.label)).toEqual(["Щоденна робота", "Каталог і продажі", "Аналітика та AI", "Система"]);
    expect(NAV_GROUPS[0].items.map((n) => n.href)).toEqual(["/beauty", "/beauty/calendar", "/beauty/inbox", "/beauty/calls", "/beauty/clients"]);
    expect(NAV_GROUPS[1].items.map((n) => n.href)).toEqual(["/beauty/staff", "/beauty/locations", "/beauty/services", "/beauty/promos", "/beauty/campaigns"]);
    expect(NAV_GROUPS[2].items.map((n) => n.href)).toEqual(["/beauty/analytics", "/beauty/ai"]);
    expect(NAV_GROUPS[3].items.map((n) => n.href)).toEqual(["/beauty/channels", "/beauty/settings"]);
    expect(navFor("owner")).toHaveLength(14);
  });
});
