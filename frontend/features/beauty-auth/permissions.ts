import type { Role } from "./types";

export interface NavItem {
  label: string;
  href: string;
}

const ALL_NAV: NavItem[] = [
  { label: "Огляд", href: "/beauty" },
  { label: "Записи", href: "/beauty/calendar" },
  { label: "Клієнти", href: "/beauty/clients" },
  { label: "Спеціалісти", href: "/beauty/staff" },
  { label: "Ціни та акції", href: "/beauty/promos" },
  { label: "Аналітика", href: "/beauty/analytics" },
  { label: "AI-асистент", href: "/beauty/ai" },
  { label: "Канали", href: "/beauty/channels" },
  { label: "Налаштування", href: "/beauty/settings" },
];

/** Узгоджено з матрицею ролей §10: specialist — лише свій календар; clients/analytics/channels/ai — owner/admin. */
const SPECIALIST_PATHS = ["/beauty/calendar"];

export function navFor(role: Role): NavItem[] {
  return role === "specialist" ? ALL_NAV.filter((n) => SPECIALIST_PATHS.includes(n.href)) : ALL_NAV;
}

export function canOpen(role: Role, pathname: string): boolean {
  if (role !== "specialist") return true;
  return SPECIALIST_PATHS.some((p) => pathname === p || pathname.startsWith(`${p}/`));
}

export const homeFor = (role: Role) => (role === "specialist" ? "/beauty/calendar" : "/beauty");

/** Лише власник змінює налаштування; admin їх бачить (§11). */
export const canEditSettings = (role: Role) => role === "owner";

/** Дозволяє тільки відносні шляхи в межах сайту (захист від open redirect). */
export function safeNext(next: string | null | undefined, fallback = "/beauty"): string {
  if (!next || !next.startsWith("/") || next.startsWith("//") || next.includes("\\")) return fallback;
  return next;
}
