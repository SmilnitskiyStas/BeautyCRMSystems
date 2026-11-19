import type { Role, SessionUser } from "./types";

/**
 * Модель прав на клієнті (ADR-023, §30). Лише видимість: backend перевіряє доступ незалежно (§30.5).
 * Рівні в кожному ключі впорядковані за зростанням; перший є найнижчим (fail-closed).
 */

/** Каталог `catalogVersion = 2` (§30.2, §32.2): ключ -> рівні за зростанням. Дзеркало `GET /api/access/catalog`. */
export const PERMISSION_LEVELS = {
  "calendar.view": ["none", "own", "all"],
  "calendar.manage": ["none", "own", "all"],
  "clients.view": ["none", "own", "all"],
  "clients.edit": ["none", "own", "all"],
  "clients.contacts": ["masked", "full"],
  "clients.calls": ["none", "all"],
  "revenue.view": ["none", "own", "all"],
  "revenue.costs": ["none", "view", "edit"],
  "channels.inbox": ["none", "view", "reply"],
  "channels.settings": ["none", "edit"],
  "ai.use": ["none", "use"],
  "ai.settings": ["none", "edit"],
  "services.catalog": ["view", "edit"],
  "marketing.campaigns": ["none", "edit"],
  "staff.access": ["own", "all", "edit"],
  "locations.access": ["own", "all", "edit"],
  "settings.general": ["none", "view", "edit"],
  "team.manage": ["none", "manage"],
  // Склад (ADR-025, §32.2): модуль `beauty_inventory`; `catalogVersion = 2`.
  "inventory.view": ["none", "own", "all"],
  "inventory.manage": ["none", "own", "all"],
  "inventory.costs": ["none", "view", "edit"],
} as const satisfies Record<string, readonly string[]>;

export type PermissionKey = keyof typeof PERMISSION_LEVELS;
export type PermissionLevel = (typeof PERMISSION_LEVELS)[PermissionKey][number];
export type PermissionMap = Partial<Record<PermissionKey, string>>;

export const PERMISSION_KEYS = Object.keys(PERMISSION_LEVELS) as PermissionKey[];

/** Пресети ролей (§30.3). Для відображення в mock/до першого `me`; джерело істини - `permissions` з `me`. */
const SPECIALIST: Record<PermissionKey, string> = {
  "calendar.view": "own", "calendar.manage": "own", "clients.view": "none", "clients.edit": "none",
  "clients.contacts": "masked", "clients.calls": "none", "revenue.view": "none", "revenue.costs": "none",
  "channels.inbox": "none", "channels.settings": "none", "ai.use": "none", "ai.settings": "none",
  "services.catalog": "view", "marketing.campaigns": "none", "staff.access": "own", "locations.access": "own",
  "settings.general": "none", "team.manage": "none",
  "inventory.view": "own", "inventory.manage": "none", "inventory.costs": "none",
};
const ADMIN: Record<PermissionKey, string> = {
  "calendar.view": "all", "calendar.manage": "all", "clients.view": "all", "clients.edit": "all",
  "clients.contacts": "full", "clients.calls": "all", "revenue.view": "all", "revenue.costs": "edit",
  "channels.inbox": "reply", "channels.settings": "edit", "ai.use": "use", "ai.settings": "edit",
  "services.catalog": "edit", "marketing.campaigns": "edit", "staff.access": "edit", "locations.access": "edit",
  "settings.general": "view", "team.manage": "manage",
  "inventory.view": "all", "inventory.manage": "all", "inventory.costs": "edit",
};
const DEPUTY: Record<PermissionKey, string> = {
  ...ADMIN,
  "revenue.view": "none", "revenue.costs": "none", "channels.settings": "none", "ai.settings": "none",
  "services.catalog": "view", "marketing.campaigns": "none", "staff.access": "all", "locations.access": "all",
  "settings.general": "none", "team.manage": "none",
  "inventory.costs": "none",
};
const OWNER: Record<PermissionKey, string> = { ...ADMIN, "settings.general": "edit" };

export const ROLE_PRESETS: Record<Role, Readonly<Record<PermissionKey, string>>> = {
  owner: OWNER,
  admin: ADMIN,
  deputy: DEPUTY,
  specialist: SPECIALIST,
};

