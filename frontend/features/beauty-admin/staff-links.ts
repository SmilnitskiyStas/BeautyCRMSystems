import type { StaffLocation, StaffMember } from "./types";

/**
 * Зв'язок майстра із закладом (§14.5): `DELETE /specialists/{id}/locations/{locId}` лишає заклад у `locations[]`
 * з `isActive=false` (графік збережено). Знятий зв'язок не є закладом майстра: слотів і записів там немає.
 */
export const isLinkActive = (l: Pick<StaffLocation, "isActive">) => l.isActive !== false;

export const activeLinks = (m: Pick<StaffMember, "locations">): StaffLocation[] => m.locations.filter(isLinkActive);
