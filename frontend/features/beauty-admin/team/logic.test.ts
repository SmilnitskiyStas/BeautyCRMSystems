import { describe, expect, it } from "vitest";
import { BeautyApiError } from "@/features/beauty-auth/errors";
import { mockAccessCatalog } from "@/features/beauty-auth/access";
import { accessErrorMessages, draftChanges, findViolations, inviteAssignable, menuScopeNote, optionState } from "./logic";
import type { UserAccess } from "./types";

const base: UserAccess = {
  userId: "u",
  role: "deputy",
  isActive: true,
  specialistId: null,
  accessVersion: 4,
  editable: true,
  notEditableReason: null,
  roleOptions: ["admin", "deputy"],
  granted: { "calendar.view": "all", "calendar.manage": "all", "revenue.view": "none", "team.manage": "none", "ai.settings": "none" },
  effective: {},
  assignable: {
    "calendar.view": ["none", "own", "all"],
    "revenue.view": ["none", "own"],
    "team.manage": [],
    "ai.settings": ["none", "edit"],
    "marketing.campaigns": ["none", "edit"],
  },
  locked: [],
  moduleDisabled: [],
  ownWithoutSpecialist: false,
};

describe("optionState: чому варіант недоступний", () => {
  it("вище за стелю актора", () => {
    expect(optionState(base, "revenue.view", "all", {})).toEqual({ disabled: true, reason: "Вище за ваш власний рівень" });
    expect(optionState(base, "revenue.view", "own", {})).toEqual({ disabled: false });
  });

  it("team.manage закрито для не-адміністратора окремим поясненням", () => {
    expect(optionState(base, "team.manage", "manage", {}).reason).toContain("Адміністратор");
  });

  it("«видано директором» (locked) має пріоритет над «модуль вимкнено»", () => {
    const a = { ...base, locked: ["ai.settings"], moduleDisabled: ["ai.settings"] };
    expect(optionState(a, "ai.settings", "edit", {}).reason).toMatch(/Видано директором/);
  });

  it("вимкнений модуль: підвищити не можна, знизити можна", () => {
    const a = { ...base, granted: { ...base.granted, "ai.settings": "edit" }, moduleDisabled: ["ai.settings"] };
    expect(optionState({ ...base, moduleDisabled: ["ai.settings"] }, "ai.settings", "edit", {}).reason).toMatch(/Модуль вимкнено/);
    expect(optionState(a, "ai.settings", "none", {})).toEqual({ disabled: false });
  });

  it("не редагується: директор, власний рядок, ранг; вибраний варіант лишається без пояснення", () => {
    const owner = { ...base, editable: false, notEditableReason: "owner" as const, granted: {}, effective: { "calendar.view": "all" } };
    expect(optionState(owner, "calendar.view", "own", {}).reason).toMatch(/директора/);
    expect(optionState(owner, "calendar.view", "all", {})).toEqual({ disabled: true });
    expect(optionState({ ...base, editable: false, notEditableReason: "self" }, "calendar.view", "own", {}).reason).toMatch(/власний/);
    expect(optionState({ ...base, editable: false, notEditableReason: "rank" }, "calendar.view", "own", {}).reason).toMatch(/рангу/);
  });
});

describe("чернетка й обмеження", () => {
  it("draftChanges порівнює зі збереженим, ігнорує однакові значення", () => {
    expect(draftChanges(base, { "revenue.view": "own", "calendar.view": "all" })).toEqual([{ permission: "revenue.view", from: "none", to: "own" }]);
  });

  it("findViolations: lte, requires, roleCeiling", () => {
    const c = mockAccessCatalog().constraints;
    expect(findViolations(c, { "calendar.view": "own", "calendar.manage": "all" }, "admin").map((v) => v.permission)).toEqual(["calendar.manage"]);
    expect(findViolations(c, { "revenue.costs": "view", "revenue.view": "own" }, "admin").map((v) => v.permission)).toEqual(["revenue.costs"]);
    expect(findViolations(c, { "team.manage": "manage" }, "deputy").map((v) => v.permission)).toEqual(["team.manage"]);
    expect(findViolations(c, { "team.manage": "manage" }, "admin")).toEqual([]);
  });

  it("scope menu: підпис лише для menuLevels", () => {
    const p = { key: "staff.access", levels: ["own", "all", "edit"], modules: [], scope: "menu" as const, menuLevels: ["own", "all"] };
    expect(menuScopeNote(p, "own")).toMatch(/лише на меню/);
    expect(menuScopeNote(p, "edit")).toBeNull();
  });

  it("inviteAssignable: до власного рівня запрошувача; team.manage лише для admin", () => {
    const lv = (k: string) => (k === "revenue.view" ? "own" : k === "team.manage" ? "manage" : "none");
    expect(inviteAssignable(lv, "deputy")["revenue.view"]).toEqual(["none", "own"]);
    expect(inviteAssignable(lv, "deputy")["team.manage"]).toEqual([]);
    expect(inviteAssignable(lv, "admin")["team.manage"]).toEqual(["none", "manage"]);
  });
});

describe("accessErrorMessages", () => {
  const err = (status: number, code: string, body: Record<string, unknown>) => new BeautyApiError(status, code, code, undefined, undefined, undefined, undefined, undefined, body);

  it("permission_inconsistent: перелік порушень", () => {
    const m = accessErrorMessages(err(422, "permission_inconsistent", { violations: [{ permission: "clients.calls", rule: "requires", other: "clients.view", level: "all" }] }));
    expect(m[0]).toContain("потребує");
  });
  it("permission_ceiling і module_disabled називають права", () => {
    expect(accessErrorMessages(err(403, "permission_ceiling", { permissions: ["revenue.view"] }))[0]).toContain("Виторг");
    expect(accessErrorMessages(err(422, "module_disabled", { permissions: ["ai.settings"] }))[0]).toContain("Налаштування AI");
  });
  it("інакше - загальний текст", () => {
    expect(accessErrorMessages(err(409, "access_version_conflict", { currentVersion: 5 }))[0]).toMatch(/щойно змінили/);
  });
});
