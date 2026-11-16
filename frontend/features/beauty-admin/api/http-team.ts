import { apiJson } from "@/features/beauty-auth/session";
import { parsePermissions } from "@/features/beauty-auth/access";
import type { Role } from "@/features/beauty-auth/types";
import type { BeautyAdminApi } from "./client";
import type {
  AccessChange,
  AccessClamp,
  AccessSaveResult,
  AuditItem,
  MyRevenue,
  NotEditableReason,
  TeamInvite,
  TeamUser,
  UserAccess,
} from "../team/types";

/** Клієнт «Команда й доступи» (§30.9): шляхи `/api/users`, `/api/invites`, `/api/access`; `me/revenue` - `/api/beauty`. */
const NO_STORE = { cache: "no-store" } as const;
const enc = encodeURIComponent;

const str = (v: unknown, d = "") => (typeof v === "string" ? v : d);
const arr = <T>(v: unknown): T[] => (Array.isArray(v) ? (v as T[]) : []);
const strs = (v: unknown) => arr<unknown>(v).filter((x): x is string => typeof x === "string");
const obj = (v: unknown): Record<string, unknown> => (v && typeof v === "object" && !Array.isArray(v) ? (v as Record<string, unknown>) : {});

function toUser(d: unknown): TeamUser {
  const o = obj(d);
  return {
    id: str(o.id),
    email: str(o.email),
    fullName: str(o.fullName),
    role: str(o.role) as Role,
    specialistId: typeof o.specialistId === "string" ? o.specialistId : null,
    isActive: o.isActive !== false,
    lastLoginAt: typeof o.lastLoginAt === "string" ? o.lastLoginAt : null,
    accessVersion: typeof o.accessVersion === "number" ? o.accessVersion : 1,
    // Fail-closed: без явного `manageable: true` керувати не можна.
    manageable: o.manageable === true,
  };
}

const toChanges = (v: unknown): AccessChange[] =>
  arr<Record<string, unknown>>(v).map((c) => ({ permission: str(c.permission), from: str(c.from), to: str(c.to) }));

function toAccess(d: unknown): UserAccess {
  const o = obj(d);
  const assignable: Record<string, string[]> = {};
  for (const [k, v] of Object.entries(obj(o.assignable))) assignable[k] = strs(v);
  const reason = str(o.notEditableReason);
  return {
    userId: str(o.userId),
    role: str(o.role) as Role,
    isActive: o.isActive !== false,
    specialistId: typeof o.specialistId === "string" ? o.specialistId : null,
    accessVersion: typeof o.accessVersion === "number" ? o.accessVersion : 1,
    // Fail-closed: редагування лише за явним `editable: true`.
    editable: o.editable === true,
    notEditableReason: reason === "owner" || reason === "self" || reason === "rank" ? (reason as NotEditableReason) : null,
    roleOptions: strs(o.roleOptions) as Role[],
    granted: parsePermissions(o.granted) ?? {},
    effective: parsePermissions(o.effective) ?? {},
    assignable,
    locked: strs(o.locked),
    moduleDisabled: strs(o.moduleDisabled),
    ownWithoutSpecialist: o.ownWithoutSpecialist === true,
  };
}

const toClamped = (v: unknown): AccessClamp[] =>
  arr<Record<string, unknown>>(v).map((c) => ({
    permission: str(c.permission),
    requested: str(c.requested),
    applied: str(c.applied),
    reason: c.reason === "locked" ? "locked" : "ceiling",
  }));

function toSave(d: unknown): AccessSaveResult {
  const o = obj(d);
  const rc = obj(o.roleChange);
  return {
    ...toAccess(d),
    changes: toChanges(o.changes),
    roleChange: rc.from && rc.to ? { from: str(rc.from) as Role, to: str(rc.to) as Role } : null,
    clamped: toClamped(o.clamped),
  };
}

function toInvite(d: unknown): TeamInvite {
  const o = obj(d);
  return {
    id: str(o.id),
    email: str(o.email),
    role: str(o.role) as Role,
    specialistId: typeof o.specialistId === "string" ? o.specialistId : null,
    expiresAt: str(o.expiresAt),
    status: str(o.status),
    permissions: parsePermissions(o.permissions) ?? null,
    invitedByUserId: typeof o.invitedByUserId === "string" ? o.invitedByUserId : null,
  };
}

