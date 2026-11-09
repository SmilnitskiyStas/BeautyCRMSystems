import { useCallback } from "react";
import { useRouter } from "expo-router";
import type { ResolvedLocation } from "@/lib/locationSource";
import type { LocationRef } from "@/api/catalog";
import { localStore } from "@/services";
import { useDraft } from "@/state/draft";

export const toResolved = (r: LocationRef): ResolvedLocation => ({
  publicCode: r.publicCode,
  tenantSlug: r.tenantSlug,
  locationId: r.locationId,
  name: r.name,
  address: r.address,
  city: r.city,
  timezone: r.timezone,
});

/** Зафіксувати заклад: у чернетку + як «остання локація» (лише код і назва, без координат) -> крок «послуга». */
export function useChooseLocation() {
  const router = useRouter();
  const setLocation = useDraft((d) => d.setLocation);
  return useCallback(
    async (loc: ResolvedLocation, mode: "push" | "replace" = "push") => {
      setLocation(loc);
      await localStore.setLastLocation({ publicCode: loc.publicCode, name: loc.name });
      if (mode === "replace") router.replace("/service");
      else router.push("/service");
    },
    [router, setLocation],
  );
}
