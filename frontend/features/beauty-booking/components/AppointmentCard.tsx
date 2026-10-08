import { describeCancellation, formatDateTime, formatDuration, formatPrice, paymentOptions, reminderSummary } from "../content";
import type { Appointment, AppointmentStatus } from "../types";

const TITLE: Record<AppointmentStatus, string> = {
  pending: "Запис створено",
  confirmed: "Запис підтверджено",
  in_progress: "Візит триває",
  completed: "Візит завершено",
  cancelled: "Запис скасовано",
  no_show: "Візит не відбувся",
};

const PAYMENT_STATUS: Record<string, string> = { paid: "Оплачено", refunded: "Кошти повернено" };

/** Картка запису (екран підтвердження і сторінка запису). Умови скасування - з поля `cancellation` API. */
export function AppointmentCard({ appointment: a }: { appointment: Appointment }) {
  const pay = paymentOptions.find((p) => p.id === a.paymentMethod)?.label;
  const payState = a.paymentStatus ? PAYMENT_STATUS[a.paymentStatus] : undefined;
  const active = a.status === "pending" || a.status === "confirmed";
  return (
    <section className="mt-2 flex flex-col gap-3.5 rounded-[20px] bg-white p-6">
      <div aria-hidden className={`flex h-14 w-14 items-center justify-center rounded-full ${a.status === "cancelled" ? "bg-[#8A8499]" : "bg-[#6B2F5C]"}`}>
        <svg width="28" height="28" viewBox="0 0 24 24" fill="none" stroke="#fff" strokeWidth="2.4" strokeLinecap="round" strokeLinejoin="round">
          <path d={a.status === "cancelled" ? "M6 6l12 12M18 6L6 18" : "M5 12.5l4.5 4.5L19 7.5"} />
        </svg>
      </div>
      <h2 className="font-[family-name:var(--font-unbounded)] text-xl font-semibold">{TITLE[a.status]}</h2>
      <div className="flex flex-col gap-2 text-[15px]">
        <div>{a.locationName}</div>
        <div>{a.specialistName}</div>
        <div>{a.serviceName}</div>
        <div>
          {formatDateTime(a.startsAt)} · {formatDuration(a.durationMinutes)}
        </div>
        <div className="font-semibold">
          {a.priceOriginal > a.priceFinal && (
            <span className="mr-2 font-normal text-[#5E5873] line-through">{formatPrice(a.priceOriginal)}</span>
          )}
          {formatPrice(a.priceFinal)}
          {pay && ` · ${pay}`}
          {payState && ` · ${payState}`}
        </div>
      </div>
      {active && <div className="border-t border-[#E4DFEC] pt-3 text-sm">{reminderSummary(a.reminderOption)}</div>}
      <div className="flex flex-col text-[13px] text-[#5E5873]">
        <span className="font-semibold">Умови скасування</span>
        {describeCancellation(a.cancellation).map((l) => (
          <span key={l}>{l}</span>
        ))}
      </div>
    </section>
  );
}