function toAudit(d: unknown): AuditItem {
  const o = obj(d);
  const who = (v: unknown) => {
    const p = obj(v);
    return typeof p.id === "string" ? { id: p.id, fullName: str(p.fullName) } : null;
  };
  return {
    id: str(o.id),
    createdAt: str(o.createdAt),
    action: str(o.action),
    actor: who(o.actor),
    target: who(o.target),
    inviteId: typeof o.inviteId === "string" ? o.inviteId : null,
    changes: toChanges(o.changes),
  };
}

type TeamApi = Pick<
  BeautyAdminApi,
  | "getTeamUsers"
  | "getTeamInvites"
  | "getUserAccess"
  | "saveUserAccess"
  | "resetUserAccess"
  | "setUserActive"
  | "createTeamInvite"
  | "getAccessAudit"
  | "getMyRevenue"
>;

export const httpTeamApi: TeamApi = {
  getTeamUsers: async () => arr<unknown>(await apiJson<unknown>("/api/users", NO_STORE)).map(toUser),

  getTeamInvites: async () => arr<unknown>(await apiJson<unknown>("/api/invites", NO_STORE)).map(toInvite),

  getUserAccess: async (id) => toAccess(await apiJson<unknown>(`/api/users/${enc(id)}/access`, NO_STORE)),

  saveUserAccess: async (id, input) =>
    toSave(
      await apiJson<unknown>(`/api/users/${enc(id)}/access`, {
        method: "PUT",
        body: JSON.stringify({
          expectedVersion: input.expectedVersion,
          ...(input.role ? { role: input.role } : {}),
          ...(input.permissions ? { permissions: input.permissions } : {}),
          dryRun: input.dryRun === true,
        }),
      }),
    ),

  resetUserAccess: async (id, input) =>
    toSave(
      await apiJson<unknown>(`/api/users/${enc(id)}/access/reset`, {
        method: "POST",
        body: JSON.stringify({ expectedVersion: input.expectedVersion, dryRun: input.dryRun === true }),
      }),
    ),

  setUserActive: async (id, isActive, expectedVersion) => {
    await apiJson<unknown>(`/api/users/${enc(id)}/status`, {
      method: "PATCH",
      body: JSON.stringify({ isActive, ...(expectedVersion !== undefined ? { expectedVersion } : {}) }),
    });
  },

  createTeamInvite: async (input) => {
    const r = obj(
      await apiJson<unknown>("/api/invites", {
        method: "POST",
        body: JSON.stringify({
          email: input.email.trim(),
          role: input.role,
          ...(input.specialistId ? { specialistId: input.specialistId } : {}),
          ...(input.permissions && Object.keys(input.permissions).length > 0 ? { permissions: input.permissions } : {}),
        }),
      }),
    );
    return { token: str(r.token), invite: toInvite(r.invite), clamped: toClamped(r.clamped) };
  },

  getAccessAudit: async (q) => {
    const p = new URLSearchParams();
    if (q?.userId) p.set("userId", q.userId);
    if (q?.cursor) p.set("cursor", q.cursor);
    p.set("take", String(q?.take ?? 50));
    const o = obj(await apiJson<unknown>(`/api/users/access-audit?${p}`, NO_STORE));
    return { items: arr<unknown>(o.items).map(toAudit), nextCursor: typeof o.nextCursor === "string" ? o.nextCursor : null };
  },

  getMyRevenue: async ({ from, to }) => {
    const o = obj(await apiJson<unknown>(`/api/beauty/me/revenue?from=${enc(from)}&to=${enc(to)}`, NO_STORE));
    const num = (v: unknown) => (typeof v === "number" && Number.isFinite(v) ? v : 0);
    const r: MyRevenue = {
      fromDate: str(o.fromDate, from),
      toDate: str(o.toDate, to),
      specialistId: typeof o.specialistId === "string" ? o.specialistId : null,
      completed: num(o.completed),
      revenue: num(o.revenue),
      byLocation: arr<Record<string, unknown>>(o.byLocation).map((l) => ({
        locationId: str(l.locationId),
        name: str(l.name),
        completed: num(l.completed),
        revenue: num(l.revenue),
      })),
    };
    return r;
  },
};
