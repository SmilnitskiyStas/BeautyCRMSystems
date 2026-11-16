import { PERMISSION_LEVELS, ROLE_PRESETS, levelIndex, mockAccessCatalog } from "@/features/beauty-auth/access";
import { BeautyApiError } from "@/features/beauty-auth/errors";
import type { Role } from "@/features/beauty-auth/types";
import type { BeautyAdminApi } from "./client";
import type {
  AccessChange,
  AccessClamp,
  AccessSaveResult,
  AuditItem,
  MyRevenue,
  PermissionValues,
  TeamInvite,
  TeamUser,
  UserAccess,
} from "../team/types";

/**
 * Демо-режим «Команда й доступи» (§30.9, §30.10) без backend: ті самі коди помилок і порядок перевірок.
 * Роль актора - `NEXT_PUBLIC_MOCK_ROLE`. Стан живе в межах сесії браузера.
 */
const RANK: Record<string, number> = { owner: 4, admin: 3, deputy: 2, specialist: 1 };
const LEVELS = PERMISSION_LEVELS as Record<string, readonly string[]>;
const KEYS = Object.keys(LEVELS);

function fail(status: number, code: string, body: Record<string, unknown> = {}): never {
  throw new BeautyApiError(status, code, code, undefined, undefined, undefined, undefined, typeof body.permission === "string" ? body.permission : undefined, { code, ...body });
}

interface MockUser {
  id: string;
  email: string;
  fullName: string;
  role: Role;
  specialistId: string | null;
  isActive: boolean;
  version: number;
  /** Директор: рядків немає (`granted = {}`), права максимальні неявно. */
  granted: PermissionValues;
}

const preset = (role: Role): PermissionValues => ({ ...ROLE_PRESETS[role] });
const maxLevels = (): PermissionValues => Object.fromEntries(KEYS.map((k) => [k, LEVELS[k]![LEVELS[k]!.length - 1]!]));

