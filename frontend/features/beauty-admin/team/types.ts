import type { Role } from "@/features/beauty-auth/types";

/** Типи «Команда й доступи» (ADR-023, §30.9). Права - `{ключ: рівень}`, ключі й рівні за каталогом. */
export type PermissionValues = Record<string, string>;

/** Елемент `GET /api/users` (§30.6.1): + `accessVersion`, `manageable`. */
export interface TeamUser {
  id: string;
  email: string;
  fullName: string;
  role: Role;
  specialistId: string | null;
  isActive: boolean;
  lastLoginAt: string | null;
  accessVersion: number;
  /** Чи може актор керувати цим користувачем (ранг, не owner, не він сам). */
  manageable: boolean;
}

export type NotEditableReason = "owner" | "self" | "rank";

/** `GET /api/users/{id}/access` (§30.9.3). */
export interface UserAccess {
  userId: string;
  role: Role;
  isActive: boolean;
  specialistId: string | null;
  accessVersion: number;
  editable: boolean;
  notEditableReason: NotEditableReason | null;
  /** Ролі, на які актор може змінити роль цілі (V1: admin <-> deputy). `[]` = зміна ролі недоступна. */
  roleOptions: Role[];
  granted: PermissionValues;
  effective: PermissionValues;
  /** Рівні, які актор може виставити; `[]` - ключ закритий. */
  assignable: Record<string, string[]>;
  /** Ключі зі значенням вище стелі актора (видав директор): не змінюються. */
  locked: string[];
  /** Ключі, чий модуль вимкнено: діє найнижчий рівень, підвищити не можна. */
  moduleDisabled: string[];
  ownWithoutSpecialist: boolean;
}

export interface AccessChange {
  permission: string;
  from: string;
  to: string;
}
export interface AccessClamp {
  permission: string;
  requested: string;
  applied: string;
  reason: "ceiling" | "locked";
}

/** Відповідь PUT/reset (§30.9.4): доступ + diff. */
export interface AccessSaveResult extends UserAccess {
  changes: AccessChange[];
  roleChange: { from: Role; to: Role } | null;
  clamped: AccessClamp[];
}

export interface AccessSaveInput {
  expectedVersion: number;
  role?: Role;
  /** PATCH-семантика: лише змінені ключі. */
  permissions?: PermissionValues;
  dryRun?: boolean;
}

export type InviteRole = "admin" | "deputy" | "specialist";

export interface TeamInviteInput {
  email: string;
  role: InviteRole;
  specialistId?: string;
  /** Перевизначення поверх пресету ролі (§30.9.5). */
  permissions?: PermissionValues;
}

export interface TeamInvite {
  id: string;
  email: string;
  role: Role;
  specialistId: string | null;
  expiresAt: string;
  status: string;
  permissions: PermissionValues | null;
  invitedByUserId: string | null;
}

/** Токен показується один раз і не кешується. `clamped` - ключі, обрізані стелею запрошувача (B1). */
export interface TeamInviteCreated {
  token: string;
  invite: TeamInvite;
  clamped: AccessClamp[];
}

export interface AuditItem {
  id: string;
  createdAt: string;
  action: string;
  actor: { id: string; fullName: string } | null;
  target: { id: string; fullName: string } | null;
  inviteId: string | null;
  changes: AccessChange[];
}
export interface AuditPage {
  items: AuditItem[];
  nextCursor: string | null;
}

/** `GET /api/beauty/me/revenue` (§30.9.7): лише власні записи, без собівартості. */
export interface MyRevenue {
  fromDate: string;
  toDate: string;
  specialistId: string | null;
  completed: number;
  revenue: number;
  byLocation: { locationId: string; name: string; completed: number; revenue: number }[];
}
