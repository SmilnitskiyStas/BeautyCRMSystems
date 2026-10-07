"use client";

import { useState } from "react";
import {
  useLocationAnalytics,
  useLocations,
  useNetworkAnalytics,
  usePromotionAnalytics,
} from "../hooks/use-beauty-admin";
import { BarRow, Card, Chip, KpiGrid, PageHeader, ProgressBar, QueryState, RowLink, Table, TD, Tabs } from "./ui";

type Tab = "net" | "loc" | "promo";

function NetworkTab() {
  const q = useNetworkAnalytics();
  const max = Math.max(1, ...(q.data?.weeks.map((w) => w.value) ?? [1]));
  return (
    <QueryState isPending={q.isPending} isError={q.isError} onRetry={() => q.refetch()}>
      {q.data ? (
        <>
          <KpiGrid items={q.data.kpis} />
          <div className="flex flex-wrap items-start gap-6">
            <Card className="flex-[1_1_480px]">
              <h2 className="mb-1 text-lg font-semibold">Виручка мережі по тижнях, тис. ₴</h2>
              <div className="mb-4 text-[13px] text-(--muted)">Останні 8 тижнів</div>
              <ul className="m-0 flex h-[200px] list-none items-end gap-3 p-0" aria-label="Виручка по тижнях">
                {q.data.weeks.map((w, i) => (
                  <li key={w.label} className="flex h-full min-w-0 flex-1 flex-col items-center justify-end gap-1.5" aria-label={`${w.label}: ${w.value} тис. ₴`}>
                    <span className="text-xs font-semibold">{w.value}</span>
                    <span
                      className={`w-full rounded-t-md ${i === q.data.weeks.length - 1 ? "bg-(--accent)" : "bg-[#B9A1C8]"}`}
                      style={{ height: Math.round((w.value / max) * 150) }}
                    />
                    <span className="text-xs text-(--muted)">{w.label}</span>
                  </li>
                ))}
              </ul>
            </Card>
            <Card className="flex flex-[1_1_420px] flex-col gap-3.5">
              <h2 className="text-lg font-semibold">Виручка по закладах</h2>
              {q.data.locations.map((l) => (
                <BarRow key={l.id} label={l.label} value={l.value} pct={l.pct} note={l.note} />
              ))}
            </Card>
          </div>
        </>
      ) : null}
    </QueryState>
  );
}

function LocationsTab() {
  const [loc, setLoc] = useState("c");
  const locations = useLocations();
  const q = useLocationAnalytics(loc);
  return (
    <>
      <div className="flex flex-wrap gap-2" role="group" aria-label="Заклад">
        {locations.data?.map((l) => (
          <Chip key={l.id} variant="soft" pressed={loc === l.id} onClick={() => setLoc(l.id)}>
            {l.name}
          </Chip>
        ))}
      </div>
      <QueryState isPending={q.isPending} isError={q.isError} onRetry={() => q.refetch()}>
        {q.data ? (
          <>
            <KpiGrid items={q.data.kpis} />
            <div className="flex flex-wrap items-start gap-6">
              <Card className="flex-[1_1_480px]">
                <h2 className="mb-3.5 text-lg font-semibold">Спеціалісти закладу</h2>
                <Table headers={["Спеціаліст", "Записів", "Виручка", "Завантаження"]} minWidth={440}>
                  {q.data.specialists.map((r) => (
                    <tr key={r.id} className="border-t border-(--line)">
                      <td className={`${TD} font-semibold`}>
                        <RowLink href={`/beauty/staff/${r.id}`}>{r.name}</RowLink>
                      </td>
                      <td className={TD}>{r.count}</td>
                      <td className={TD}>{r.revenue}</td>
                      <td className={TD}>
                        <span className="flex items-center gap-2">
                          <span className="block w-[90px]">
                            <ProgressBar pct={r.load} thin />
                          </span>
                          {r.load}%
                        </span>
                      </td>
                    </tr>
                  ))}
                </Table>
              </Card>
              <Card className="flex flex-[1_1_360px] flex-col gap-3.5">
                <h2 className="text-lg font-semibold">Топ послуг</h2>
                {q.data.topServices.map((s) => (
                  <BarRow key={s.label} label={s.label} value={s.value} pct={s.pct} />
                ))}
              </Card>
            </div>
          </>
        ) : null}
      </QueryState>
    </>
  );
}

function PromosTab() {
  const q = usePromotionAnalytics();
  return (
    <QueryState isPending={q.isPending} isError={q.isError} onRetry={() => q.refetch()}>
      {q.data ? (
        <>
          <KpiGrid items={q.data.kpis} />
          <Card>
            <h2 className="mb-3.5 text-lg font-semibold">Ефективність акцій</h2>
            <Table headers={["Акція", "Період", "Заклади", "Записів", "Нових клієнтів", "Виручка", "Віддано знижками"]} minWidth={760}>
              {q.data.rows.map((r) => (
                <tr key={r.id} className="border-t border-(--line)">
                  <td className={`${TD} font-semibold`}>
                    <RowLink href="/beauty/promos">{r.name}</RowLink>
                  </td>
                  <td className={`${TD} text-(--muted)`}>{r.period}</td>
                  <td className={`${TD} text-(--muted)`}>{r.where}</td>
                  <td className={TD}>{r.count}</td>
                  <td className={TD}>{r.fresh}</td>
                  <td className={`${TD} font-semibold`}>{r.revenue}</td>
                  <td className={`${TD} font-semibold text-(--promo)`}>{r.cost}</td>
                </tr>
              ))}
            </Table>
          </Card>
          <Card className="flex flex-col gap-3.5">
            <h2 className="text-lg font-semibold">Записи по акціях</h2>
            {q.data.bars.map((b) => (
              <BarRow key={b.label} label={b.label} value={b.value} pct={b.pct} color="bg-(--promo)" />
            ))}
          </Card>
        </>
      ) : null}
    </QueryState>
  );
}

export function AnalyticsScreen() {
  const [tab, setTab] = useState<Tab>("net");
  return (
    <>
      <PageHeader
        title="Аналітика"
        subtitle="Жовтень 2026 · дані за місяць"
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
      {tab === "net" ? <NetworkTab /> : null}
      {tab === "loc" ? <LocationsTab /> : null}
      {tab === "promo" ? <PromosTab /> : null}
    </>
  );
}
