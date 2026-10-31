"use client";

import { useSearchParams } from "next/navigation";
import { useEffect, useState, type ReactNode } from "react";
import { useAuth } from "@/features/beauty-auth/components/auth-provider";
import { canSeeFinance } from "@/features/beauty-auth/permissions";
import { targetOf, type DetailsTarget } from "../analytics/details";
import { downloadCsv, locationCsv, networkCsv, promotionsCsv } from "../analytics/csv";
import { computeDelta, describeDelta } from "../analytics/delta";
import { locationKpis, networkKpis, percentLabel, promotionKpis } from "../analytics/kpis";
import {
  parseRangeParams,
  previousRange,
  rangeLabel,
  rangeLabelShort,
  referenceZone,
  resolveRange,
  serializeRangeParams,
  type RangeState,
} from "../analytics/ranges";
import { GRANULARITY_TITLE } from "../analytics/series";
import { todayInZone } from "../../beauty-shared/zone-time";
import { completedWord, money, moneyExact } from "../format";
import {
  useLocationBreakdown,
  useLocations,
  useLocationsAnalytics,
  useNetworkAnalytics,
  useNetworkSeries,
  usePromotionAnalytics,
} from "../hooks/use-beauty-admin";
import type { AnalyticsQuery } from "../types";
import { AnalyticsDetailsPanel } from "./analytics-details-panel";
import { AnalyticsKpiGrid, DeltaText, ExportButton, MetricSwitch, RangeSwitcher, RevenueChart, hasNetIncome, type ChartMetric } from "./analytics-parts";
import { BarRow, Card, Chip, PageHeader, ProgressBar, QueryState, RowLink, Switch, Table, TD, TextLink, Tabs } from "./ui";

type Tab = "net" | "loc" | "promo";

const pctOf = (v: number, max: number) => (max > 0 ? Math.round((v / max) * 100) : 0);

function Toolbar({ children }: { children: ReactNode }) {
  return <div className="flex flex-wrap items-center justify-end gap-3">{children}</div>;
}

