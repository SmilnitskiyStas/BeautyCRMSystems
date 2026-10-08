"use client";

import Link from "next/link";
import { useState } from "react";
import { describeCancellation, estimateRefundPercent, formatPrice } from "../content";
import { humanizeBookingError } from "../errors";
import { useAppointment, useCancelAppointment } from "../hooks/queries";
import { bookHref } from "../tenant";
import type { CancelResult } from "../types";
import { AppointmentCard } from "./AppointmentCard";
import { BookingProviders } from "./BookingProviders";
import { StateMessage } from "./ui";

function Inner({ token, tenant }: { token: string; tenant: string }) {
  const { data, isLoading, isError, error } = useAppointment(token);
  const cancel = useCancelAppointment(token);
  const [confirming, setConfirming] = useState(false);
  const [result, setResult] = useState<CancelResult | null>(null);

  const canCancel = !!data && (data.status === "pending" || data.status === "confirmed");
  const paid = data?.paymentStatus === "paid";
  const estimate = data?.cancellation ? estimateRefundPercent(data.cancellation, data.startsAt) : 0;

  async function doCancel() {
    try {
      setResult(await cancel.mutateAsync());
      setConfirming(false);
    } catch {
      /* текст помилки показуємо з cancel.error */
    }
  }

  return (
    <>
      {isLoading && <StateMessage>Завантаження запису…</StateMessage>}
      {isError && <StateMessage error>{humanizeBookingError(error)}</StateMessage>}
      {data && <AppointmentCard appointment={data} />}

      {result && (
        <p role="status" className="rounded-xl bg-[#E6F4EA] px-3.5 py-3 text-sm font-semibold text-[#14532D]">
          Запис скасовано.{" "}
          {result.refundAmount > 0
            ? `Повертаємо ${formatPrice(result.refundAmount)} (${result.refundPercent}%${result.feePercent > 0 ? `, комісія ${result.feePercent}%` : ""}).`
            : "Повернення коштів не передбачено."}
        </p>
      )}

      {canCancel && !confirming && (
        <button
          type="button"
          onClick={() => setConfirming(true)}
          className="min-h-[44px] rounded-xl border-2 border-[#A2300A] bg-white text-[15px] font-semibold text-[#A2300A]"
        >
          Скасувати запис
        </button>
      )}

      {canCancel && confirming && data && (
        <section aria-label="Підтвердження скасування" className="flex flex-col gap-2 rounded-xl bg-[#FFF1CC] p-3.5 text-[13px] leading-snug text-[#5C3900]">
          <span className="font-semibold">Скасувати запис?</span>
          {describeCancellation(data.cancellation).map((l) => (
            <span key={l}>{l}</span>
          ))}
          {paid && data.cancellation && (
            <span className="font-semibold">
              Якщо скасувати зараз, повернемо приблизно {estimate}% (орієнтовно{" "}
              {formatPrice(Math.floor((data.priceFinal * estimate) / 100))}).
            </span>
          )}
          {cancel.isError && (
            <span role="alert" className="font-semibold text-[#A2300A]">
              {humanizeBookingError(cancel.error)}
            </span>
          )}
          <div className="mt-1 flex gap-2">
            <button
              type="button"
              onClick={doCancel}
              disabled={cancel.isPending}
              className="min-h-[44px] flex-1 rounded-xl bg-[#A2300A] text-[15px] font-semibold text-white disabled:opacity-60"
            >
              {cancel.isPending ? "Зачекайте…" : "Так, скасувати"}
            </button>
            <button
              type="button"
              onClick={() => setConfirming(false)}
              className="min-h-[44px] flex-1 rounded-xl border-2 border-[#6B2F5C] bg-white text-[15px] font-semibold text-[#6B2F5C]"
            >
              Не скасовувати
            </button>
          </div>
        </section>
      )}

      <Link href={bookHref(tenant)} className="flex min-h-[44px] items-center justify-center text-sm font-semibold text-[#6B2F5C] underline">
        Новий запис
      </Link>
    </>
  );
}

export function AppointmentView({ token, tenant }: { token: string; tenant: string }) {
  return (
    <BookingProviders tenant={tenant}>
      <Inner token={token} tenant={tenant} />
    </BookingProviders>
  );
}
