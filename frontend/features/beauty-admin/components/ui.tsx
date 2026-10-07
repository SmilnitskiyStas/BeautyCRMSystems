import Link from "next/link";
import type { ButtonHTMLAttributes, ReactNode } from "react";
import type { AppointmentStatus, ClientTag, OverviewKpi } from "../types";

export const TONES = {
  ok: "bg-[#DDF3E6] text-[#145A32]",
  wait: "bg-[#FFF1CC] text-[#7A4B00]",
  now: "bg-[#E3E9FB] text-[#223A8C]",
  vip: "bg-[#EADCF2] text-[#4A2260]",
  neutral: "bg-[#E9E6EF] text-[#4A4560]",
  promo: "bg-[#FDE9DC] text-[#7A2308]",
} as const;
export type Tone = keyof typeof TONES;

export const STATUS_VIEW: Record<AppointmentStatus, { label: string; tone: Tone }> = {
  completed: { label: "Завершено", tone: "ok" },
  in_progress: { label: "У процесі", tone: "now" },
  confirmed: { label: "Підтверджено", tone: "ok" },
  pending: { label: "Очікує підтвердження", tone: "wait" },
  cancelled: { label: "Скасовано", tone: "neutral" },
  no_show: { label: "Не прийшов", tone: "neutral" },
};

export const CLIENT_TAG_VIEW: Record<ClientTag, { label: string; tone: Tone }> = {
  vip: { label: "VIP", tone: "vip" },
  new: { label: "Новий", tone: "ok" },
  sleep: { label: "Давно не був", tone: "wait" },
};

export function Badge({ tone, children, className = "" }: { tone: Tone; children: ReactNode; className?: string }) {
  return (
    <span className={`inline-block rounded-lg px-2.5 py-1 text-[13px] font-semibold ${TONES[tone]} ${className}`}>
      {children}
    </span>
  );
}

export function Card({ children, className = "", as: Tag = "section" }: { children: ReactNode; className?: string; as?: "section" | "div" | "aside" }) {
  return <Tag className={`min-w-0 rounded-2xl bg-white p-5 ${className}`}>{children}</Tag>;
}

export function PageHeader({ title, subtitle, actions }: { title: string; subtitle?: ReactNode; actions?: ReactNode }) {
  return (
    <div className="flex flex-wrap items-center justify-between gap-4">
      <div>
        <h1 className="beauty-display text-[28px] font-semibold">{title}</h1>
        {subtitle ? <div className="mt-1 text-sm text-(--muted)">{subtitle}</div> : null}
      </div>
      {actions ? <div className="flex flex-wrap gap-2">{actions}</div> : null}
    </div>
  );
}

export function KpiGrid({ items, min = 210 }: { items: OverviewKpi[]; min?: number }) {
  return (
    <div className="grid gap-4" style={{ gridTemplateColumns: `repeat(auto-fit, minmax(${min}px, 1fr))` }}>
      {items.map((k) => (
        <div key={k.label} className="flex flex-col gap-1.5 rounded-2xl bg-white p-[18px]">
          <div className="text-[13px] text-(--muted)">{k.label}</div>
          <div className="beauty-display text-2xl font-medium">{k.value}</div>
          <div className="text-[13px] text-(--muted)">{k.note}</div>
        </div>
      ))}
    </div>
  );
}

/** Кнопка-перемикач (pill) з aria-pressed. Висота ≥44px. */
export function Chip({
  pressed,
  onClick,
  children,
  variant = "pill",
}: {
  pressed: boolean;
  onClick: () => void;
  children: ReactNode;
  variant?: "pill" | "soft" | "square";
}) {
  const shape = variant === "pill" ? "rounded-[22px]" : "rounded-xl";
  const on = variant === "soft" ? "border-(--accent) bg-(--tint) text-(--ink)" : "border-(--accent) bg-(--accent) text-white";
  return (
    <button
      type="button"
      aria-pressed={pressed}
      onClick={onClick}
      className={`min-h-11 cursor-pointer border-2 px-4 text-sm font-semibold ${shape} ${pressed ? on : "border-(--line-strong) bg-white text-(--ink)"}`}
    >
      {children}
    </button>
  );
}

export function Tabs<T extends string>({ tabs, value, onChange }: { tabs: { id: T; label: string }[]; value: T; onChange: (id: T) => void }) {
  return (
    <div className="flex flex-wrap gap-2" role="group" aria-label="Розділи">
      {tabs.map((t) => (
        <Chip key={t.id} pressed={value === t.id} onClick={() => onChange(t.id)}>
          {t.label}
        </Chip>
      ))}
    </div>
  );
}

export function Switch({ label, checked, onChange }: { label: string; checked: boolean; onChange: (next: boolean) => void }) {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={checked}
      aria-label={label}
      onClick={() => onChange(!checked)}
      className="flex h-11 w-[52px] flex-none cursor-pointer items-center border-0 bg-transparent p-0"
    >
      <span className={`relative block h-[30px] w-[52px] rounded-full ${checked ? "bg-(--accent)" : "bg-[#6F6985]"}`}>
        <span className={`absolute top-[3px] block h-6 w-6 rounded-full bg-white transition-[left] ${checked ? "left-[25px]" : "left-[3px]"}`} />
      </span>
    </button>
  );
}

