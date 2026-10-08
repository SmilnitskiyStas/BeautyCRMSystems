"use client";

import { useState } from "react";
import { formatPrice, stepTitles } from "../content";
import { useCreateAppointment } from "../hooks/queries";
import { useBookingFlow } from "../hooks/useBookingFlow";
import { humanizeBookingError, isSlotLost } from "../errors";
import { clientSchema } from "../types";
import { BookingProviders } from "./BookingProviders";
import { CheckoutStep, type ClientErrors } from "./CheckoutStep";
import { ConfirmationStep } from "./ConfirmationStep";
import { LocationStep } from "./LocationStep";
import { ServiceTimeStep } from "./ServiceTimeStep";
import { SpecialistStep } from "./SpecialistStep";

function Flow() {
  const flow = useBookingFlow();
  const { step, location, specialist, service, slot, client, reminder, paymentMethod, website } = flow;
  const create = useCreateAppointment();
  const [errors, setErrors] = useState<ClientErrors>({});
  const [submitError, setSubmitError] = useState<string>();

  const ready =
    (step === 1 && !!location) ||
    (step === 2 && !!specialist) ||
    (step === 3 && !!service && !!slot) ||
    step === 4;

  const ctaLabel =
    step === 3
      ? "До оформлення"
      : step === 4
        ? create.isPending
          ? "Зачекайте…"
          : paymentMethod === "card" && service
            ? `Оплатити ${formatPrice(service.priceFinal)}`
            : "Підтвердити запис"
        : "Далі";

  async function onNext() {
    if (!ready || create.isPending) return;
    if (step < 4) return flow.next();

    setSubmitError(undefined);
    const parsed = clientSchema.safeParse(client);
    if (!parsed.success) {
      const f = parsed.error.flatten().fieldErrors;
      setErrors({ name: f.name?.[0], phone: f.phone?.[0] });
      return;
    }
    setErrors({});
    if (!location || !specialist || !service || !slot) return;
    try {
      const a = await create.mutateAsync({
        locationId: location.id,
        specialistId: slot.specialistId,
        serviceId: service.id,
        startsAt: slot.startsAt,
        client: parsed.data,
        reminder,
        paymentMethod,
        website,
      });
      flow.done(a);
    } catch (e) {
      setSubmitError(humanizeBookingError(e, "Не вдалося створити запис. Спробуйте ще раз."));
      if (isSlotLost(e)) {
        flow.setSlot(null);
        flow.back();
      }
    }
  }

  return (
    <div className="mx-auto flex min-h-dvh w-full max-w-[480px] flex-col bg-[#F6F4F8] text-[#1E1B2E]">
      <header className="flex flex-col gap-3.5 px-5 pb-3 pt-5">
        <div className="flex min-h-[44px] items-center gap-3">
          {step > 1 && step < 5 && (
            <button
              type="button"
              aria-label="Назад"
              onClick={flow.back}
              className="flex h-11 w-11 flex-none items-center justify-center rounded-full border border-[#D9D4E4] bg-white"
            >
              <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="#1E1B2E" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden>
                <path d="M15 6l-6 6 6 6" />
              </svg>
            </button>
          )}
          <h1 className="font-[family-name:var(--font-unbounded)] text-xl font-semibold leading-tight">
            {stepTitles[step]}
          </h1>
        </div>
        {step < 5 && (
          <div className="flex gap-1.5" role="progressbar" aria-valuemin={1} aria-valuemax={4} aria-valuenow={step} aria-label="Крок запису">
            {[1, 2, 3, 4].map((n) => (
              <div key={n} className={`h-1 flex-1 rounded-sm ${step >= n ? "bg-[#6B2F5C]" : "bg-[#E4DFEC]"}`} />
            ))}
          </div>
        )}
      </header>

      <main className="flex flex-1 flex-col gap-3 px-5 pb-4 pt-1">
        {step === 1 && <LocationStep />}
        {step === 2 && <SpecialistStep />}
        {step === 3 && <ServiceTimeStep />}
        {step === 4 && <CheckoutStep errors={errors} submitError={submitError} />}
        {step === 5 && <ConfirmationStep />}
      </main>

      {step < 5 && (
        <footer className="sticky bottom-0 flex flex-col gap-2.5 border-t border-[#E4DFEC] bg-white px-5 pb-6 pt-3">
          {step === 3 && service && (
            <div className="flex justify-between text-sm">
              <span className="text-[#5E5873]">Вартість</span>
              <span className="font-semibold">{formatPrice(service.priceFinal)}</span>
            </div>
          )}
          <button
            type="button"
            onClick={onNext}
            disabled={!ready || create.isPending}
            className="h-[52px] rounded-full bg-[#6B2F5C] text-base font-semibold text-white focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[#6B2F5C] disabled:cursor-not-allowed disabled:opacity-60"
          >
            {ctaLabel}
          </button>
        </footer>
      )}
    </div>
  );
}

export function BookingFlow({ tenant }: { tenant: string }) {
  return (
    <BookingProviders tenant={tenant}>
      <Flow />
    </BookingProviders>
  );
}
