import { useCallback } from "react";
import { useRouter } from "expo-router";
import { ANY_SPECIALIST_ID } from "@beauty/booking-core";
import type { ResolvedLocation } from "@/lib/locationSource";
import type { LocationRef } from "@/api/catalog";
import { bookingApi, catalogApi, localStore } from "@/services";
import { useDraft } from "@/state/draft";
import { restorableDate, resumeStep } from "@/state/draftPersistence";
import { todayIn } from "@/lib/dates";
import type { BookingDraft } from "@/storage/tokenStore";

export const toResolved = (r: LocationRef): ResolvedLocation => ({
  publicCode: r.publicCode,
  tenantSlug: r.tenantSlug,
  locationId: r.locationId,
  name: r.name,
  address: r.address,
  city: r.city,
  timezone: r.timezone,
  logoUrl: r.logoUrl ?? null,
});

/** Зафіксувати заклад: у чернетку + «остання локація» + історія (лише код, назва, адреса; без координат) -> крок «послуга». */
export function useChooseLocation() {
  const router = useRouter();
  const setLocation = useDraft((d) => d.setLocation);
  return useCallback(
    async (loc: ResolvedLocation, mode: "push" | "replace" = "push") => {
      setLocation(loc);
      await localStore.setLastLocation({ publicCode: loc.publicCode, name: loc.name });
      await localStore
        .addRecentLocation({ publicCode: loc.publicCode, name: loc.name, address: loc.address ?? null, city: loc.city ?? null, logoUrl: loc.logoUrl ?? null })
        .catch(() => undefined);
      if (mode === "replace") router.replace("/service");
      else router.push("/service");
    },
    [router, setLocation],
  );
}

/**
 * «Продовжити запис»: заклад резолвиться заново (актуальні дані), послуга звіряється зі свіжим списком
 * (ціна/тривалість могли змінитися; зникла -> крок «послуга»). Майстер і дата відновлюються, слот - ні.
 */
export function useResumeDraft() {
  const router = useRouter();
  return useCallback(
    async (d: BookingDraft) => {
      const loc = toResolved(await catalogApi.resolve(d.publicCode));
      const st = useDraft.getState();
      st.setLocation(loc);
      await localStore.setLastLocation({ publicCode: loc.publicCode, name: loc.name });
      await localStore
        .addRecentLocation({ publicCode: loc.publicCode, name: loc.name, address: loc.address ?? null, city: loc.city ?? null, logoUrl: loc.logoUrl ?? null })
        .catch(() => undefined);

      const services = await bookingApi.getServices(loc.tenantSlug, loc.locationId, ANY_SPECIALIST_ID);
      const service = services.find((s) => s.id === d.service.id);
      router.push("/service");
      if (!service) return;
      st.setService(service);
      st.setSpecialist(d.specialist);
      const today = todayIn(loc.timezone);
      const date = restorableDate(d.date, today);
      if (date) st.setDate(date);
      router.push("/specialist");
      if (resumeStep(d, today) === "/time") router.push("/time");
    },
    [router],
  );
}
