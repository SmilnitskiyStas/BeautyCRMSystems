"use client";

import { useRef, useState, type KeyboardEvent } from "react";
import { thousands, pointFullLabel, pointLabel } from "../analytics/series";
import { PRESETS, validateRange, type DateRange, type RangePreset, type RangeState } from "../analytics/ranges";
import type { DeltaView } from "../analytics/delta";
import type { AnalyticsKpi } from "../analytics/kpis";
import { moneyExact } from "../format";
import type { NetworkSeries, SeriesPoint } from "../types";
import { FIELD_INPUT, FIELD_LABEL, PrimaryButton, SecondaryButton } from "./ui";

/** Текст дельти: напрямок передає і стрілка, і знак числа, і текст для зчитувачів (не лише колір, §19.3). */
export function DeltaText({ view }: { view: DeltaView | null }) {
  if (!view) return null;
  const color = view.tone === "good" ? "text-[#145A32]" : view.tone === "bad" ? "text-[#9B1C1C]" : "text-(--muted)";
  return (
    <span className={`text-[13px] font-semibold ${color}`} data-tone={view.tone}>
      <span aria-hidden="true">{view.text}</span>
      <span className="sr-only">{view.sr}</span>
    </span>
  );
}

export function AnalyticsKpiGrid({ items }: { items: AnalyticsKpi[] }) {
  return (
    <div className="grid gap-4" style={{ gridTemplateColumns: "repeat(auto-fit, minmax(min(210px, 100%), 1fr))" }}>
      {items.map((k) => (
        <div key={k.label} className="flex flex-col gap-1.5 rounded-2xl bg-white p-[18px]">
          <div className="text-[13px] text-(--muted)">{k.label}</div>
          <div className="beauty-display text-2xl font-medium">{k.value}</div>
          {k.delta ? <DeltaText view={k.delta} /> : null}
          {k.note ? <div className="text-[13px] text-(--muted)">{k.note}</div> : null}
        </div>
      ))}
    </div>
  );
}

export function ExportButton({ disabled, onClick }: { disabled: boolean; onClick: () => void }) {
  return (
    <SecondaryButton disabled={disabled} onClick={onClick}>
      Експорт CSV
    </SecondaryButton>
  );
}

// ---------- перемикач діапазону ----------

interface RangeSwitcherProps {
  state: RangeState;
  /** Фактичний діапазон (для підстановки у «Власний від–до»). */
  resolved: DateRange | null;
  onChange: (next: RangeState) => void;
}

/**
 * Перемикач діапазону: `radiogroup` (стрілки/Home/End переміщують вибір, Tab - у групу й з неї), працює на 320 px (перенос рядків).
 * «Власний від–до»: два поля дати з валідацією (`from <= to`, до 366 днів) і зрозумілою помилкою.
 */
export function RangeSwitcher({ state, resolved, onChange }: RangeSwitcherProps) {
  const refs = useRef<(HTMLButtonElement | null)[]>([]);
  const select = (preset: RangePreset) => {
    if (preset === "custom") {
      const base = state.custom ?? resolved;
      onChange({ ...state, preset, custom: base ? { ...base } : null });
    } else onChange({ ...state, preset, custom: null });
  };

  function onKey(e: KeyboardEvent<HTMLDivElement>) {
    const i = PRESETS.findIndex((p) => p.id === state.preset);
    const last = PRESETS.length - 1;
    const next = e.key === "ArrowRight" || e.key === "ArrowDown" ? (i + 1) % PRESETS.length : e.key === "ArrowLeft" || e.key === "ArrowUp" ? (i + last) % PRESETS.length : e.key === "Home" ? 0 : e.key === "End" ? last : -1;
    if (next < 0) return;
    e.preventDefault();
    select(PRESETS[next].id);
    refs.current[next]?.focus();
  }

  return (
    <div className="flex flex-col gap-3">
      <div role="radiogroup" aria-label="Період" onKeyDown={onKey} className="flex flex-wrap gap-2">
        {PRESETS.map((p, i) => {
          const checked = state.preset === p.id;
          return (
            <button
              key={p.id}
              ref={(el) => {
                refs.current[i] = el;
              }}
              type="button"
              role="radio"
              aria-checked={checked}
              tabIndex={checked ? 0 : -1}
              onClick={() => select(p.id)}
              className={`min-h-11 cursor-pointer rounded-[22px] border-2 px-4 text-sm font-semibold ${checked ? "border-(--accent) bg-(--accent) text-white" : "border-(--line-strong) bg-white text-(--ink)"}`}
            >
              {p.label}
            </button>
          );
        })}
      </div>
      {state.preset === "custom" ? <CustomRange key={`${state.custom?.from}|${state.custom?.to}`} initial={state.custom ?? resolved} onApply={(r) => onChange({ ...state, custom: r })} /> : null}
    </div>
  );
}