export function createMockTeam(opts: { role: () => string; wait: <T>(v: T) => Promise<T>; today: () => string }) {
  const users: MockUser[] = [
    { id: "u-owner", email: "owner@salon.test", fullName: "Олена Директорова", role: "owner", specialistId: null, isActive: true, version: 1, granted: {} },
    { id: "u-admin", email: "admin@salon.test", fullName: "Ірина Адміністраторська", role: "admin", specialistId: null, isActive: true, version: 3, granted: preset("admin") },
    { id: "u-deputy", email: "deputy@salon.test", fullName: "Петро Заступник", role: "deputy", specialistId: null, isActive: true, version: 2, granted: preset("deputy") },
    { id: "u-spec", email: "marina@salon.test", fullName: "Марина Майстер", role: "specialist", specialistId: "m", isActive: true, version: 1, granted: preset("specialist") },
    { id: "u-spec2", email: "sofia@salon.test", fullName: "Софія Майстер", role: "specialist", specialistId: null, isActive: false, version: 2, granted: preset("specialist") },
  ];
  const invites: TeamInvite[] = [];
  const audit: AuditItem[] = [
    { id: "a0", createdAt: new Date(Date.now() - 86_400_000).toISOString(), action: "permissions_changed", actor: { id: "u-owner", fullName: "Олена Директорова" }, target: { id: "u-admin", fullName: "Ірина Адміністраторська" }, inviteId: null, changes: [{ permission: "revenue.costs", from: "view", to: "edit" }] },
  ];

  const actorOf = (): MockUser => {
    const role = opts.role() as Role;
    return users.find((u) => u.role === role) ?? users[0]!;
  };
  /** Ефективні права актора (директор - максимальні). */
  const actorLevels = (a: MockUser): PermissionValues => (a.role === "owner" ? maxLevels() : a.granted);
  const lvl = (v: PermissionValues, k: string) => v[k] ?? LEVELS[k]![0]!;
  const guard = (): MockUser => {
    const a = actorOf();
    if (!(a.role === "owner" || lvl(a.granted, "team.manage") === "manage")) {
      fail(403, "forbidden_role", { permission: "team.manage", required: "manage" });
    }
    return a;
  };
  const find = (id: string) => users.find((u) => u.id === id) ?? fail(404, "user_not_found");

  function accessOf(a: MockUser, t: MockUser): UserAccess {
    const reason = t.role === "owner" ? "owner" : t.id === a.id ? "self" : RANK[a.role]! <= RANK[t.role]! ? "rank" : null;
    const mine = actorLevels(a);
    const assignable: Record<string, string[]> = {};
    const locked: string[] = [];
    for (const k of KEYS) {
      const cap = levelIndex(k, lvl(mine, k));
      const cur = levelIndex(k, lvl(t.granted, k));
      if (reason) assignable[k] = [];
      else if (cur > cap) {
        locked.push(k);
        assignable[k] = [];
      } else assignable[k] = k === "team.manage" && t.role !== "admin" ? [] : LEVELS[k]!.filter((_, i) => i <= cap);
    }
    const roleOptions: Role[] = !reason && (t.role === "admin" || t.role === "deputy") ? (["admin", "deputy"] as Role[]).filter((r) => RANK[r]! < RANK[a.role]!) : [];
    return {
      userId: t.id,
      role: t.role,
      isActive: t.isActive,
      specialistId: t.specialistId,
      accessVersion: t.version,
      editable: reason === null,
      notEditableReason: reason,
      roleOptions,
      granted: t.role === "owner" ? {} : { ...t.granted },
      effective: t.role === "owner" ? maxLevels() : { ...t.granted },
      assignable,
      locked,
      moduleDisabled: [],
      ownWithoutSpecialist: KEYS.some((k) => lvl(t.granted, k) === "own" && !k.startsWith("staff") && !k.startsWith("locations")) && t.specialistId === null && t.role === "specialist",
    };
  }

  /** Обмеження §30.2 на повному наборі (структурований вигляд для 422 `permission_inconsistent`). */
  function violations(values: PermissionValues, role: Role) {
    const out: { permission: string; rule: string; other?: string; level?: string }[] = [];
    for (const c of mockAccessCatalog().constraints) {
      const v = levelIndex(c.key, lvl(values, c.key));
      if (c.type === "lte" && c.other && v > levelIndex(c.other, lvl(values, c.other))) out.push({ permission: c.key, rule: "lte", other: c.other });
      if (c.type === "requires" && c.other && c.level && v > 0 && levelIndex(c.other, lvl(values, c.other)) < levelIndex(c.other, c.level))
        out.push({ permission: c.key, rule: "requires", other: c.other, level: c.level });
      if (c.type === "roleCeiling" && v > 0 && !(c.roles ?? []).includes(role)) out.push({ permission: c.key, rule: "roleCeiling" });
    }
    return out;
  }

  function note(action: string, a: MockUser, t: MockUser | null, changes: AccessChange[] = [], inviteId: string | null = null) {
    audit.unshift({
      id: `a${audit.length + 1}`,
      createdAt: new Date().toISOString(),
      action,
      actor: { id: a.id, fullName: a.fullName },
      target: t ? { id: t.id, fullName: t.fullName } : null,
      inviteId,
      changes,
    });
  }

  function apply(a: MockUser, t: MockUser, input: { expectedVersion: number; role?: Role; permissions?: PermissionValues; dryRun?: boolean; reset?: boolean }): AccessSaveResult {
    if (t.id === a.id) fail(422, "cannot_modify_self");
    if (t.role === "owner" || RANK[a.role]! <= RANK[t.role]!) fail(403, "forbidden_role");
    if (!Number.isInteger(input.expectedVersion) || input.expectedVersion < 1) fail(422, "expected_version_required");
    if (input.expectedVersion !== t.version) fail(409, "access_version_conflict", { currentVersion: t.version });
    let role = t.role;
    if (input.role !== undefined) {
      if (!["admin", "deputy", "specialist"].includes(input.role)) fail(422, "invalid_role");
      if (RANK[input.role]! >= RANK[a.role]!) fail(403, "forbidden_role");
      if (!((t.role === "admin" || t.role === "deputy") && (input.role === "admin" || input.role === "deputy"))) fail(422, "role_change_not_supported");
      role = input.role;
    }
    const mine = actorLevels(a);
    const before = accessOf(a, t);
    const asked = input.permissions ?? {};
    for (const [k, v] of Object.entries(asked)) {
      if (!LEVELS[k]) fail(422, "invalid_permission", { permission: k });
      if (!LEVELS[k]!.includes(v)) fail(422, "invalid_level", { permission: k, level: v });
    }
    const over = Object.entries(asked).filter(([k, v]) => before.locked.includes(k) || levelIndex(k, v) > levelIndex(k, lvl(mine, k)));
    if (over.length) fail(403, "permission_ceiling", { permissions: over.map(([k]) => k) });
    for (const [k, v] of Object.entries(asked)) if (k === "team.manage" && v !== "none" && role !== "admin") fail(422, "permission_not_allowed_for_role", { permission: k, role });

    const clamped: AccessClamp[] = [];
    let next: PermissionValues = { ...t.granted };
    if (input.reset || input.role !== undefined) {
      const base = preset(role);
      next = {};
      for (const k of KEYS) {
        const want = lvl(base, k);
        if (before.locked.includes(k)) {
          next[k] = lvl(t.granted, k);
          if (want !== next[k]) clamped.push({ permission: k, requested: want, applied: next[k]!, reason: "locked" });
        } else if (levelIndex(k, want) > levelIndex(k, lvl(mine, k))) {
          next[k] = lvl(mine, k);
          clamped.push({ permission: k, requested: want, applied: next[k]!, reason: "ceiling" });
        } else next[k] = want;
      }
    }
    Object.assign(next, asked);
    const bad = violations(next, role);
    if (bad.length) fail(422, "permission_inconsistent", { violations: bad });

    const changes: AccessChange[] = KEYS.filter((k) => lvl(next, k) !== lvl(t.granted, k)).map((k) => ({ permission: k, from: lvl(t.granted, k), to: lvl(next, k) }));
    const roleChange = role !== t.role ? { from: t.role, to: role } : null;
    if (!input.dryRun && (changes.length > 0 || roleChange)) {
      t.role = role;
      t.granted = next;
      t.version += 1;
      note(input.reset ? "preset_reset" : roleChange ? "role_changed" : "permissions_changed", a, t, changes);
    }
    return { ...accessOf(a, t), changes, roleChange, clamped };
  }

  const api: Pick<
    BeautyAdminApi,
    "getTeamUsers" | "getTeamInvites" | "getUserAccess" | "saveUserAccess" | "resetUserAccess" | "setUserActive" | "createTeamInvite" | "getAccessAudit" | "getMyRevenue"
  > = {
    getTeamUsers: () => {
      const a = guard();
      return opts.wait(
        users.map<TeamUser>((u) => ({
          id: u.id, email: u.email, fullName: u.fullName, role: u.role, specialistId: u.specialistId, isActive: u.isActive,
          lastLoginAt: u.isActive ? new Date(Date.now() - 3_600_000).toISOString() : null,
          accessVersion: u.version,
          manageable: u.id !== a.id && u.role !== "owner" && RANK[a.role]! > RANK[u.role]!,
        })),
      );
    },
    getTeamInvites: () => {
      guard();
      return opts.wait(invites.filter((i) => i.status === "pending"));
    },
    getUserAccess: (id) => {
      const a = guard();
      return opts.wait(accessOf(a, find(id)));
    },
    saveUserAccess: (id, input) => {
      const a = guard();
      const t = find(id);
      try {
        return opts.wait(apply(a, t, input));
      } catch (e) {
        return Promise.reject(e);
      }
    },
    resetUserAccess: (id, input) => {
      const a = guard();
      const t = find(id);
      try {
        return opts.wait(apply(a, t, { ...input, reset: true }));
      } catch (e) {
        return Promise.reject(e);
      }
    },
    setUserActive: (id, isActive, expectedVersion) => {
      const a = guard();
      const t = find(id);
      try {
        if (t.id === a.id) fail(422, "cannot_modify_self");
        if (t.role === "owner" || RANK[a.role]! <= RANK[t.role]!) fail(403, "forbidden_role");
        if (expectedVersion !== undefined && expectedVersion !== t.version) fail(409, "access_version_conflict", { currentVersion: t.version });
        if (t.isActive !== isActive) {
          t.isActive = isActive;
          t.version += 1;
          note(isActive ? "activated" : "deactivated", a, t);
        }
        return opts.wait(undefined);
      } catch (e) {
        return Promise.reject(e);
      }
    },
    createTeamInvite: (input) => {
      const a = guard();
      try {
        if (!["admin", "deputy", "specialist"].includes(input.role)) fail(422, "invalid_role");
        if (RANK[input.role]! >= RANK[a.role]!) fail(403, "forbidden_role");
        const asked = input.permissions ?? {};
        for (const [k, v] of Object.entries(asked)) {
          if (!LEVELS[k]) fail(422, "invalid_permission", { permission: k });
          if (!LEVELS[k]!.includes(v)) fail(422, "invalid_level", { permission: k, level: v });
          if (k === "team.manage" && v !== "none" && input.role !== "admin") fail(422, "permission_not_allowed_for_role", { permission: k, role: input.role });
        }
        const mine = actorLevels(a);
        const merged = { ...preset(input.role), ...asked };
        const bad = violations(merged, input.role);
        if (bad.length) fail(422, "permission_inconsistent", { violations: bad });
        const clamped: AccessClamp[] = [];
        const snapshot: PermissionValues = {};
        for (const k of KEYS) {
          const want = lvl(merged, k);
          if (levelIndex(k, want) > levelIndex(k, lvl(mine, k))) {
            snapshot[k] = lvl(mine, k);
            clamped.push({ permission: k, requested: want, applied: snapshot[k]!, reason: "ceiling" });
          } else snapshot[k] = want;
        }
        const invite: TeamInvite = {
          id: crypto.randomUUID(), email: input.email.trim(), role: input.role, specialistId: input.specialistId ?? null,
          expiresAt: new Date(Date.now() + 7 * 86_400_000).toISOString(), status: "pending", permissions: snapshot, invitedByUserId: a.id,
        };
        invites.push(invite);
        note("invited", a, null, [], invite.id);
        return opts.wait({ token: `mock-${crypto.randomUUID()}`, invite, clamped });
      } catch (e) {
        return Promise.reject(e);
      }
    },
    getAccessAudit: (q) => {
      if (opts.role() !== "owner") return Promise.reject(new BeautyApiError(403, "forbidden_role", "forbidden_role"));
      const list = q?.userId ? audit.filter((i) => i.target?.id === q.userId) : audit;
      const start = q?.cursor ? Number(q.cursor) || 0 : 0;
      const take = Math.min(100, Math.max(1, q?.take ?? 50));
      const items = list.slice(start, start + take);
      return opts.wait({ items, nextCursor: start + take < list.length ? String(start + take) : null });
    },
    getMyRevenue: ({ from, to }) => {
      const days = Math.max(1, Math.round((Date.parse(to) - Date.parse(from)) / 86_400_000) + 1);
      const completed = Math.min(40, Math.round(days * 0.4));
      const r: MyRevenue = {
        fromDate: from, toDate: to, specialistId: "m", completed, revenue: completed * 650,
        byLocation: completed ? [{ locationId: "c", name: "Центр", completed, revenue: completed * 650 }] : [],
      };
      return opts.wait(r);
    },
  };
  return api;
}
