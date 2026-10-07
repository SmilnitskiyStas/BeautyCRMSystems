"use client";

import Link from "next/link";
import { useAppointment } from "../hooks/queries";
import { useBookingFlow } from "../hooks/useBookingFlow";
import { AppointmentCard } from "./AppointmentCard";
import { StateMessage } from "./ui";

export function ConfirmationStep() {
  const id = useBookingFlow((s) => s.appointmentId);
  const reset = useBookingFlow((s) => s.reset);
  const { data, isLoading, isError } = useAppointment(id ?? "");

  if (isLoading) return <StateMessage>Завантаження запису…</StateMessage>;
  if (isError || !data) return <StateMessage error>Не вдалося показати запис.</StateMessage>;

  return (
    <>
      <AppointmentCard appointment={data} />
      <Link
        href={`/book/appointment/${data.id}`}
        className="flex min-h-[44px] items-center justify-center rounded-xl bg-[#E3E9FB] px-3.5 py-3 text-sm font-semibold text-[#223A8C] underline"
      >
        Посилання на ваш запис
      </Link>
      <button
        type="button"
        onClick={reset}
        className="min-h-[44px] rounded-xl border-2 border-[#6B2F5C] bg-white text-[15px] font-semibold text-[#6B2F5C]"
      >
        Записатися ще раз
      </button>
    </>
  );
}
