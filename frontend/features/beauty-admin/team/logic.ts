import { PERMISSION_LEVELS, levelIndex, type AccessCatalog, type AccessCatalogConstraint, type AccessCatalogPermission } from "@/features/beauty-auth/access";
import { BeautyApiError, humanizeError } from "@/features/beauty-auth/errors";
import type { Role } from "@/features/beauty-auth/types";
import type { AccessChange, NotEditableReason, PermissionValues, UserAccess } from "./types";

/** Українські підписи каталогу: назви блоків, прав і рівнів. Каталог з API їх не містить (§30.2). */
export const BLOCK_TITLE: Record<string, string> = {
  calendar: "Календар",
  clients: "Клієнти",
  revenue: "Виторг і аналітика",
  channels: "Канали й розмови",
  ai: "AI-асистент",
  services: "Послуги й ціни",
  marketing: "Розсилки й акції",
  staff: "Спеціалісти",
  locations: "Заклади",
  settings: "Налаштування",
  team: "Команда й доступи",
  inventory: "Склад",
};

interface PermText {
  title: string;
  levels: Record<string, string>;
}
const NONE = "Немає доступу";
export const PERM_TEXT: Record<string, PermText> = {
  "calendar.view": { title: "Перегляд записів", levels: { none: NONE, own: "Лише свої записи", all: "Усі записи" } },
  "calendar.manage": { title: "Створення й зміна записів", levels: { none: "Не може змінювати", own: "Лише свої записи", all: "Усі записи" } },
  "clients.view": { title: "Перегляд клієнтів", levels: { none: NONE, own: "Лише клієнти зі своїми записами", all: "Усі клієнти" } },
  "clients.edit": { title: "Зміна клієнтів і нотаток", levels: { none: "Не може змінювати", own: "Лише клієнти зі своїми записами", all: "Усі клієнти" } },
  "clients.contacts": { title: "Контакти клієнтів", levels: { masked: "Приховані (маска)", full: "Повні номери й пошти" } },
  "clients.calls": { title: "Задачі дзвінків", levels: { none: NONE, all: "Усі задачі" } },
  "revenue.view": { title: "Виторг", levels: { none: NONE, own: "Лише власний виторг", all: "Уся аналітика" } },
  "revenue.costs": { title: "Собівартість і виплати", levels: { none: NONE, view: "Перегляд", edit: "Перегляд і зміна" } },
  "channels.inbox": { title: "Розмови з клієнтами", levels: { none: NONE, view: "Лише перегляд", reply: "Перегляд і відповіді" } },
  "channels.settings": { title: "Налаштування каналів", levels: { none: NONE, edit: "Зміна налаштувань" } },
  "ai.use": { title: "Користування AI-асистентом", levels: { none: NONE, use: "Дозволено" } },
  "ai.settings": { title: "Налаштування AI", levels: { none: NONE, edit: "Зміна налаштувань" } },
  "services.catalog": { title: "Послуги, ціни, акції", levels: { view: "Лише перегляд", edit: "Перегляд і зміна" } },
  "marketing.campaigns": { title: "Розсилки й акції", levels: { none: NONE, edit: "Створення й запуск" } },
  "staff.access": { title: "Спеціалісти", levels: { own: "Мій профіль", all: "Список спеціалістів", edit: "Керування спеціалістами" } },
  "locations.access": { title: "Заклади", levels: { own: "Мої заклади", all: "Усі заклади", edit: "Керування закладами" } },
  "settings.general": { title: "Налаштування бізнесу", levels: { none: NONE, view: "Лише перегляд", edit: "Перегляд і зміна" } },
  "team.manage": { title: "Керування командою", levels: { none: NONE, manage: "Керування" } },
  "inventory.view": { title: "Перегляд складу", levels: { none: NONE, own: "Лише заклади спеціаліста", all: "Усі заклади" } },
  "inventory.manage": { title: "Операції зі складом", levels: { none: "Не може змінювати", own: "Лише заклади спеціаліста", all: "Усі заклади й каталог товарів" } },
  "inventory.costs": { title: "Закупівельні ціни складу", levels: { none: NONE, view: "Перегляд", edit: "Перегляд і зміна" } },
};