function CustomRange({ initial, onApply }: { initial: DateRange | null; onApply: (r: DateRange) => void }) {
  const [from, setFrom] = useState(initial?.from ?? "");
  const [to, setTo] = useState(initial?.to ?? "");
  const error = validateRange(from, to);
  const same = initial?.from === from && initial?.to === to;
  return (
    <form
      className="flex flex-wrap items-end gap-3"
      onSubmit={(e) => {
        e.preventDefault();
        if (!error) onApply({ from, to });
      }}
    >
      <label className="flex min-w-[140px] flex-1 flex-col gap-1 sm:flex-none">
        <span className={FIELD_LABEL}>Від</span>
        <input type="date" className={FIELD_INPUT} value={from} onChange={(e) => setFrom(e.target.value)} aria-invalid={error ? true : undefined} aria-describedby={error ? "range-error" : undefined} />
      </label>
      <label className="flex min-w-[140px] flex-1 flex-col gap-1 sm:flex-none">
        <span className={FIELD_LABEL}>До</span>
        <input type="date" className={FIELD_INPUT} value={to} onChange={(e) => setTo(e.target.value)} aria-invalid={error ? true : undefined} aria-describedby={error ? "range-error" : undefined} />
      </label>
      <PrimaryButton type="submit" disabled={!!error || same}>
        Застосувати
      </PrimaryButton>
      {error ? (
        <p id="range-error" role="alert" className="m-0 w-full text-sm font-semibold text-[#9B1C1C]">
          {error}
        </p>
      ) : null}
    </form>
  );
}

// ---------- графік динаміки ----------

const POINT_MIN_WIDTH: Record<NetworkSeries["granularity"], number> = { day: 36, week: 70, month: 76 };

export type ChartMetric = "revenue" | "net";

/** Чи віддав backend чистий дохід по точках (§24.6.2): інакше перемикача «Чистий дохід» немає. */
export const hasNetIncome = (series: NetworkSeries): boolean => series.points.some((p) => p.netIncome !== undefined);

const valueOf = (p: SeriesPoint, metric: ChartMetric): number => (metric === "net" ? (p.netIncome ?? 0) : p.revenue);

/** «Виручка / Чистий дохід» (§24.8.1): `radiogroup` без впливу на режим порівняння. */
export function MetricSwitch({ value, onChange }: { value: ChartMetric; onChange: (m: ChartMetric) => void }) {
  const options: { id: ChartMetric; label: string }[] = [
    { id: "revenue", label: "Виручка" },
    { id: "net", label: "Чистий дохід" },
  ];
  return (
    <div role="radiogroup" aria-label="Показник графіка" className="flex flex-wrap gap-2">
      {options.map((o) => (
        <button
          key={o.id}
          type="button"
          role="radio"
          aria-checked={value === o.id}
          onClick={() => onChange(o.id)}
          className={`min-h-11 cursor-pointer rounded-[22px] border-2 px-4 text-sm font-semibold ${value === o.id ? "border-(--accent) bg-(--accent) text-white" : "border-(--line-strong) bg-white text-(--ink)"}`}
        >
          {o.label}
        </button>
      ))}
    </div>
  );
}

/**
 * Графік динаміки (§19.2, §24.8.1). Кожен стовпець - `<button type="button">` на всю висоту колонки (Enter/Space нативно, видимий
 * фокус), `aria-label` «{період}: виручка {x}, чистий дохід {y}. Відкрити деталі». Від'ємний чистий дохід - червоний підпис і
 * мінімальний червоний стовпчик (висота не може бути від'ємною).
 */
export function RevenueChart({ series, metric = "revenue", onOpen }: { series: NetworkSeries; metric?: ChartMetric; onOpen?: (p: SeriesPoint, button: HTMLButtonElement) => void }) {
  const { points, granularity } = series;
  const max = Math.max(1, ...points.map((p) => Math.abs(valueOf(p, metric))));
  const w = POINT_MIN_WIDTH[granularity];
  return (
    <div role="region" aria-label="Виручка мережі, графік" className="overflow-x-auto">
      <ul className="m-0 flex h-[200px] list-none items-end gap-1.5 p-0" style={{ minWidth: points.length * (w + 6) }} aria-label={metric === "net" ? "Чистий дохід за періодами" : "Виручка за періодами"}>
        {points.map((p, i) => {
          const v = valueOf(p, metric);
          const negative = v < 0;
          const full = pointFullLabel(p, granularity);
          const label = `${full}: виручка ${moneyExact(p.revenue)}${p.netIncome !== undefined ? `, чистий дохід ${moneyExact(p.netIncome)}` : ""}. Відкрити деталі`;
          return (
            <li key={p.from} className="flex h-full min-w-0 flex-1" style={{ minWidth: w }}>
              <button
                type="button"
                aria-label={label}
                title={label}
                data-point={p.from}
                onClick={(e) => onOpen?.(p, e.currentTarget)}
                className="flex h-full min-h-6 w-full min-w-0 cursor-pointer flex-col items-center justify-end gap-1.5 rounded-md border-0 bg-transparent p-0 hover:bg-(--tint) focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-(--accent)"
              >
                <span className={`text-[11px] font-semibold ${negative ? "text-[#9B1C1C]" : ""}`} aria-hidden="true">
                  {negative ? "−" : ""}
                  {thousands(Math.abs(v))}
                </span>
                <span
                  aria-hidden="true"
                  className={`w-full rounded-t-md ${negative ? "bg-[#C0392B]" : i === points.length - 1 ? "bg-(--accent)" : "bg-[#B9A1C8]"}`}
                  style={{ height: negative ? 4 : Math.max(v > 0 ? 2 : 0, Math.round((v / max) * 150)) }}
                />
                <span className="text-[11px] whitespace-nowrap text-(--muted)" aria-hidden="true">
                  {pointLabel(p, granularity)}
                </span>
              </button>
            </li>
          );
        })}
      </ul>
    </div>
  );
}