/** Індекс рівня в ключі; -1 для невідомого ключа чи рівня (тоді доступу немає). */
export function levelIndex(key: string, level: string | undefined): number {
  const levels = (PERMISSION_LEVELS as Record<string, readonly string[]>)[key];
  return levels && level !== undefined ? levels.indexOf(level) : -1;
}

/** Результат `useAccess()`; чиста модель без React, щоб гейтинг тестувався окремо. */
export interface Access {
  role: Role;
  specialistId: string | null;
  /** Ефективний рівень ключа; невідомий/відсутній ключ дає найнижчий рівень каталогу. */
  level(key: PermissionKey): string;
  /** `true`, якщо ефективний рівень ключа >= `level` (за порядком каталогу). Невідомий рівень дає `false`. */
  can(key: PermissionKey, level: string): boolean;
  /** Модуль увімкнено; якщо перелік модулів невідомий (ще не було `me`), вважається увімкненим. */
  hasModule(module: string): boolean;
  isOwner: boolean;
}

/** Хто завгодно, з чого можна визначити права: роль (лише пресет) або користувач сесії. */
export type AccessSubject = Role | Pick<SessionUser, "role"> & Partial<SessionUser>;

export function accessOf(subject: AccessSubject): Access {
  const user = typeof subject === "string" ? { role: subject } : subject;
  const role = user.role;
  const raw: Record<string, string | undefined> = user.permissions ?? ROLE_PRESETS[role] ?? {};
  const modules = user.modules;
  const level = (key: PermissionKey) => {
    const levels = PERMISSION_LEVELS[key];
    const v = raw[key];
    return v !== undefined && (levels as readonly string[]).includes(v) ? v : levels[0];
  };
  return {
    role,
    specialistId: "specialistId" in user ? (user.specialistId ?? null) : null,
    level,
    can: (key, min) => {
      const need = levelIndex(key, min);
      return need >= 0 && levelIndex(key, level(key)) >= need;
    },
    hasModule: (m) => (modules ? modules.includes(m) : true),
    isOwner: role === "owner",
  };
}

/** Звужує невідомий об'єкт `me.permissions` до `Record<string,string>` (чужі типи значень відкидаються). */
export function parsePermissions(raw: unknown): Record<string, string> | undefined {
  if (!raw || typeof raw !== "object" || Array.isArray(raw)) return undefined;
  const out: Record<string, string> = {};
  for (const [k, v] of Object.entries(raw)) if (typeof v === "string") out[k] = v;
  return out;
}

// ---------- Каталог прав (§30.9.2) ----------

export interface AccessCatalogPermission {
  key: string;
  levels: string[];
  modules: string[];
  /** `menu`: рівні `menuLevels` змінюють лише меню, дані ті самі (Q-C3); UI підписує «Впливає лише на меню». */
  scope?: "menu";
  menuLevels?: string[];
}
export interface AccessCatalogBlock {
  block: string;
  routes: string[];
  permissions: AccessCatalogPermission[];
}
export interface AccessCatalogConstraint {
  type: "lte" | "requires" | "roleCeiling";
  key: string;
  other?: string;
  level?: string;
  roles?: string[];
}
export interface AccessCatalog {
  catalogVersion: number;
  roles: { code: Role; rank: number; tenantAssignable: boolean }[];
  blocks: AccessCatalogBlock[];
  constraints: AccessCatalogConstraint[];
  presets: Partial<Record<Role, Record<string, string>>>;
  reservedToOwner: string[];
}

