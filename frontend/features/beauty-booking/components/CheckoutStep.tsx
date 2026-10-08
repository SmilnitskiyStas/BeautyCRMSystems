"use client";

import {
  describeCancellation,
  formatDateTime,
  formatDuration,
  formatPrice,
  paymentOptions,
  paymentPolicy,
  reminderOptions,
} from "../content";
import { useBookingFlow } from "../hooks/useBookingFlow";
import type { ReminderOption } from "../types";
import { OptionCard, PriceTag, SummaryRow } from "./ui";

export interface ClientErrors {
  name?: string;
  phone?: string;
}

const inputCls =
  "h-[52px] w-full rounded-[14px] border-2 border-[#D9D4E4] bg-white px-3.5 text-[15px] text-[#1E1B2E] focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[#6B2F5C]";

export function CheckoutStep({ errors, submitError }: { errors: ClientErrors; submitError?: string }) {
  const { location, specialist, service, slot, reminder, paymentMethod, client } = useBookingFlow();
  const setReminder = useBookingFlow((s) => s.setReminder);
  const setPaymentMethod = useBookingFlow((s) => s.setPaymentMethod);
  const setClient = useBookingFlow((s) => s.setClient);
  const website = useBookingFlow((s) => s.website);
  const setWebsite = useBookingFlow((s) => s.setWebsite);
  if (!service || !slot) return null;

  return (
    <>
      <section className="flex flex-col gap-2.5 rounded-2xl bg-white p-4" aria-label="Ваш запис">
        <h2 className="text-base font-semibold">Ваш запис</h2>
        <SummaryRow label="Заклад" value={location?.name} />
        <SummaryRow label="Спеціаліст" value={specialist?.name} />
        <SummaryRow label="Послуга" value={service.name} />
        <SummaryRow
          label="Час"
          value={`${formatDateTime(slot.startsAt)} · ${formatDuration(service.durationMinutes)}`}
        />
        <div className="flex items-center justify-between border-t border-[#E4DFEC] pt-2.5">
          <span className="text-base font-semibold">Разом</span>
          <PriceTag service={service} />
        </div>
        {service.promoLabel && (
          <span className="text-xs font-semibold text-[#A2300A]">{service.promoLabel}</span>
        )}
      </section>

      <div className="flex flex-col gap-1.5">
        <label htmlFor="client-name" className="text-sm font-semibold">
          Імʼя
        </label>
        <input
          id="client-name"
          autoComplete="name"
          value={client.name}
          onChange={(e) => setClient({ name: e.target.value })}
          aria-invalid={!!errors.name}
          aria-describedby={errors.name ? "client-name-err" : undefined}
          className={inputCls}
        />
        {errors.name && (
          <span id="client-name-err" role="alert" className="text-[13px] text-[#A2300A]">
            {errors.name}
          </span>
        )}
        <label htmlFor="client-phone" className="mt-1 text-sm font-semibold">
          Телефон
        </label>
        <input
          id="client-phone"
          type="tel"
          inputMode="tel"
          autoComplete="tel"
          placeholder="+380"
          value={client.phone}
          onChange={(e) => setClient({ phone: e.target.value })}
          aria-invalid={!!errors.phone}
          aria-describedby={errors.phone ? "client-phone-err" : undefined}
          className={inputCls}
        />
        {errors.phone && (
          <span id="client-phone-err" role="alert" className="text-[13px] text-[#A2300A]">
            {errors.phone}
          </span>
        )}
      </div>

      {/* Honeypot: невидиме для людей й допоміжних технологій поле; боти його заповнюють - сервер відхиляє запис. */}
      <div aria-hidden="true" className="absolute -left-[9999px] h-0 w-0 overflow-hidden">
        <label htmlFor="website">Website</label>
        <input
          id="website"
          name="website"
          type="text"
          tabIndex={-1}
          autoComplete="off"
          value={website}
          onChange={(e) => setWebsite(e.target.value)}
        />
      </div>

      <label htmlFor="remind" className="mt-1 text-sm font-semibold">
        Нагадати про запис
      </label>
      <select
        id="remind"
        value={reminder}
        onChange={(e) => setReminder(e.target.value as ReminderOption)}
        className={`${inputCls} font-semibold`}
      >
        {reminderOptions.map((r) => (
          <option key={r.id} value={r.id}>
            {r.label}
          </option>
        ))}
      </select>

      <div role="group" aria-labelledby="pay-heading" className="mt-1 flex flex-col gap-2">
        <div id="pay-heading" className="text-sm font-semibold">
          Спосіб оплати
        </div>
        {paymentOptions.map((p) => (
          <OptionCard
            key={p.id}
            selected={paymentMethod === p.id}
            onClick={() => setPaymentMethod(p.id)}
            className="flex min-h-14 flex-col gap-0.5 !rounded-[14px] !p-3"
          >
            <span className="text-[15px] font-semibold">{p.label}</span>
            <span className="text-[13px] text-[#5E5873]">{p.note}</span>
          </OptionCard>
        ))}
      </div>

      <div className="flex flex-col gap-1 rounded-xl bg-[#FFF1CC] px-3.5 py-3 text-[13px] leading-snug text-[#5C3900]">
        <span className="font-semibold">Умови скасування</span>
        {describeCancellation(slot.cancellation).map((line) => (
          <span key={line}>{line}</span>
        ))}
        <span>{paymentPolicy[paymentMethod]}</span>
      </div>

      {submitError && (
        <p role="alert" className="text-sm font-semibold text-[#A2300A]">
          {submitError}
        </p>
      )}
      <span className="sr-only">Разом до сплати {formatPrice(service.priceFinal)}</span>
    </>
  );
}
