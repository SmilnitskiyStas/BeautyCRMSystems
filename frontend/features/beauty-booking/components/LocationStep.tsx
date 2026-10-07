"use client";

import { useLocations } from "../hooks/queries";
import { useBookingFlow } from "../hooks/useBookingFlow";
import { OptionCard, PromoBadge, StateMessage } from "./ui";

export function LocationStep() {
  const { data, isLoading, isError, refetch } = useLocations();
  const selected = useBookingFlow((s) => s.location);
  const setLocation = useBookingFlow((s) => s.setLocation);

  return (
    <>
      <p className="text-sm text-[#5E5873]">Оберіть заклад мережі, у який хочете записатися.</p>
      {isLoading && <StateMessage>Завантаження закладів…</StateMessage>}
      {isError && (
        <StateMessage error>
          Не вдалося завантажити заклади.{" "}
          <button type="button" className="min-h-[44px] underline" onClick={() => refetch()}>
            Повторити
          </button>
        </StateMessage>
      )}
      {data?.length === 0 && <StateMessage>Закладів поки немає.</StateMessage>}
      {data?.map((l) => (
        <OptionCard key={l.id} selected={selected?.id === l.id} onClick={() => setLocation(l)} className="flex flex-col gap-1.5">
          <span className="text-base font-semibold">{l.name}</span>
          <span className="text-sm text-[#5E5873]">{l.address}</span>
          <span className="flex items-center justify-between text-[13px] text-[#5E5873]">
            <span>{l.hours}</span>
            {l.hasPromo && <PromoBadge>Акція</PromoBadge>}
          </span>
        </OptionCard>
      ))}
    </>
  );
}
