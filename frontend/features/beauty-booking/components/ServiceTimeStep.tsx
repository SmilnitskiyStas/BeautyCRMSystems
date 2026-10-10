"use client";

import { useMemo, useState } from "react";
import { formatDuration } from "../content";
import { CLOSED_DAY_HINT, dateOptions, longDate } from "../dates";
import { humanizeBookingError } from "../errors";
import { useServices, useSlots } from "../hooks/queries";
import { useBookingFlow } from "../hooks/useBookingFlow";
import { OptionCard, PriceTag, StateMessage } from "./ui";

const DAYS_AHEAD = 14;

/**
 * Вибір послуги й часу; дата - на 14 днів уперед (за календарем закладу). Закриті дні закладу (§17: щотижневі вихідні
 * й закриття на дати) відключені з підказкою; причину закриття публічний API не віддає.
 */
export function ServiceTimeStep() {
  const { location, specialist, service, slot, setService, setSlot } = useBookingFlow();
  const days = useMemo(
    () =>
      dateOptions(location?.timezone ?? "UTC", DAYS_AHEAD, new Date(), {
        closedWeekdays: location?.closedWeekdays,
        closures: location?.closures,
      }),
    [location?.timezone, location?.closedWeekdays, location?.closures],
  );
  const firstOpen = days.find((d) => !d.closed);
  const [chosen, setDate] = useState<string | null>(null);
  const [closedClicked, setClosedClicked] = useState(false);
  // Вибрана дата не може бути закритою: інакше беремо перший робочий день.
  const date = days.find((d) => d.iso === chosen && !d.closed)?.iso ?? firstOpen?.iso ?? null;
  const services = useServices(location?.id, specialist?.id);
  const slots = useSlots({
    locationId: location?.id,
    specialistId: specialist?.id,
    serviceId: service?.id,
    date: date ?? "",
    enabled: date !== null,
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
                  aria-pressed={d.closed ? undefined : on}
                  // aria-disabled (не disabled): кнопка лишається в порядку табуляції, щоб підказку почув і користувач клавіатури.
                  aria-disabled={d.closed ? true : undefined}
                  title={d.closed ? CLOSED_DAY_HINT : undefined}
                  onClick={() => {
                    if (d.closed) {
                      setClosedClicked(true);
                      return;
                    }
                    setClosedClicked(false);
                    setDate(d.iso);
                    setSlot(null);
                  }}
                  className={`flex min-h-[56px] min-w-[68px] flex-none flex-col items-center justify-center rounded-xl border-2 px-2 text-sm font-semibold focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[#6B2F5C] ${
                    d.closed
                      ? "cursor-not-allowed border-dashed border-[#B9B2C9] bg-[#F1EEF6] text-[#5E5873]"
                      : on
                        ? "border-[#6B2F5C] bg-[#6B2F5C] text-white"
                        : "border-[#D9D4E4] bg-white text-[#1E1B2E]"
                  }`}
                >
                  <span className="text-xs font-normal capitalize">{d.weekday}</span>
                  <span className={d.closed ? "line-through" : undefined}>{d.label}</span>
                  {d.closed ? <span className="sr-only">. {CLOSED_DAY_HINT}</span> : null}
                </button>
              );
            })}
          </div>
          {days.some((d) => d.closed) ? (
            <p role={closedClicked ? "status" : undefined} className="text-[13px] text-[#5E5873]">
              {closedClicked ? `${CLOSED_DAY_HINT}. Оберіть інший день.` : "Дні з перекресленою датою заклад не працює."}
            </p>
          ) : null}
          {date === null ? (
            <StateMessage>У найближчі дні заклад не працює. Спробуйте пізніше або зверніться до закладу.</StateMessage>
          ) : (
            <p className="text-[13px] capitalize text-[#5E5873]">{longDate(date)}</p>
          )}
          {date !== null && slots.isLoading && <StateMessage>Шукаємо вільний час…</StateMessage>}
          {slots.isError && <StateMessage error>{humanizeBookingError(slots.error)}</StateMessage>}
          {date !== null && slots.data?.length === 0 && <StateMessage>На цю дату вільного часу немає. Спробуйте іншу.</StateMessage>}
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