export const permTitle = (key: string) => PERM_TEXT[key]?.title ?? key;
export const levelLabel = (key: string, level: string) => PERM_TEXT[key]?.levels[level] ?? level;

export const ROLE_TITLE: Record<string, string> = { owner: "Директор", admin: "Адміністратор", deputy: "Заступник", specialist: "Майстер" };
export const roleTitle = (r: string) => ROLE_TITLE[r] ?? r;

export const AUDIT_ACTION: Record<string, string> = {
  permissions_changed: "Змінено права",
  role_changed: "Змінено роль",
  preset_reset: "Скинуто до пресету ролі",
  invited: "Надіслано запрошення",
  invite_revoked: "Скасовано запрошення",
  invite_accepted: "Запрошення прийнято",
  activated: "Доступ відновлено",
  deactivated: "Доступ вимкнено",
  migrated: "Міграція доступів",
};

const levelsOf = (key: string): readonly string[] => (PERMISSION_LEVELS as Record<string, readonly string[]>)[key] ?? [];
const lowest = (key: string) => levelsOf(key)[0] ?? "none";

/** Збережене значення (для директора granted порожній, тому беремо ефективне). */
const baseOf = (a: UserAccess) => (a.notEditableReason === "owner" ? a.effective : a.granted);

/** Поточне значення ключа в редакторі: чернетка, інакше збережене. */
export function currentValue(a: UserAccess, key: string, draft: PermissionValues): string {
  return draft[key] ?? baseOf(a)[key] ?? lowest(key);
}

const NOT_EDITABLE_TEXT: Record<NotEditableReason, string> = {
  owner: "Права директора не змінюються",
  self: "Це ваш власний рядок: права змінює керівник вище",
  rank: "Користувач вищого або рівного рангу: змінити права не можна",
};

export interface OptionState {
  disabled: boolean;
  /** Чому недоступно; показується текстом біля варіанта (не лише кольором). */
  reason?: string;
}

/** Чи можна вибрати рівень і чому ні. Порядок причин за §30.9.3: «видано директором» раніше за «модуль вимкнено». */
export function optionState(a: UserAccess, key: string, level: string, draft: PermissionValues): OptionState {
  const selected = currentValue(a, key, draft) === level;
  if (!a.editable) {
    return { disabled: true, ...(selected ? {} : { reason: a.notEditableReason ? NOT_EDITABLE_TEXT[a.notEditableReason] : "Зміна недоступна" }) };
  }
  if (selected) return { disabled: false };
  if (a.locked.includes(key)) return { disabled: true, reason: "Видано директором: змінити не можна" };
  const levels = levelsOf(key);
  const raising = levels.indexOf(level) > levels.indexOf(a.granted[key] ?? lowest(key));
  if (a.moduleDisabled.includes(key) && raising) return { disabled: true, reason: "Модуль вимкнено для вашого бізнесу" };
  if (!(a.assignable[key] ?? []).includes(level)) {
    return { disabled: true, reason: key === "team.manage" ? "Лише для ролі «Адміністратор»" : "Вище за ваш власний рівень" };
  }
  return { disabled: false };
}

/** Відмінності чернетки від збереженого (лише справжні зміни). */
export function draftChanges(a: UserAccess, draft: PermissionValues): AccessChange[] {
  const base = baseOf(a);
  return Object.entries(draft)
    .filter(([k, v]) => v !== (base[k] ?? lowest(k)))
    .map(([k, v]) => ({ permission: k, from: base[k] ?? lowest(k), to: v }));
}

/** Підпис для рівня з `scope: "menu"` (Q-C3): рівень змінює лише меню, довідник видно в обох. */
export function menuScopeNote(p: AccessCatalogPermission | undefined, level: string): string | null {
  return p?.scope === "menu" && p.menuLevels?.includes(level) ? "Впливає лише на меню: довідник видно в обох варіантах" : null;
}

export interface Violation {
  permission: string;
  text: string;
}