function NetworkTab({ q, zone, onZone }: { q: AnalyticsQuery; zone: string; onZone: (z: string) => void }) {
  const net = useNetworkAnalytics(q);
  const series = useNetworkSeries(q);
  const locs = useLocationsAnalytics(q);
  const tz = net.data?.timezone;
  useEffect(() => {
    // Еталонна зона мережі - з відповіді сервера (§19.1): якщо відрізняється від припущеної, пересчитуємо пресети.
    if (tz && tz !== zone) onZone(tz);
  }, [tz, zone, onZone]);

  const [metric, setMetric] = useState<ChartMetric>("revenue");
  const [open, setOpen] = useState<{ target: DetailsTarget; opener: HTMLElement | null } | null>(null);
  const rows = locs.data ?? [];
  const maxRev = Math.max(0, ...rows.map((l) => l.revenue));
  const totals = net.data?.totals;
  const netShown = series.data ? hasNetIncome(series.data) : false;
  return (
    <>
      <Toolbar>
        <ExportButton
          disabled={!net.data || !series.data}
          onClick={() => net.data && series.data && downloadCsv(networkCsv(net.data, series.data, q.compare))}
        />
      </Toolbar>
      <QueryState isPending={net.isPending} isError={net.isError} onRetry={() => net.refetch()}>
        {net.data ? <AnalyticsKpiGrid items={networkKpis(net.data, q.compare)} /> : null}
        {totals && (totals.withoutCost ?? 0) > 0 ? (
          <div role="status" data-testid="without-cost-banner" className="flex flex-wrap items-center gap-x-3 rounded-2xl bg-[#FFF1CC] px-4 py-2 text-sm text-[#5C3900]">
            <span>
              {totals.withoutCost} {completedWord(totals.withoutCost ?? 0)} без собівартості ({moneyExact(totals.revenueWithoutCost ?? 0)} виручки) — чистий дохід завищено.
            </span>
            <TextLink href="/beauty/services">Задати собівартість →</TextLink>
          </div>
        ) : null}
      </QueryState>
      <div className="flex flex-wrap items-start gap-6">
        <Card className="flex-[1_1_480px]">
          <h2 className="mb-1 text-lg font-semibold">
            {metric === "net" ? "Чистий дохід мережі" : "Виручка мережі"} {series.data ? GRANULARITY_TITLE[series.data.granularity] : ""}, тис. ₴
          </h2>
          <div className="mb-3 text-[13px] text-(--muted)">{rangeLabelShort(q)}. Натисніть на стовпець, щоб побачити деталі.</div>
          {netShown ? (
            <div className="mb-3">
              <MetricSwitch value={metric} onChange={setMetric} />
            </div>
          ) : null}
          <QueryState isPending={series.isPending} isError={series.isError} onRetry={() => series.refetch()}>
            {series.data ? (
              <RevenueChart
                series={series.data}
                metric={netShown ? metric : "revenue"}
                onOpen={(p, el) => setOpen({ target: targetOf(p, series.data.granularity), opener: el })}
              />
            ) : null}
          </QueryState>
        </Card>
        <Card className="flex flex-[1_1_420px] flex-col gap-3.5">
          <h2 className="text-lg font-semibold">Виручка по закладах</h2>
          <QueryState isPending={locs.isPending} isError={locs.isError} onRetry={() => locs.refetch()}>
            {rows.length === 0 ? <p className="m-0 text-sm text-(--muted)">Закладів ще немає.</p> : null}
            {rows.map((l) => (
              <div key={l.locationId} className="flex flex-col gap-1">
                <BarRow
                  label={l.isActive ? l.name : `${l.name} (неактивний)`}
                  value={money(l.revenue)}
                  pct={pctOf(l.revenue, maxRev)}
                  note={`${l.appointments} записів · скасовано ${l.cancelled}${l.netIncome !== undefined ? ` · чистий дохід ${moneyExact(l.netIncome)}` : ""}`}
                />
                {q.compare && l.previous ? <DeltaText view={describeDelta(computeDelta(l.revenue, l.previous.revenue), "money")} /> : null}
              </div>
            ))}
          </QueryState>
        </Card>
      </div>
      {open ? <AnalyticsDetailsPanel key={`${open.target.from}|${open.target.to}`} target={open.target} returnFocus={open.opener} onClose={() => setOpen(null)} /> : null}
    </>
  );
}

