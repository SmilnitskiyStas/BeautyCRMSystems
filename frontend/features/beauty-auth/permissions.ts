import type { Role } from "./types";

export interface NavItem {
  label: string;
  href: string;
}

const ALL_NAV: NavItem[] = [
  { label: "Огляд", href: "/beauty" },
  { label: "Записи", href: "/beauty/calendar" },
  { label: "Розмови", href: "/beauty/inbox" },
  { label: "Дзвінки", href: "/beauty/calls" },
  { label: "Клієнти", href: "/beauty/clients" },
  { label: "Спеціалісти", href: "/beauty/staff" },
  { label: "Заклади", href: "/beauty/locations" },
  { label: "Ціни та акції", href: "/beauty/promos" },
  { label: "Аналітика", href: "/beauty/analytics" },
  { label: "AI-асистент", href: "/beauty/ai" },
  { label: "Канали", href: "/beauty/channels" },
  { label: "Налаштування", href: "/beauty/settings" },
];

/**
 * Узгоджено з матрицею ролей §10/§13: specialist — свій календар і власний профіль (`/beauty/staff` перенаправляє
 * на `/beauty/staff/{specialistId}`); clients/analytics/channels/ai — owner/admin.
 */
const SPECIALIST_PATHS = ["/beauty/calendar", "/beauty/staff"];

export function navFor(role: Role): NavItem[] {
  if (role !== "specialist") return ALL_NAV;
  return ALL_NAV.filter((n) => SPECIALIST_PATHS.includes(n.href)).map((n) =>
    n.href === "/beauty/staff" ? { ...n, label: "Мій профіль" } : n,
  );
}

/** Сторінка конкретного закладу `/beauty/locations/{id}` (§18.3): specialist бачить її без редагування; список - ні. */
const LOCATION_PAGE = /^\/beauty\/locations\/[^/]+\/?$/;

export function canOpen(role: Role, pathname: string): boolean {
  if (role !== "specialist") return true;
  return LOCATION_PAGE.test(pathname) || SPECIALIST_PATHS.some((p) => pathname === p || pathname.startsWith(`${p}/`));
}

export const homeFor = (role: Role) => (role === "specialist" ? "/beauty/calendar" : "/beauty");

/** Лише власник змінює налаштування; admin їх бачить (§11). */
export const canEditSettings = (role: Role) => role === "owner";

/** §20.9: ключ API провайдера ШІ бачить і змінює лише власник. */
export const canManageAiKey = (role: Role) => role === "owner";

/** §20.9: налаштування ШІ (моделі, режим, промпт, ліміти) - owner і admin; specialist розділу не бачить. */
export const canEditAiSettings = (role: Role) => role === "owner" || role === "admin";

/**
 * §22.10: inbox розмов, задачі дзвінків і вподобання нагадувань - лише owner/admin; specialist отримує 403 від API
 * ще до читання, тому ці розділи для нього не показуються й не відкриваються.
 */
export const canUseInbox = (role: Role) => role === "owner" || role === "admin";
export const canUseCalls = canUseInbox;
export const canManageReminderPrefs = canUseInbox;
/** Налаштування нагадувань (§22.6.4): читають owner/admin, змінює лише owner. */
export const canEditReminderSettings = (role: Role) => role === "owner";

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
