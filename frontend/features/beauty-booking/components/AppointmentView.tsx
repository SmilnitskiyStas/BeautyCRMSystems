"use client";

import Link from "next/link";
import { useAppointment } from "../hooks/queries";
import { AppointmentCard } from "./AppointmentCard";
import { StateMessage } from "./ui";
import { BookingProviders } from "./BookingProviders";

function Inner({ id }: { id: string }) {
  const { data, isLoading, isError } = useAppointment(id);
  return (
    <>
      {isLoading && <StateMessage>Завантаження запису…</StateMessage>}
      {isError && (
        <StateMessage error>Запис не знайдено. Посилання діє в межах поточної сесії (демо).</StateMessage>
      )}
      {data && <AppointmentCard appointment={data} />}
      <Link href="/book" className="flex min-h-[44px] items-center justify-center text-sm font-semibold text-[#6B2F5C] underline">
        Новий запис
      </Link>
    </>
  );
}

export function AppointmentView({ id }: { id: string }) {
  return (
    <BookingProviders>
      <Inner id={id} />
    </BookingProviders>
  );
}