function LocationsTab({ q }: { q: AnalyticsQuery }) {
  const [chosen, setLoc] = useState<string | null>(null);
  const locs = useLocationsAnalytics(q);
  const list = locs.data ?? [];
  // Початковий заклад = перший реальний (не вигаданий id, аудит F6).
  const loc = (chosen !== null && list.some((l) => l.locationId === chosen) ? chosen : list[0]?.locationId) ?? "";
  const stat = list.find((l) => l.locationId === loc) ?? null;
  const b = useLocationBreakdown(loc, q, loc !== "");

  if (locs.isSuccess && list.length === 0) {
    return <p className="text-sm text-(--muted)">Закладів ще немає: додайте заклад, щоб побачити аналітику по закладах.</p>;
  }
  const maxSvc = Math.max(0, ...(b.data?.services.map((s) => s.revenue) ?? [0]));
  return (
    <>
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div className="flex flex-wrap gap-2" role="group" aria-label="Заклад">
          {list.map((l) => (
            <Chip key={l.locationId} variant="soft" pressed={loc === l.locationId} onClick={() => setLoc(l.locationId)}>
              {l.isActive ? l.name : `${l.name} (неактивний)`}
            </Chip>
          ))}
        </div>
        <ExportButton disabled={!b.data || b.isFetching} onClick={() => b.data && downloadCsv(locationCsv(stat, b.data))} />
      </div>
      <QueryState isPending={locs.isPending || (loc !== "" && b.isPending)} isError={locs.isError || b.isError} onRetry={() => { void locs.refetch(); void b.refetch(); }}>
        {stat && b.data ? (
          <>
            <AnalyticsKpiGrid items={locationKpis(stat, q.compare)} />
            <div className="flex flex-wrap items-start gap-6">
              <Card className="flex-[1_1_480px]">
                <h2 className="mb-3.5 text-lg font-semibold">Спеціалісти закладу</h2>
                <Table headers={["Спеціаліст", "Записів", "Виручка", "Завантаження"]} minWidth={440}>
                  {b.data.specialists.map((r) => (
                    <tr key={r.specialistId} className="border-t border-(--line)">
                      <td className={`${TD} font-semibold`}>
                        <RowLink href={`/beauty/staff/${r.specialistId}`}>{r.isActive ? r.name : `${r.name} (неактивний)`}</RowLink>
                      </td>
                      <td className={TD}>{r.appointments}</td>
                      <td className={TD}>{money(r.revenue)}</td>
                      <td className={TD}>
                        <span className="flex items-center gap-2">
                          <span className="block w-[90px]">
                            <ProgressBar pct={r.load === null ? 0 : Math.round(r.load * 100)} thin />
                          </span>
                          {percentLabel(r.load, 0)}
                        </span>
                      </td>
                    </tr>
                  ))}
                </Table>
                {b.data.specialists.length === 0 ? <p className="mt-3 text-sm text-(--muted)">За цей період немає записів і доступних годин.</p> : null}
              </Card>
              <Card className="flex flex-[1_1_360px] flex-col gap-3.5">
                <h2 className="text-lg font-semibold">Топ послуг</h2>
                {b.data.services.slice(0, 5).map((s) => (
                  <BarRow key={s.serviceId} label={s.name} value={money(s.revenue)} pct={pctOf(s.revenue, maxSvc)} />
                ))}
                {b.data.services.length === 0 ? <p className="m-0 text-sm text-(--muted)">Завершених послуг за цей період немає.</p> : null}
              </Card>
            </div>
          </>
        ) : null}
      </QueryState>
    </>
  );
}

function PromosTab({ q }: { q: AnalyticsQuery }) {
  const a = usePromotionAnalytics(q);
  const rows = a.data?.rows ?? [];
  const maxUses = Math.max(0, ...rows.map((r) => r.uses));
  return (
    <>
      <Toolbar>
        <ExportButton disabled={!a.data} onClick={() => a.data && downloadCsv(promotionsCsv(a.data.rows, q.from, q.to, q.compare))} />
      </Toolbar>
      <QueryState isPending={a.isPending} isError={a.isError} onRetry={() => a.refetch()}>
        {a.data ? (
          <>
            <AnalyticsKpiGrid items={promotionKpis(rows, q.compare)} />
            <Card>
              <h2 className="mb-3.5 text-lg font-semibold">Ефективність акцій</h2>
              <Table headers={["Акція", "Період", "Заклади", "Використань", "Виручка", "Віддано знижками"]} minWidth={700}>
                {rows.map((r) => (
                  <tr key={r.id} className="border-t border-(--line)">
                    <td className={`${TD} font-semibold`}>
                      <RowLink href="/beauty/promos">{r.name}</RowLink>
                    </td>
                    <td className={`${TD} text-(--muted)`}>{r.period}</td>
                    <td className={`${TD} text-(--muted)`}>{r.where}</td>
                    <td className={TD}>
                      <div>{r.uses}</div>
                      {q.compare ? <DeltaText view={describeDelta(computeDelta(r.uses, r.previous?.uses), "count")} /> : null}
                    </td>
                    <td className={`${TD} font-semibold`}>
                      <div>{money(r.revenue)}</div>
                      {q.compare ? <DeltaText view={describeDelta(computeDelta(r.revenue, r.previous?.revenue), "money")} /> : null}
                    </td>
                    <td className={`${TD} font-semibold text-(--promo)`}>{money(r.discountTotal)}</td>
                  </tr>
                ))}
              </Table>
              {rows.length === 0 ? <p className="mt-3 text-sm text-(--muted)">За цей період акції не використовувались.</p> : null}
            </Card>
            {rows.length > 0 ? (
              <Card className="flex flex-col gap-3.5">
                <h2 className="text-lg font-semibold">Використання акцій</h2>
                {rows.map((r) => (
                  <BarRow key={r.id} label={r.name} value={String(r.uses)} pct={pctOf(r.uses, maxUses)} color="bg-(--promo)" />
                ))}
              </Card>
            ) : null}
          </>
        ) : null}
      </QueryState>
    </>
  );
}

