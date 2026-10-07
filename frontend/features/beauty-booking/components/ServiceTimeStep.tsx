"use client";

import { useMemo } from "react";
import { formatDuration } from "../content";
import { useServices, useSlots } from "../hooks/queries";
import { useBookingFlow } from "../hooks/useBookingFlow";
import { OptionCard, PriceTag, StateMessage } from "./ui";

function todayLocalIso(): string {
  const d = new Date();
  const p = (n: number) => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`;
}

export function ServiceTimeStep() {
  const { location, specialist, service, slot, setService, setSlot } = useBookingFlow();
  const date = useMemo(() => todayLocalIso(), []);
  const services = useServices(location?.id, specialist?.id);
  const slots = useSlots({
    locationId: location?.id,
    specialistId: specialist?.id,
    serviceId: service?.id,
    date,
  });

  return (
    <>
      <p className="text-sm text-[#5E5873]">Послуга та зручний час.</p>
      {services.isLoading && <StateMessage>Завантаження послуг…</StateMessage>}
      {services.isError && <StateMessage error>Не вдалося завантажити послуги.</StateMessage>}
      {services.data?.map((v) => (
        <OptionCard
          key={v.id}
          selected={service?.id === v.id}
          onClick={() => setService(v)}
          className="flex items-center justify-between gap-3"
        >
          <span className="flex flex-col gap-0.5">
            <span className="text-[15px] font-semibold">{v.name}</span>
            <span className="text-[13px] text-[#5E5873]">{formatDuration(v.durationMinutes)}</span>
            {v.promoLabel && <span className="text-xs font-semibold text-[#A2300A]">{v.promoLabel}</span>}
          </span>
          <PriceTag service={v} />
        </OptionCard>
      ))}

      {service && (
        <section aria-labelledby="slots-heading" className="mt-1 flex flex-col gap-2">
          <h2 id="slots-heading" className="text-sm font-semibold">
            Сьогодні
          </h2>
          {slots.isLoading && <StateMessage>Шукаємо вільний час…</StateMessage>}
          {slots.isError && <StateMessage error>Не вдалося завантажити час.</StateMessage>}
          {slots.data?.length === 0 && <StateMessage>На сьогодні вільного часу немає.</StateMessage>}
          <div className="flex flex-wrap gap-2">
            {slots.data?.map((t) => {
              const on = slot?.startsAt === t.startsAt;
              return (
                <button
                  key={t.startsAt}
                  type="button"
                  aria-pressed={on}
                  onClick={() => setSlot(t)}
                  className={`h-11 min-w-[76px] rounded-xl border-2 text-[15px] font-semibold focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[#6B2F5C] ${
                    on
                      ? "border-[#6B2F5C] bg-[#6B2F5C] text-white"
                      : "border-[#D9D4E4] bg-white text-[#1E1B2E]"
                  }`}
                >
                  {t.label}
                </button>
              );
            })}
          </div>
        </section>
      )}
    </>
  );
}
