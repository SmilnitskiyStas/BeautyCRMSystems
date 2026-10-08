"use client";

import { useMemo, useState } from "react";
import { formatDuration } from "../content";
import { dateOptions, longDate } from "../dates";
import { humanizeBookingError } from "../errors";
import { useServices, useSlots } from "../hooks/queries";
import { useBookingFlow } from "../hooks/useBookingFlow";
import { OptionCard, PriceTag, StateMessage } from "./ui";

/** Вибір послуги й часу; дата - на 7 днів уперед (за календарем закладу). */
export function ServiceTimeStep() {
  const { location, specialist, service, slot, setService, setSlot } = useBookingFlow();
  const days = useMemo(() => dateOptions(location?.timezone ?? "UTC", 7), [location?.timezone]);
  const [date, setDate] = useState(days[0].iso);
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
      {services.isError && <StateMessage error>{humanizeBookingError(services.error)}</StateMessage>}
      {services.data?.length === 0 && <StateMessage>Для цього майстра зараз немає послуг для онлайн-запису.</StateMessage>}
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
            Дата та час
          </h2>
          <div role="group" aria-label="Дата запису" className="-mx-1 flex gap-2 overflow-x-auto px-1 pb-1">
            {days.map((d) => {
              const on = d.iso === date;
              return (
                <button
                  key={d.iso}
                  type="button"
                  data-testid="date-option"
                  aria-pressed={on}
                  onClick={() => {
                    setDate(d.iso);
                    setSlot(null);
                  }}
                  className={`flex min-h-[56px] min-w-[68px] flex-none flex-col items-center justify-center rounded-xl border-2 px-2 text-sm font-semibold focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[#6B2F5C] ${
                    on ? "border-[#6B2F5C] bg-[#6B2F5C] text-white" : "border-[#D9D4E4] bg-white text-[#1E1B2E]"
                  }`}
                >
                  <span className="text-xs font-normal capitalize">{d.weekday}</span>
                  <span>{d.label}</span>
                </button>
              );
            })}
          </div>
          <p className="text-[13px] capitalize text-[#5E5873]">{longDate(date)}</p>
          {slots.isLoading && <StateMessage>Шукаємо вільний час…</StateMessage>}
          {slots.isError && <StateMessage error>{humanizeBookingError(slots.error)}</StateMessage>}
          {slots.data?.length === 0 && <StateMessage>На цю дату вільного часу немає. Спробуйте іншу.</StateMessage>}
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
