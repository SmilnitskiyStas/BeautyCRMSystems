"use client";

import { useState } from "react";
import { formatPrice } from "../content";
import { humanizeBookingError } from "../errors";
import { useCheckCoupon } from "../hooks/queries";
import { useBookingFlow } from "../hooks/useBookingFlow";

/** Загальний промокод закладу: 4..20 літер/цифр; пробіли й дефіси всередині ігноруються сервером. */
const FORMAT = /^[A-Za-z0-9][A-Za-z0-9 -]{2,22}[A-Za-z0-9]$/;
const inputCls =
  "h-[52px] min-w-0 flex-1 rounded-[14px] border-2 border-[#D9D4E4] bg-white px-3.5 text-[15px] uppercase text-[#1E1B2E] focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[#6B2F5C]";

/**
 * Поле «Промокод» (§26.11.4, лише загальні коди). Перевірка повертає тільки ціну: чому код не підійшов, не уточнюється
 * (однакова відповідь для всіх причин), про персональні коди форма нічого не обіцяє.
 */
export function CouponField() {
  const location = useBookingFlow((s) => s.location);
  const service = useBookingFlow((s) => s.service);
  const code = useBookingFlow((s) => s.couponCode);
  const coupon = useBookingFlow((s) => s.coupon);
  const setCode = useBookingFlow((s) => s.setCouponCode);
  const setCoupon = useBookingFlow((s) => s.setCoupon);
  const check = useCheckCoupon();
  const [error, setError] = useState<string | null>(null);
  const trimmed = code.trim();

  function run() {
    if (!location || !service || check.isPending) return;
    if (!FORMAT.test(trimmed)) {
      setError("Перевірте код: від 4 до 20 літер і цифр.");
      return;
    }
    setError(null);
    check.mutate(
      { code: trimmed, locationId: location.id, serviceId: service.id },
      {
        onSuccess: (result) => setCoupon({ code: trimmed, result }),
        onError: (e) => {
          setCoupon(null);
          setError(humanizeBookingError(e, "Не вдалося перевірити код. Спробуйте ще раз."));
        },
      },
    );
  }

  const r = coupon && coupon.code === trimmed ? coupon.result : null;
  return (
    <div className="flex flex-col gap-1.5">
      <label htmlFor="coupon-code" className="text-sm font-semibold">
        Промокод (необов&apos;язково)
      </label>
      <div className="flex gap-2">
        <input
          id="coupon-code"
          type="text"
          autoComplete="off"
          autoCapitalize="characters"
          spellCheck={false}
          maxLength={24}
          value={code}
          onChange={(e) => {
            setError(null);
            setCode(e.target.value);
          }}
          onKeyDown={(e) => {
            if (e.key === "Enter") {
              e.preventDefault();
              run();
            }
          }}
          aria-describedby="coupon-hint coupon-status"
          aria-invalid={error ? "true" : "false"}
          className={inputCls}
        />
        <button
          type="button"
          onClick={run}
          disabled={trimmed === "" || check.isPending}
          className="min-h-[52px] rounded-[14px] border-2 border-[#6B2F5C] bg-white px-4 text-[15px] font-semibold text-[#6B2F5C] focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[#6B2F5C] disabled:cursor-not-allowed disabled:opacity-60"
        >
          {check.isPending ? "Перевіряємо…" : "Перевірити"}
        </button>
      </div>
      <p id="coupon-hint" className="text-[13px] text-[#5E5873]">
        Є промокод закладу? Введіть його, і ми перевіримо знижку перед записом.
      </p>
      <div id="coupon-status" aria-live="polite" data-testid="coupon-status">
        {error ? (
          <p role="alert" className="text-[13px] font-semibold text-[#A2300A]">
            {error}
          </p>
        ) : r?.outcome === "applied" ? (
          <p className="text-[13px] font-semibold text-[#145A32]">
            Промокод застосовано: {formatPrice(r.priceFinal)} замість {formatPrice(r.priceOriginal)}.
          </p>
        ) : r?.outcome === "not_better" ? (
          <p className="text-[13px] text-[#5E5873]">Промокод не змінить ціну: діє вигідніша знижка.</p>
        ) : r?.outcome === "not_applicable" ? (
          <p className="text-[13px] text-[#5E5873]">Цей промокод не діє на обрану послугу.</p>
        ) : null}
      </div>
    </div>
  );
}
