import { describe, expect, it } from "vitest";
import { PERMISSION_KEYS, PERMISSION_LEVELS, ROLE_PRESETS, accessOf, mockAccessCatalog, parsePermissions, toAccessCatalog } from "./access";
import { accessStampStale, toSessionUser } from "./session";
import { canOpen, canSeeFinance, canSeeFullPhone, homeFor, navFor, navGroupsFor } from "./permissions";
import type { SessionUser } from "./types";

const user = (role: SessionUser["role"], permissions?: Record<string, string>, extra: Partial<SessionUser> = {}): SessionUser => ({
  id: "u",
  email: "e",
  fullName: "n",
  role,
  specialistId: role === "specialist" ? "s1" : null,
  permissions,
  ...extra,
});

describe("can(key, level): порядок рівнів з каталогу", () => {
  it("«>=» за індексом: all покриває own, own не покриває all", () => {
    const a = accessOf(user("admin", { "calendar.view": "own", "clients.contacts": "full" }));
    expect(a.can("calendar.view", "none")).toBe(true);
    expect(a.can("calendar.view", "own")).toBe(true);
    expect(a.can("calendar.view", "all")).toBe(false);
    expect(a.can("clients.contacts", "full")).toBe(true);
  });

  it("відсутній ключ у permissions = найнижчий рівень (fail-closed), невідомий рівень = false", () => {
    const a = accessOf(user("owner", { "calendar.view": "all" }));
    expect(a.level("revenue.view")).toBe("none");
    expect(a.can("revenue.view", "all")).toBe(false);
    expect(a.can("clients.contacts", "full")).toBe(false);
    expect(a.level("clients.contacts")).toBe("masked");
    expect(a.can("calendar.view", "superuser")).toBe(false);
  });

  it("невідомий рівень у даних = найнижчий; регістр значущий", () => {
    const a = accessOf(user("admin", { "calendar.view": "ALL", "revenue.view": "bogus" }));
    expect(a.level("calendar.view")).toBe("none");
    expect(a.level("revenue.view")).toBe("none");
  });

  it("permissions з me мають перевагу над пресетом ролі (admin без виторгу)", () => {
    const noRevenue = user("admin", { ...ROLE_PRESETS.admin, "revenue.view": "none" });
    expect(accessOf(noRevenue).can("revenue.view", "all")).toBe(false);
    expect(canSeeFinance(noRevenue)).toBe(false);
    // без permissions (mock/старий backend) - пресет ролі лише для відображення
    expect(canSeeFinance(user("admin"))).toBe(true);
    expect(canSeeFinance("deputy")).toBe(false);
  });

  it("модулі: невідомий перелік = увімкнено; відомий - за списком", () => {
    expect(accessOf(user("owner")).hasModule("beauty_catalog")).toBe(true);
    expect(accessOf(user("owner", undefined, { modules: ["beauty_booking"] })).hasModule("beauty_catalog")).toBe(false);
  });

  it("пресети: рівні кожного ключа існують у каталозі, ролі повні", () => {
    for (const role of ["owner", "admin", "deputy", "specialist"] as const) {
      for (const key of PERMISSION_KEYS) {
        expect(PERMISSION_LEVELS[key] as readonly string[]).toContain(ROLE_PRESETS[role][key]);
      }
    }
    expect(ROLE_PRESETS.deputy["team.manage"]).toBe("none");
    expect(ROLE_PRESETS.specialist["clients.contacts"]).toBe("masked");
  });

  it("mock-каталог збігається зі статичною моделлю", () => {
    const cat = mockAccessCatalog();
    const perms = cat.blocks.flatMap((b) => b.permissions);
    expect(perms.map((p) => p.key).sort()).toEqual([...PERMISSION_KEYS].sort());
    for (const p of perms) expect(p.levels).toEqual([...PERMISSION_LEVELS[p.key as keyof typeof PERMISSION_LEVELS]]);
    expect(cat.blocks.find((b) => b.block === "staff")?.permissions[0]).toMatchObject({ scope: "menu", menuLevels: ["own", "all"] });
  });

  it("toAccessCatalog толерантний до зламаних полів", () => {
    const c = toAccessCatalog({
      catalogVersion: 1,
      blocks: [{ block: "x", permissions: [{ key: "a", levels: ["n", 5], scope: "menu", menuLevels: ["own"] }] }],
      roles: "bad",
    });
    expect(c.roles).toEqual([]);
    expect(c.blocks[0]?.permissions[0]).toEqual({ key: "a", levels: ["n"], modules: [], scope: "menu", menuLevels: ["own"] });
    expect(toAccessCatalog(null).blocks).toEqual([]);
  });
});

describe("me: розбір і синхронізація", () => {
  it("toSessionUser: permissions/stamp/modules; нестрокові значення відкидаються", () => {
    const u = toSessionUser({
      id: "1",
      email: "a@b",
      fullName: "N",
      role: "deputy",
      specialistId: null,
      accessVersion: 7,
      accessStamp: "7.3f9a1c2e",
      catalogVersion: 1,
      modules: ["beauty_booking", 5],
      permissions: { "calendar.view": "all", x: 3 },
    });
    expect(u).toMatchObject({ role: "deputy", accessStamp: "7.3f9a1c2e", accessVersion: 7, modules: ["beauty_booking"] });
    expect(u.permissions).toEqual({ "calendar.view": "all" });
    expect(parsePermissions([])).toBeUndefined();
    expect(toSessionUser({ id: "1", role: "admin" }).permissions).toBeUndefined();
  });

  it("X-Access-Version розходиться зі stamp = застаріло; збіг, відсутній заголовок чи stamp - ні", () => {
    expect(accessStampStale("8.aaaa0000", "7.3f9a1c2e")).toBe(true);
    expect(accessStampStale("7.3f9a1c2e", "7.3f9a1c2e")).toBe(false);
    expect(accessStampStale(null, "7.3f9a1c2e")).toBe(false);
    expect(accessStampStale("7.x", undefined)).toBe(false);
  });
});