export function PrimaryButton({ children, ...rest }: ButtonHTMLAttributes<HTMLButtonElement>) {
  return (
    <button
      type="button"
      {...rest}
      className={`min-h-11 cursor-pointer rounded-[22px] border-0 bg-(--accent) px-5 text-sm font-semibold text-white disabled:cursor-not-allowed disabled:opacity-60 ${rest.className ?? ""}`}
    >
      {children}
    </button>
  );
}

export function SecondaryButton({ children, ...rest }: ButtonHTMLAttributes<HTMLButtonElement>) {
  return (
    <button
      type="button"
      {...rest}
      className={`min-h-11 cursor-pointer rounded-[22px] border border-(--line-strong) bg-white px-5 text-sm font-semibold text-(--ink) disabled:cursor-not-allowed disabled:opacity-60 ${rest.className ?? ""}`}
    >
      {children}
    </button>
  );
}

export function LinkButton({ href, children }: { href: string; children: ReactNode }) {
  return (
    <Link href={href} className="inline-flex min-h-11 items-center rounded-[22px] bg-(--accent) px-5 text-sm font-semibold text-white no-underline">
      {children}
    </Link>
  );
}

/** Текстове посилання з мінімальною зоною натискання 44px. */
export function TextLink({ href, children, className = "" }: { href: string; children: ReactNode; className?: string }) {
  return (
    <Link href={href} className={`inline-flex min-h-11 items-center text-sm font-semibold text-(--accent) underline ${className}`}>
      {children}
    </Link>
  );
}

export function RowLink({ href, children }: { href: string; children: ReactNode }) {
  return (
    <Link href={href} className="inline-flex min-h-11 items-center text-(--ink) underline decoration-(--line-strong) underline-offset-4">
      {children}
    </Link>
  );
}

export function ProgressBar({ pct, thin = false, color = "bg-(--accent)" }: { pct: number; thin?: boolean; color?: string }) {
  return (
    <div className={`${thin ? "h-2.5" : "h-3"} rounded-full bg-(--line)`} role="presentation">
      <div className={`${thin ? "h-2.5" : "h-3"} rounded-full ${color}`} style={{ width: `${pct}%` }} />
    </div>
  );
}

export function BarRow({ label, value, pct, note, color }: { label: string; value: string; pct: number; note?: string; color?: string }) {
  return (
    <div className="flex flex-col gap-1.5">
      <div className="flex justify-between gap-3 text-sm">
        <span className="font-semibold">{label}</span>
        <span>{value}</span>
      </div>
      <ProgressBar pct={pct} color={color} />
      {note ? <div className="text-[13px] text-(--muted)">{note}</div> : null}
    </div>
  );
}

export function KeyValue({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex justify-between gap-3 text-sm">
      <span className="text-(--muted)">{label}</span>
      <span className="text-right font-semibold">{value}</span>
    </div>
  );
}

export function Avatar({ text, size = 40 }: { text: string; size?: number }) {
  return (
    <span
      aria-hidden="true"
      className="flex flex-none items-center justify-center rounded-full bg-[#EADCF2] text-sm font-semibold text-[#4A2260]"
      style={{ width: size, height: size }}
    >
      {text}
    </span>
  );
}

export function Table({ headers, children, minWidth = 560 }: { headers: string[]; children: ReactNode; minWidth?: number }) {
  return (
    <div className="overflow-x-auto">
      <table className="w-full border-collapse text-sm" style={{ minWidth }}>
        <thead>
          <tr className="text-left text-[13px] text-(--muted)">
            {headers.map((h) => (
              <th key={h} scope="col" className="px-2.5 py-2 font-medium">
                {h}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>{children}</tbody>
      </table>
    </div>
  );
}

export const TD = "px-2.5 py-3";

/** Загальні стани завантаження / помилки / порожньо. */
export function QueryState({
  isPending,
  isError,
  onRetry,
  children,
}: {
  isPending: boolean;
  isError: boolean;
  onRetry?: () => void;
  children: ReactNode;
}) {
  if (isPending) {
    return (
      <div role="status" aria-live="polite" className="rounded-2xl bg-white p-6 text-sm text-(--muted)">
        Завантаження…
      </div>
    );
  }
  if (isError) {
    return (
      <div role="alert" className="flex flex-wrap items-center gap-3 rounded-2xl bg-white p-6 text-sm">
        <span>Не вдалося завантажити дані.</span>
        {onRetry ? <SecondaryButton onClick={onRetry}>Спробувати ще раз</SecondaryButton> : null}
      </div>
    );
  }
  return <>{children}</>;
}

export function EmptyState({ children }: { children: ReactNode }) {
  return <div className="text-sm text-(--muted)">{children}</div>;
}

export const FIELD_INPUT =
  "min-h-11 w-full rounded-xl border border-(--line-strong) bg-white px-3.5 text-[15px] text-(--ink)";
export const FIELD_LABEL = "text-[13px] font-semibold";
