import {
  cancellationPolicy,
  formatDateTime,
  formatDuration,
  formatPrice,
  paymentOptions,
  reminderSummary,
} from "../content";
import type { Appointment } from "../types";

/** Картка підтвердженого запису (екран підтвердження і сторінка запису). */
export function AppointmentCard({ appointment: a }: { appointment: Appointment }) {
  const pay = paymentOptions.find((p) => p.id === a.paymentMethod)?.label;
  return (
    <section className="mt-2 flex flex-col gap-3.5 rounded-[20px] bg-white p-6">
      <div aria-hidden className="flex h-14 w-14 items-center justify-center rounded-full bg-[#6B2F5C]">
        <svg width="28" height="28" viewBox="0 0 24 24" fill="none" stroke="#fff" strokeWidth="2.4" strokeLinecap="round" strokeLinejoin="round">
          <path d="M5 12.5l4.5 4.5L19 7.5" />
        </svg>
      </div>
      <h2 className="font-[family-name:var(--font-unbounded)] text-xl font-semibold">Запис підтверджено</h2>
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
          {formatPrice(a.priceFinal)} · {pay}
        </div>
      </div>
      <div className="border-t border-[#E4DFEC] pt-3 text-sm">{reminderSummary(a.reminderOption)}</div>
      <div className="text-[13px] text-[#5E5873]">Умови скасування: {cancellationPolicy}</div>
    </section>
  );
}