describe("гейтинг меню й маршрутів за правами (§30.8)", () => {
  const labels = (u: SessionUser) => navFor(u).map((n) => n.label);

  it("маршрут /beauty/team за team.manage: admin-пресет так, deputy ні", () => {
    expect(canOpen(user("admin", { ...ROLE_PRESETS.admin }), "/beauty/team")).toBe(true);
    expect(canOpen(user("deputy", { ...ROLE_PRESETS.deputy }), "/beauty/team")).toBe(false);
  });

  it("deputy: без аналітики, каналів, розсилок, налаштувань; є клієнти, розмови, ШІ", () => {
    const d = user("deputy", { ...ROLE_PRESETS.deputy });
    const l = labels(d);
    expect(l).toEqual(expect.arrayContaining(["Огляд", "Записи", "Розмови", "Дзвінки", "Клієнти", "Заклади", "Спеціалісти", "Ціни та акції", "AI-асистент"]));
    for (const hidden of ["Аналітика", "Канали", "Розсилки", "Налаштування"]) expect(l).not.toContain(hidden);
    expect(canOpen(d, "/beauty/analytics")).toBe(false);
    expect(canSeeFullPhone(d)).toBe(true);
  });

  it("specialist-пресет: Записи, Мій профіль, Ціни та акції; сторінка закладу так, список ні", () => {
    const s = user("specialist", { ...ROLE_PRESETS.specialist });
    expect(labels(s)).toEqual(["Записи", "Мій профіль", "Ціни та акції"]);
    expect(canOpen(s, "/beauty")).toBe(false);
    expect(canOpen(s, "/beauty/staff/abc")).toBe(true);
    expect(canOpen(s, "/beauty/locations")).toBe(false);
    expect(canOpen(s, "/beauty/locations/abc/")).toBe(true);
    expect(canOpen(s, "/beauty/clients/1")).toBe(false);
    expect(canSeeFullPhone(s)).toBe(false);
  });

  it("гейтинг не залежить від ролі: майстер з виданими правами бачить аналітику й клієнтів", () => {
    const s = user("specialist", { ...ROLE_PRESETS.specialist, "revenue.view": "all", "clients.view": "all", "calendar.view": "all" });
    expect(canOpen(s, "/beauty/analytics")).toBe(true);
    expect(canOpen(s, "/beauty/clients")).toBe(true);
    expect(canOpen(s, "/beauty")).toBe(true);
    expect(homeFor(s)).toBe("/beauty");
  });

  it("«Мій виторг» (revenue.view = own) не відкриває /beauty/analytics", () => {
    expect(canOpen(user("specialist", { ...ROLE_PRESETS.specialist, "revenue.view": "own" }), "/beauty/analytics")).toBe(false);
  });

  it("homeFor: all -> /beauty; own -> календар; без календаря - перший доступний пункт", () => {
    expect(homeFor(user("admin", { ...ROLE_PRESETS.admin }))).toBe("/beauty");
    expect(homeFor(user("specialist", { ...ROLE_PRESETS.specialist }))).toBe("/beauty/calendar");
    const noCalendar = { ...ROLE_PRESETS.specialist, "calendar.view": "none", "calendar.manage": "none", "clients.view": "all" };
    expect(homeFor(user("specialist", noCalendar))).toBe("/beauty/clients");
  });

  it("вимкнені модулі ховають пункти, що не мають рівня none (послуги, спеціалісти, заклади)", () => {
    const u = user("admin", { ...ROLE_PRESETS.admin }, { modules: ["beauty_clients"] });
    const l = labels(u);
    for (const hidden of ["Ціни та акції", "Спеціалісти", "Заклади"]) expect(l).not.toContain(hidden);
    expect(canOpen(u, "/beauty/services")).toBe(false);
    expect(canOpen(u, "/beauty/locations/x")).toBe(false);
  });

  it("налаштування: settings.general >= view або owner; пресет deputy - ні", () => {
    expect(canOpen(user("admin", { ...ROLE_PRESETS.admin }), "/beauty/settings")).toBe(true);
    expect(canOpen(user("deputy", { ...ROLE_PRESETS.deputy }), "/beauty/settings")).toBe(false);
    expect(canOpen(user("owner", {}), "/beauty/settings")).toBe(true);
  });

  it("невідомий /beauty/* закритий; поза /beauty - не наша справа", () => {
    expect(canOpen(user("owner", { ...ROLE_PRESETS.owner }), "/beauty/unknown")).toBe(false);
    expect(canOpen(user("specialist"), "/login")).toBe(true);
  });

  it("групи меню: порожні групи прибираються", () => {
    const g = navGroupsFor(user("specialist", { ...ROLE_PRESETS.specialist }));
    expect(g.map((x) => x.id)).toEqual(["daily", "catalog"]);
  });

  it("«Спеціалісти» замість «Мій профіль» залежить від staff.access, а не від ролі", () => {
    expect(labels(user("specialist", { ...ROLE_PRESETS.specialist, "staff.access": "all" }))).toContain("Спеціалісти");
  });
});