/** Локальна перевірка обмежень каталогу (§30.2) на повному наборі; backend перевіряє повторно (422 `permission_inconsistent`). */
export function findViolations(constraints: AccessCatalogConstraint[], values: PermissionValues, role: Role): Violation[] {
  const get = (k: string) => values[k] ?? lowest(k);
  const out: Violation[] = [];
  for (const c of constraints) {
    const v = get(c.key);
    if (c.type === "lte" && c.other && levelIndex(c.key, v) > levelIndex(c.key, get(c.other))) {
      out.push({ permission: c.key, text: `«${permTitle(c.key)}» не може бути вищим за «${permTitle(c.other)}» (зараз: ${levelLabel(c.other, get(c.other))}).` });
    } else if (c.type === "requires" && c.other && c.level && levelIndex(c.key, v) > 0 && levelIndex(c.other, get(c.other)) < levelIndex(c.other, c.level)) {
      out.push({ permission: c.key, text: `«${permTitle(c.key)}» потребує: «${permTitle(c.other)}» — «${levelLabel(c.other, c.level)}».` });
    } else if (c.type === "roleCeiling" && c.roles && levelIndex(c.key, v) > 0 && !c.roles.includes(role)) {
      out.push({ permission: c.key, text: `«${permTitle(c.key)}» доступне лише ролі «${c.roles.map(roleTitle).join(", ")}».` });
    }
  }
  return out;
}

/** Текст для `422 permission_inconsistent {violations}` з backend. */
export function violationText(v: { permission?: unknown; rule?: unknown; other?: unknown; level?: unknown }): string {
  const key = String(v.permission ?? "");
  const other = typeof v.other === "string" ? v.other : "";
  if (v.rule === "requires" && other) return `«${permTitle(key)}» потребує: «${permTitle(other)}» — «${levelLabel(other, String(v.level ?? ""))}».`;
  if (v.rule === "lte" && other) return `«${permTitle(key)}» не може бути вищим за «${permTitle(other)}».`;
  return `Налаштування «${permTitle(key)}» суперечить іншим правам.`;
}

/** Блоки каталогу як «основне право + підпункти»: перший ключ блоку - основний. */
export function blockEntries(catalog: AccessCatalog) {
  return catalog.blocks
    .filter((b) => b.permissions.length > 0)
    .map((b) => ({ block: b.block, title: BLOCK_TITLE[b.block] ?? b.block, main: b.permissions[0]!, subs: b.permissions.slice(1) }));
}

/** Рівні, доступні запрошувачу в діалозі: до його власного ефективного рівня (сервер обріже ще й за стелею, `clamped`). */
export function inviteAssignable(actorLevel: (key: string) => string, role: Role): Record<string, string[]> {
  const out: Record<string, string[]> = {};
  for (const [key, levels] of Object.entries(PERMISSION_LEVELS as Record<string, readonly string[]>)) {
    const max = levelIndex(key, actorLevel(key));
    out[key] = key === "team.manage" && role !== "admin" ? [] : levels.filter((_, i) => i <= max);
  }
  return out;
}

/** Людські повідомлення про помилку запису доступу (§30.11): перелік причин із тіла відповіді або загальний текст. */
export function accessErrorMessages(e: unknown): string[] {
  if (e instanceof BeautyApiError) {
    const body = e.body ?? {};
    const keys = Array.isArray(body.permissions) ? body.permissions.filter((x): x is string => typeof x === "string") : [];
    if (e.code === "permission_inconsistent" && Array.isArray(body.violations) && body.violations.length > 0) {
      return body.violations.map((v) => violationText((v ?? {}) as Record<string, unknown>));
    }
    if (e.code === "permission_ceiling" && keys.length > 0) {
      return [`Не можна змінити: ${keys.map(permTitle).join(", ")}. Це вище за ваш рівень або видано директором.`];
    }
    if (e.code === "module_disabled" && e.status === 422 && keys.length > 0) {
      return [`Модуль вимкнено, підвищити не можна: ${keys.map(permTitle).join(", ")}.`];
    }
    if (e.code === "invalid_level" || e.code === "invalid_permission") {
      return ["Каталог прав змінився. Оновіть сторінку й повторіть."];
    }
  }
  return [humanizeError(e, "Не вдалося зберегти. Спробуйте ще раз.")];
}
