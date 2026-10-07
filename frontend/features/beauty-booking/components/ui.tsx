import type { ReactNode } from "react";
import { formatPrice } from "../content";
import type { Service } from "../types";

export function OptionCard({
  selected,
  onClick,
  children,
  className = "",
}: {
  selected: boolean;
  onClick: () => void;
  children: ReactNode;
  className?: string;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      aria-pressed={selected}
      className={`min-h-[44px] w-full rounded-2xl border-2 bg-white p-4 text-left text-[#1E1B2E] focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[#6B2F5C] ${
        selected ? "border-[#6B2F5C]" : "border-[#D9D4E4]"
      } ${className}`}
    >
      {children}
    </button>
  );
}

export function PromoBadge({ children }: { children: ReactNode }) {
  return (
    <span className="inline-block rounded-lg bg-[#FDE9DC] px-2 py-1 text-xs font-semibold text-[#A2300A]">
      {children}
    </span>
  );
}

/** Ціна: для акційних послуг — нова ціна й закреслена стара. */
export function PriceTag({ service }: { service: Pick<Service, "priceFinal" | "priceOriginal"> }) {
  const promo = service.priceOriginal > service.priceFinal;
  return (
    <span className="flex flex-none flex-col items-end">
      <span className="text-base font-semibold">
        <span className="sr-only">{promo ? "Нова ціна " : "Ціна "}</span>
        {formatPrice(service.priceFinal)}
      </span>
      {promo && (
        <span className="text-[13px] text-[#5E5873] line-through">
          <span className="sr-only">Стара ціна </span>
          {formatPrice(service.priceOriginal)}
        </span>
      )}
    </span>
  );
}

export function StateMessage({ children, error }: { children: ReactNode; error?: boolean }) {
  return (
    <p role={error ? "alert" : "status"} className={`text-sm ${error ? "text-[#A2300A]" : "text-[#5E5873]"}`}>
      {children}
    </p>
  );
}

export function SummaryRow({ label, value, strong }: { label: string; value: ReactNode; strong?: boolean }) {
  return (
    <div className={`flex justify-between gap-3 ${strong ? "text-base" : "text-sm"}`}>
      <span className={strong ? "font-semibold" : "text-[#5E5873]"}>{label}</span>
      <span className="text-right font-semibold">{value}</span>
    </div>
  );
}
