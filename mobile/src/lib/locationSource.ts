/**
 * Вибір джерела локації (ADR-021 §2). Перше, що збіглося:
 *  1. deep link; 2. збережена остання локація; 3. nearby (геолокація); 4. вручну (місто / код / QR).
 * Збережена локація, що більше не резолвиться (404), очищується, і потік іде далі до nearby.
 */
export type GeoPermission = "undetermined" | "granted" | "denied";

export type LocationSource =
  | { kind: "deeplink"; code: string }
  | { kind: "saved"; code: string }
  | { kind: "nearby" }
  | { kind: "manual" };

export interface SourceInput {
  deepLinkCode: string | null;
  savedCode: string | null;
  geo: GeoPermission;
  /** Користувач натиснув «змінити заклад»: збережену не пропонуємо. */
  changing?: boolean;
}

export function decideLocationSource(i: SourceInput): LocationSource {
  if (i.deepLinkCode) return { kind: "deeplink", code: i.deepLinkCode };
  if (i.savedCode && !i.changing) return { kind: "saved", code: i.savedCode };
  if (i.geo !== "denied") return { kind: "nearby" };
  return { kind: "manual" };
}

export interface ResolvedLocation {
  publicCode: string;
  tenantSlug: string;
  locationId: string;
  name: string;
  address?: string | null;
  city?: string | null;
  timezone: string;
  logoUrl?: string | null;
}

export interface StartDeps {
  resolve(code: string): Promise<ResolvedLocation>;
  clearSaved(): Promise<void>;
  /** 404 для коду. Інші помилки (мережа) - пробрасуються, збережене не чіпаємо. */
  isNotFound(e: unknown): boolean;
}

export type StartOutcome =
  | { kind: "resolved"; location: ResolvedLocation; via: "deeplink" | "saved" }
  | { kind: "nearby" }
  | { kind: "manual" }
  | { kind: "not_found"; code: string };

/** Асинхронна частина: резолвить deep link / збережений код; збережений недійсний -> очистити й перейти до nearby/manual. */
export async function resolveStart(i: SourceInput, deps: StartDeps): Promise<StartOutcome> {
  const src = decideLocationSource(i);
  if (src.kind === "nearby" || src.kind === "manual") return { kind: src.kind };

  try {
    return { kind: "resolved", location: await deps.resolve(src.code), via: src.kind };
  } catch (e) {
    if (!deps.isNotFound(e)) throw e;
    if (src.kind === "deeplink") return { kind: "not_found", code: src.code };
    await deps.clearSaved();
    return resolveStart({ ...i, savedCode: null }, deps);
  }
}

/**
 * L7: deep link, що веде до ІНШОГО закладу, ніж збережений, не перемикає мовчки - потрібне підтвердження.
 * Без збереженого закладу (перший запуск) або з тим самим - підтвердження не потрібне.
 */
export function needsSwitchConfirmation(savedCode: string | null | undefined, linkCode: string): boolean {
  return !!savedCode && savedCode !== linkCode;
}
