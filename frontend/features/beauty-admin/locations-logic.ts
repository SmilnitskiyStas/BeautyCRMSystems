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

export type LocationErrors = Partial<Record<"name" | "address" | "phone" | "timezone" | "latitude" | "longitude", string>>;

export interface ParsedCoordinates {
  /** Обидва `null` = координати не задано (для PUT це «очистити»). */
  latitude: number | null;
  longitude: number | null;
  errors: Pick<LocationErrors, "latitude" | "longitude">;
}

const DECIMAL = /^[+-]?(\d+([.,]\d*)?|[.,]\d+)$/;

function parseCoordinate(raw: string, min: number, max: number, rangeMsg: string): { value: number | null; error?: string } {
  const text = raw.trim();
  if (text === "") return { value: null };
  if (!DECIMAL.test(text)) return { value: null, error: "Вкажіть число, наприклад 50.4501" };
  const n = Number(text.replace(",", "."));
  if (!Number.isFinite(n) || n < min || n > max) return { value: null, error: rangeMsg };
  return { value: Math.round(n * 1e6) / 1e6 };
}

/** Широта/довгота (§28.11): необов'язкові, лише парою; кома або крапка; округлення до 6 знаків. */
export function parseCoordinates(latRaw: string, lonRaw: string): ParsedCoordinates {
  const lat = parseCoordinate(latRaw, -90, 90, "Широта від −90 до 90");
  const lon = parseCoordinate(lonRaw, -180, 180, "Довгота від −180 до 180");
  const errors: ParsedCoordinates["errors"] = {};
  if (lat.error) errors.latitude = lat.error;
  if (lon.error) errors.longitude = lon.error;
  if (!errors.latitude && !errors.longitude) {
    if (lat.value !== null && lon.value === null) errors.longitude = "Вкажіть довготу або очистіть широту";
    if (lon.value !== null && lat.value === null) errors.latitude = "Вкажіть широту або очистіть довготу";
  }
  const ok = Object.keys(errors).length === 0;
  return { latitude: ok ? lat.value : null, longitude: ok ? lon.value : null, errors };
}

/** Число з API -> текст поля (крапка). */
export const coordinateText = (v: number | null | undefined): string => (typeof v === "number" && Number.isFinite(v) ? String(v) : "");

export function validateLocation(v: LocationInput): LocationErrors {
  const e: LocationErrors = {};
  const name = v.name.trim();
  if (name.length < 2 || name.length > 100) e.name = "Назва від 2 до 100 символів";
  if (v.address.trim().length > 200) e.address = "Не більше 200 символів";
  if (v.phone.trim() && !PHONE.test(v.phone.trim())) e.phone = "Некоректний номер телефону";
  if (!isValidTimeZone(v.timezone)) e.timezone = "Оберіть зону зі списку, наприклад Europe/Kyiv";
  return e;
}
