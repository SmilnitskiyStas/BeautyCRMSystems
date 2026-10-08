"use client";

import Link from "next/link";
import { useBookingFlow } from "../hooks/useBookingFlow";
import { appointmentHref } from "../tenant";
import { AppointmentCard } from "./AppointmentCard";
import { useTenant } from "./tenant-context";
import { StateMessage } from "./ui";

/** Підтвердження: дані беремо з відповіді створення (без повторного GET); токен лише в посиланні на запис. */
export function ConfirmationStep() {
  const tenant = useTenant();
  const booking = useBookingFlow((s) => s.booking);
  const reset = useBookingFlow((s) => s.reset);

  if (!booking) return <StateMessage error>Не вдалося показати запис.</StateMessage>;

  return (
    <>
      <AppointmentCard appointment={booking.appointment} />
      <Link
        href={appointmentHref(tenant, booking.token)}
        className="flex min-h-[44px] items-center justify-center rounded-xl bg-[#E3E9FB] px-3.5 py-3 text-sm font-semibold text-[#223A8C] underline"
      >
        Посилання на ваш запис
      </Link>
      <p className="text-[13px] text-[#5E5873]">
        Збережіть це посилання: за ним можна переглянути або скасувати запис. Нікому його не передавайте.
      </p>
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