/** Нормалізація відповіді `GET /api/access/catalog`: чужі/зламані поля відкидаються, масиви гарантовано масиви. */
export function toAccessCatalog(d: unknown): AccessCatalog {
  const o = (d && typeof d === "object" ? d : {}) as Record<string, unknown>;
  const arr = <T>(v: unknown): T[] => (Array.isArray(v) ? (v as T[]) : []);
  const strs = (v: unknown) => arr<unknown>(v).filter((x): x is string => typeof x === "string");
  return {
    catalogVersion: typeof o.catalogVersion === "number" ? o.catalogVersion : 0,
    roles: arr<AccessCatalog["roles"][number]>(o.roles),
    blocks: arr<Record<string, unknown>>(o.blocks).map((b) => ({
      block: String(b.block ?? ""),
      routes: strs(b.routes),
      permissions: arr<Record<string, unknown>>(b.permissions).map((p) => ({
        key: String(p.key ?? ""),
        levels: strs(p.levels),
        modules: strs(p.modules),
        ...(p.scope === "menu" ? { scope: "menu" as const, menuLevels: strs(p.menuLevels) } : {}),
      })),
    })),
    constraints: arr<AccessCatalogConstraint>(o.constraints),
    presets: (o.presets && typeof o.presets === "object" ? o.presets : {}) as AccessCatalog["presets"],
    reservedToOwner: strs(o.reservedToOwner),
  };
}

/** Каталог для mock-режиму: збирається зі статичної моделі (блоки/маршрути за §30.8, обмеження §30.2). */
export function mockAccessCatalog(): AccessCatalog {
  const block = (name: string, routes: string[], keys: PermissionKey[], modules: string[]): AccessCatalogBlock => ({
    block: name,
    routes,
    permissions: keys.map((key) => ({
      key,
      levels: [...PERMISSION_LEVELS[key]],
      modules,
      ...(key === "staff.access" || key === "locations.access" ? { scope: "menu" as const, menuLevels: ["own", "all"] } : {}),
    })),
  });
  return {
    catalogVersion: 2,
    roles: [
      { code: "owner", rank: 4, tenantAssignable: false },
      { code: "admin", rank: 3, tenantAssignable: true },
      { code: "deputy", rank: 2, tenantAssignable: true },
      { code: "specialist", rank: 1, tenantAssignable: true },
    ],
    blocks: [
      block("calendar", ["/beauty", "/beauty/calendar"], ["calendar.view", "calendar.manage"], ["beauty_booking"]),
      block("clients", ["/beauty/clients", "/beauty/calls"], ["clients.view", "clients.edit", "clients.contacts", "clients.calls"], ["beauty_clients"]),
      block("revenue", ["/beauty/analytics"], ["revenue.view", "revenue.costs"], ["beauty_analytics"]),
      block("channels", ["/beauty/inbox", "/beauty/channels"], ["channels.inbox", "channels.settings"], ["beauty_channels"]),
      block("ai", ["/beauty/ai"], ["ai.use", "ai.settings"], ["beauty_ai"]),
      block("services", ["/beauty/services", "/beauty/promos"], ["services.catalog"], ["beauty_catalog"]),
      block("marketing", ["/beauty/campaigns"], ["marketing.campaigns"], ["beauty_marketing"]),
      block("staff", ["/beauty/staff"], ["staff.access"], ["beauty_booking"]),
      block("locations", ["/beauty/locations"], ["locations.access"], ["beauty_booking"]),
      block("settings", ["/beauty/settings"], ["settings.general"], []),
      block("team", ["/beauty/team"], ["team.manage"], []),
      block("inventory", ["/beauty/inventory"], ["inventory.view", "inventory.manage", "inventory.costs"], ["beauty_inventory"]),
    ],
    constraints: [
      { type: "lte", key: "calendar.manage", other: "calendar.view" },
      { type: "lte", key: "clients.edit", other: "clients.view" },
      { type: "requires", key: "clients.calls", other: "clients.view", level: "all" },
      { type: "requires", key: "revenue.costs", other: "revenue.view", level: "all" },
      { type: "requires", key: "marketing.campaigns", other: "clients.view", level: "all" },
      { type: "roleCeiling", key: "team.manage", roles: ["admin"] },
      { type: "lte", key: "inventory.manage", other: "inventory.view" },
      { type: "requires", key: "inventory.costs", other: "revenue.view", level: "all" },
    ],
    presets: { admin: { ...ROLE_PRESETS.admin }, deputy: { ...ROLE_PRESETS.deputy }, specialist: { ...ROLE_PRESETS.specialist } },
    reservedToOwner: ["ai.provider_keys", "ai.mode_auto", "cancellation.policy", "team.admins", "access.audit"],
  };
}
