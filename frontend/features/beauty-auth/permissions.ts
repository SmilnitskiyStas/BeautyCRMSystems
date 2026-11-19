import { accessOf, type Access, type AccessSubject, type PermissionKey } from "./access";

export interface NavItem {
  label: string;
  href: string;
}

export interface NavGroup {
  id: string;
  label: string;
  items: NavItem[];
}

/** Логічні блоки бічного меню (TASK-796). Маршрути не змінюються. */
export const NAV_GROUPS: NavGroup[] = [
  {
    id: "daily",
    label: "Щоденна робота",
    items: [
      { label: "Огляд", href: "/beauty" },
      { label: "Записи", href: "/beauty/calendar" },
      { label: "Розмови", href: "/beauty/inbox" },
      { label: "Дзвінки", href: "/beauty/calls" },
      { label: "Клієнти", href: "/beauty/clients" },
    ],
  },
  {
    id: "catalog",
    label: "Каталог і продажі",
    items: [
      { label: "Заклади", href: "/beauty/locations" },
      { label: "Спеціалісти", href: "/beauty/staff" },
      { label: "Послуги", href: "/beauty/services" },
      { label: "Ціни та акції", href: "/beauty/promos" },
      { label: "Розсилки", href: "/beauty/campaigns" },
      { label: "Склад", href: "/beauty/inventory" },
    ],
  },
  {
    id: "insights",
    label: "Аналітика та AI",
    items: [
      { label: "Аналітика", href: "/beauty/analytics" },
      { label: "AI-асистент", href: "/beauty/ai" },
    ],
  },
  {
    id: "system",
    label: "Система",
    items: [
      { label: "Канали", href: "/beauty/channels" },
      { label: "Налаштування", href: "/beauty/settings" },
      { label: "Команда й доступи", href: "/beauty/team" },
    ],
  },
];

const ALL_NAV: NavItem[] = NAV_GROUPS.flatMap((g) => g.items);

/**
 * Гейтинг меню й маршрутів за правами, а не за роллю (§30.8). Модулі вже враховані в `effective`, але ключі з найнижчим
 * рівнем без `none` (`services.catalog`, `staff.access`, `locations.access`) модуль не вимикає, тому для них перевіряємо й модуль.
 * Порядок правил: від конкретніших до загальних. Невідомий `/beauty/*` закритий (fail-closed).
 */
interface Rule {
  /** Точний шлях (`exact`) або префікс (`prefix`). */
  path: string;
  mode: "exact" | "prefix";
  test: (a: Access) => boolean;
}

const when = (key: PermissionKey, level: string) => (a: Access) => a.can(key, level);

const RULES: Rule[] = [
  { path: "/beauty", mode: "exact", test: when("calendar.view", "all") },
  { path: "/beauty/calendar", mode: "prefix", test: when("calendar.view", "own") },
  { path: "/beauty/inbox", mode: "prefix", test: when("channels.inbox", "view") },
  { path: "/beauty/calls", mode: "prefix", test: when("clients.calls", "all") },
  { path: "/beauty/clients", mode: "prefix", test: when("clients.view", "own") },
  // Список закладів: `all`; сторінки конкретних закладів (`/beauty/locations/{id}`): `own` (§30.8).
  { path: "/beauty/locations", mode: "exact", test: (a) => a.hasModule("beauty_booking") && a.can("locations.access", "all") },
  { path: "/beauty/locations", mode: "prefix", test: (a) => a.hasModule("beauty_booking") && a.can("locations.access", "own") },
  { path: "/beauty/staff", mode: "prefix", test: (a) => a.hasModule("beauty_booking") && a.can("staff.access", "own") },
  // Відхилення від §30.8 («завжди»): сторінка «Послуги» - це редактор собівартості (§24.8.2) і створення послуги; без
  // `revenue.costs >= view` вона порожня, тому пункт показуємо лише тоді, коли сторінка корисна. Знімається разом з read-only каталогом.
  { path: "/beauty/services", mode: "prefix", test: (a) => a.hasModule("beauty_catalog") && a.can("revenue.costs", "view") },
  { path: "/beauty/promos", mode: "prefix", test: (a) => a.hasModule("beauty_catalog") && a.can("services.catalog", "view") },
  { path: "/beauty/campaigns", mode: "prefix", test: when("marketing.campaigns", "edit") },
  // Склад (§32.9): модуль `beauty_inventory` (вимкнено за замовчуванням) і `inventory.view >= own`.
  { path: "/beauty/inventory", mode: "prefix", test: (a) => a.hasModule("beauty_inventory") && a.can("inventory.view", "own") },
  { path: "/beauty/analytics", mode: "prefix", test: when("revenue.view", "all") },
  { path: "/beauty/ai", mode: "prefix", test: (a) => a.can("ai.settings", "edit") || a.can("ai.use", "use") },
  { path: "/beauty/channels", mode: "prefix", test: when("channels.settings", "edit") },
  { path: "/beauty/settings", mode: "prefix", test: (a) => a.isOwner || a.can("settings.general", "view") },
  { path: "/beauty/team", mode: "prefix", test: when("team.manage", "manage") },
];