/** Стан діапазону живе в URL (`?range=&from=&to=&compare=1`); оновлюється через History API (Next синхронізує `useSearchParams`). */
function writeUrl(state: RangeState) {
  const qs = serializeRangeParams(state);
  window.history.replaceState(window.history.state, "", `${window.location.pathname}${qs ? `?${qs}` : ""}`);
}

export function AnalyticsScreen() {
  const { user } = useAuth();
  // Фінансові поля й деталі періоду - лише owner/admin (§24.9); specialist отримує 403 від API, тож розділ йому не показується.
  if (!canSeeFinance(user.role)) {
    return (
      <>
        <PageHeader title="Аналітика" />
        <Card>
          <p role="note" className="text-sm">
            Аналітика доступна лише власнику й адміністратору.
          </p>
        </Card>
      </>
    );
  }
  return <AnalyticsView />;
}

function AnalyticsView() {
  const sp = useSearchParams();
  const [tab, setTab] = useState<Tab>("net");
  const [state, setState] = useState<RangeState>(() => parseRangeParams(sp ?? new URLSearchParams()));
  const locations = useLocations();
  const [zoneFromServer, setZoneFromServer] = useState<string | null>(null);

  const zone = zoneFromServer ?? referenceZone(locations.data ?? []);
  const ready = !locations.isPending || zoneFromServer !== null;
  const range = resolveRange(state, todayInZone(zone));
  const invalidCustom = state.preset === "custom" && range === null;

  const update = (next: RangeState) => {
    setState(next);
    writeUrl(next);
  };

  const q: AnalyticsQuery | null = range ? { ...range, compare: state.compare } : null;
  const prev = range && state.compare ? previousRange(range) : null;

  return (
    <>
      <PageHeader
        title="Аналітика"
        subtitle={
          range ? (
            <>
              <span>{rangeLabel(range)}</span>
              {prev ? <span className="block">Порівняння з {rangeLabelShort(prev)}</span> : null}
            </>
          ) : (
            "Оберіть коректний період"
          )
        }
        actions={
          <Tabs
            value={tab}
            onChange={setTab}
            tabs={[
              { id: "net", label: "Мережа" },
              { id: "loc", label: "Заклади" },
              { id: "promo", label: "Акції" },
            ]}
          />
        }
      />
      <div className="flex flex-col gap-3 rounded-2xl bg-white p-4">
        <RangeSwitcher state={state} resolved={range} onChange={update} />
        <div className="flex items-center gap-3">
          <Switch label="Порівняти з попереднім періодом" checked={state.compare} onChange={(compare) => update({ ...state, compare })} />
          <span className="text-sm font-semibold" aria-hidden="true">
            Порівняти з попереднім періодом
          </span>
        </div>
      </div>
      {!ready ? (
        <QueryState isPending isError={false}>
          {null}
        </QueryState>
      ) : invalidCustom || !q ? (
        <p role="alert" className="text-sm font-semibold text-[#9B1C1C]">
          Вкажіть коректний період: початок не пізніше за кінець, не довше за 366 днів.
        </p>
      ) : (
        <>
          {tab === "net" ? <NetworkTab q={q} zone={zone} onZone={setZoneFromServer} /> : null}
          {tab === "loc" ? <LocationsTab q={q} /> : null}
          {tab === "promo" ? <PromosTab q={q} /> : null}
        </>
      )}
    </>
  );
}
