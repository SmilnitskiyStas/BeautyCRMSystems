import type { LocationInput } from "./types";

export const DEFAULT_TIMEZONE = "Europe/Kyiv";
const SHAPE = /^(UTC|[A-Za-z]+(?:[_-][A-Za-z]+)*(?:\/[A-Za-z0-9_+-]+)+)$/;
const PHONE = /^\+?[\d\s()-]{7,20}$/;

/** IANA-зона: форма `Region/City` (або UTC) і розпізнається середовищем. */
export function isValidTimeZone(tz: string): boolean {
  const v = tz.trim();
  if (!SHAPE.test(v)) return false;
  try {
    new Intl.DateTimeFormat("uk-UA", { timeZone: v });
    return true;
  } catch {
    return false;
  }
}

let cached: string[] | null = null;
/** Усі IANA-зони середовища (для пошуку); `Europe/Kyiv` додається, якщо рушій віддає лише `Europe/Kiev`. */
export function timeZoneOptions(): string[] {
  if (cached) return cached;
  let list: string[] = [];
  try {
    const sv = (Intl as unknown as { supportedValuesOf?: (k: string) => string[] }).supportedValuesOf;
    list = sv ? sv("timeZone") : [];
  } catch {
    list = [];
  }
  if (list.length === 0) list = ["Europe/Kyiv", "Europe/Warsaw", "Europe/London", "Europe/Berlin", "America/New_York", "Pacific/Auckland", "UTC"];
  if (!list.includes(DEFAULT_TIMEZONE)) list = [DEFAULT_TIMEZONE, ...list];
  if (!list.includes("UTC")) list = [...list, "UTC"];
  cached = list;
  return list;
}

export type LocationErrors = Partial<Record<"name" | "address" | "phone" | "timezone", string>>;

export function validateLocation(v: LocationInput): LocationErrors {
  const e: LocationErrors = {};
  const name = v.name.trim();
  if (name.length < 2 || name.length > 100) e.name = "Назва від 2 до 100 символів";
  if (v.address.trim().length > 200) e.address = "Не більше 200 символів";
  if (v.phone.trim() && !PHONE.test(v.phone.trim())) e.phone = "Некоректний номер телефону";
  if (!isValidTimeZone(v.timezone)) e.timezone = "Оберіть зону зі списку, наприклад Europe/Kyiv";
  return e;
}