const trimSlash = (p: string) => (p.length > 1 ? p.replace(/\/+$/, "") : p);

function ruleFor(pathname: string): Rule | undefined {
  const path = trimSlash(pathname.split(/[?#]/)[0] ?? pathname);
  // Конкретніше правило виграє: exact для списку, інакше найдовший префікс.
  const exact = RULES.find((r) => r.mode === "exact" && r.path === path);
  if (exact) return exact;
  return RULES.filter((r) => r.mode === "prefix" && (path === r.path || path.startsWith(`${r.path}/`))).sort(
    (a, b) => b.path.length - a.path.length,
  )[0];
}

/** Чи може користувач відкрити маршрут (`subject` - користувач сесії; роль лише для тестів/пресету). */
export function canOpen(subject: AccessSubject, pathname: string): boolean {
  const path = trimSlash(pathname.split(/[?#]/)[0] ?? pathname);
  if (!path.startsWith("/beauty")) return true;
  const rule = ruleFor(path);
  return rule ? rule.test(accessOf(subject)) : false;
}

/** Плоский перелік доступних пунктів меню за порядком §30.8; «Спеціалісти» з `staff.access = own` стає «Мій профіль». */
export function navFor(subject: AccessSubject): NavItem[] {
  const a = accessOf(subject);
  return ALL_NAV.filter((n) => canOpen(subject, n.href)).map((n) =>
    n.href === "/beauty/staff" && !a.can("staff.access", "all") ? { ...n, label: "Мій профіль" } : n,
  );
}

/** Групи меню з відфільтрованими пунктами (порожні групи прибираються). */
export function navGroupsFor(subject: AccessSubject): NavGroup[] {
  const allowed = new Map(navFor(subject).map((n) => [n.href, n]));
  return NAV_GROUPS.map((g) => ({ ...g, items: g.items.flatMap((i) => (allowed.has(i.href) ? [allowed.get(i.href)!] : [])) })).filter(
    (g) => g.items.length > 0,
  );
}

/** `/beauty`, якщо `calendar.view = all`; інакше `/beauty/calendar`; без календаря - перший доступний пункт меню (§30.8). */
export function homeFor(subject: AccessSubject): string {
  const a = accessOf(subject);
  if (a.can("calendar.view", "all")) return "/beauty";
  if (a.can("calendar.view", "own")) return "/beauty/calendar";
  return navFor(subject)[0]?.href ?? "/beauty/services";
}

/** Ключ дозволу «Мій профіль» (`staff.access = own`): `/beauty/staff` перенаправляє на власну сторінку. */
export const isOwnProfileOnly = (subject: AccessSubject) => !accessOf(subject).can("staff.access", "all");

// Права на блоки сторінок. Приймають користувача сесії (або роль для пресету), рішення бере `accessOf`.
const gate = (key: PermissionKey, level: string) => (s: AccessSubject) => accessOf(s).can(key, level);

/** Лише директор змінює політику скасування (§30.6.6, Q6: зарезервовано). */
export const canEditSettings = (s: AccessSubject) => accessOf(s).isOwner;
/** §20.9 / `ai.provider_keys`: ключ API провайдера ШІ - лише директор. */
export const canManageAiKey = canEditSettings;
export const canEditAiSettings = gate("ai.settings", "edit");
export const canUseAi = gate("ai.use", "use");
/** Розмови: перегляд за `view`, відповідь і «Опрацьовано» за `reply`. */
export const canUseInbox = gate("channels.inbox", "view");
export const canReplyInbox = gate("channels.inbox", "reply");
export const canUseCalls = gate("clients.calls", "all");
/** Вподобання нагадувань клієнта (§30.6.8): за `clients.view`. */
export const canManageReminderPrefs = gate("clients.view", "own");
/** Налаштування нагадувань (§30.6.6): читання `settings.general >= view`, зміна `= edit`. */
export const canViewReminderSettings = gate("settings.general", "view");
export const canEditReminderSettings = gate("settings.general", "edit");
export const canSeeFinance = gate("revenue.view", "all");
export const canSeeCosts = gate("revenue.costs", "view");
export const canEditCosts = gate("revenue.costs", "edit");
/** Повний телефон клієнта: `clients.contacts = full`; інакше лише маска (§30.5). */
export const canSeeFullPhone = gate("clients.contacts", "full");
export const canManageCatalog = gate("services.catalog", "edit");
export const canUseMarketing = gate("marketing.campaigns", "edit");
export const canManageConsent = gate("clients.view", "own");
/** `codeHint` купона на записі - лише за `calendar.manage = all` (§30.6.2). */
export const canSeeCouponHint = gate("calendar.manage", "all");
/** «Команда й доступи» (§30.8): `team.manage = manage`; журнал змін - лише директор (Q7). */
export const canManageTeam = gate("team.manage", "manage");
export const canSeeAccessAudit = (s: AccessSubject) => accessOf(s).isOwner;
/** «Мій виторг» (§30.8): `revenue.view = own` (повна аналітика - окремо, `revenue.view = all`). */
export const canSeeMyRevenue = gate("revenue.view", "own");
export const canEditLocations = gate("locations.access", "edit");
export const canEditStaff = gate("staff.access", "edit");
/** Склад (§32.2): перегляд закладів scope = `own`; керування (операції) = `manage >= own`; каталог товарів = `manage = all`; ціни = `costs`. */
export const canViewInventory = (s: AccessSubject) => accessOf(s).hasModule("beauty_inventory") && accessOf(s).can("inventory.view", "own");
export const canManageInventory = (s: AccessSubject) => accessOf(s).hasModule("beauty_inventory") && accessOf(s).can("inventory.manage", "own");
/** Норми матеріалів (§32.16): змінюють лише з `inventory.manage = all` (сервер додатково вимагає owner/admin). */
export const canEditInventoryNorms = (s: AccessSubject) => accessOf(s).hasModule("beauty_inventory") && accessOf(s).can("inventory.manage", "all");
export const canSeeInventoryCosts = gate("inventory.costs", "view");
export const canEditInventoryCosts = gate("inventory.costs", "edit");
/** Календар: `own` - лише власні записи (майстер у пресеті). */
export const seesOnlyOwnCalendar = (s: AccessSubject) => !accessOf(s).can("calendar.view", "all");

const CONTROL_CHARS = /[\x00-\x1f\x7f]/;
/** Умовний origin для розбору відносного шляху: перевіряємо лише, що розбір не виводить за межі сайту. */
const BASE_ORIGIN = "http://safe.invalid";

/**
 * Дозволяє тільки відносні шляхи в межах сайту (захист від open redirect). Відсікає керівні символи
 * (браузер ігнорує tab/newline усередині URL, тож "/<TAB>/evil.com" стає "//evil.com"), "//" і зворотний слеш,
 * далі перевіряє, що `new URL(next, origin).origin === origin` і нормалізований шлях не починається з "//".
 */
export function safeNext(next: string | null | undefined, fallback = "/beauty"): string {
  if (!next || !next.startsWith("/") || next.startsWith("//") || next.includes("\\") || CONTROL_CHARS.test(next)) {
    return fallback;
  }
  let url: URL;
  try {
    url = new URL(next, BASE_ORIGIN);
  } catch {
    return fallback;
  }
  if (url.origin !== BASE_ORIGIN) return fallback;
  const path = url.pathname + url.search + url.hash;
  // "/..//evil" нормалізується до "//evil" — це вже protocol-relative URL.
  return path.startsWith("//") ? fallback : path;
}
