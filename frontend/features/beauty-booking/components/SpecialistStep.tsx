"use client";

import { useSpecialists } from "../hooks/queries";
import { useBookingFlow } from "../hooks/useBookingFlow";
import { ANY_SPECIALIST_ID } from "../types";
import { OptionCard, StateMessage } from "./ui";

const initialsOf = (name: string) =>
  name
    .split(" ")
    .map((w) => w[0])
    .slice(0, 2)
    .join("");

export function SpecialistStep() {
  const location = useBookingFlow((s) => s.location);
  const selected = useBookingFlow((s) => s.specialist);
  const setSpecialist = useBookingFlow((s) => s.setSpecialist);
  const { data, isLoading, isError, refetch } = useSpecialists(location?.id);

  return (
    <>
      <p className="text-sm text-[#5E5873]">
        {location?.name}: оберіть майстра або дозвольте нам підібрати.
      </p>
      {isLoading && <StateMessage>Завантаження майстрів…</StateMessage>}
      {isError && (
        <StateMessage error>
          Не вдалося завантажити майстрів.{" "}
          <button type="button" className="min-h-[44px] underline" onClick={() => refetch()}>
            Повторити
          </button>
        </StateMessage>
      )}
      {data?.length === 0 && <StateMessage>У цьому закладі зараз немає майстрів для онлайн-запису.</StateMessage>}
      {data?.map((p) => (
        <OptionCard
          key={p.id}
          selected={selected?.id === p.id}
          onClick={() => setSpecialist(p)}
          className="flex items-center gap-3.5"
        >
          <span
            aria-hidden
            className="flex h-12 w-12 flex-none items-center justify-center rounded-full bg-[#EADCF2] font-semibold text-[#4A2260]"
          >
            {p.id === ANY_SPECIALIST_ID ? "★" : initialsOf(p.name)}
          </span>
          <span className="flex flex-col gap-0.5">
            <span className="text-base font-semibold">{p.name}</span>
            {p.role && <span className="text-[13px] text-[#5E5873]">{p.role}</span>}
            {p.nextFree && <span className="text-[13px]">{p.nextFree}</span>}
          </span>
        </OptionCard>
      ))}
    </>
  );
}
