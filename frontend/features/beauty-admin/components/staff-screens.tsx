"use client";

import { useState } from "react";
import { useStaff, useStaffProfile } from "../hooks/use-beauty-admin";
import { initials } from "../format";
import type { StaffFeedItem } from "../types";
import {
  Avatar,
  Badge,
  BarRow,
  Card,
  KeyValue,
  KpiGrid,
  LinkButton,
  PageHeader,
  QueryState,
  RowLink,
  SecondaryButton,
  Table,
  TD,
  Tabs,
  TextLink,
  type Tone,
} from "./ui";

export function StaffListScreen() {
  const staff = useStaff();
  return (
    <>
      <PageHeader title="Спеціалісти" subtitle="Команда мережі" />
      <QueryState isPending={staff.isPending} isError={staff.isError} onRetry={() => staff.refetch()}>
        <Card className="flex flex-col">
          {staff.data?.map((s) => (
            <div key={s.id} className="flex items-center gap-3 border-t border-(--line) py-2.5 first:border-t-0">
              <Avatar text={initials(s.name)} />
              <span className="flex min-w-0 flex-1 flex-col">
                <RowLink href={`/beauty/staff/${s.id}`}>
                  <span className="text-[15px] font-semibold no-underline">{s.name}</span>
                </RowLink>
                <span className="text-[13px] text-(--muted)">{s.role}</span>
              </span>
              <Badge tone="vip">{s.locations}</Badge>
            </div>
          ))}
        </Card>
      </QueryState>
    </>
  );
}

type Tab = "act" | "svc" | "sch";
const FEED_TONE: Record<StaffFeedItem["kind"], Tone> = { done: "ok", move: "wait", note: "now", review: "vip", slot: "neutral" };

export function StaffProfileScreen({ id }: { id: string }) {
  const [tab, setTab] = useState<Tab>("act");
  const q = useStaffProfile(id);
  const s = q.data;
  const maxVisits = Math.max(1, ...(s?.activity.map((d) => d.value) ?? [1]));

  return (
    <>
      <TextLink href="/beauty/staff">← Усі спеціалісти</TextLink>
      <QueryState isPending={q.isPending} isError={q.isError} onRetry={() => q.refetch()}>
        {s === null ? (
          <Card>
            <p className="text-sm">Спеціаліста не знайдено.</p>
          </Card>
        ) : s ? (
          <>
            <Card className="flex flex-wrap items-center gap-5 p-6">
              <span
                aria-hidden="true"
                className="beauty-display flex size-[72px] flex-none items-center justify-center rounded-full bg-[#EADCF2] text-2xl font-semibold text-[#4A2260]"
              >
                {initials(s.name)}
              </span>
              <div className="flex min-w-0 flex-[1_1_260px] flex-col gap-1.5">
                <h1 className="beauty-display text-[26px] font-semibold">{s.name}</h1>
                <div className="text-sm text-(--muted)">{s.role}</div>
                <div className="flex flex-wrap gap-1.5">
                  {s.locationBadges.map((b) => (
                    <Badge key={b} tone="vip" className="!text-xs">
                      {b}
                    </Badge>
                  ))}
                </div>
              </div>
              <div className="flex flex-wrap gap-2">
                <LinkButton href="/beauty/calendar">Відкрити календар</LinkButton>
                <SecondaryButton>Редагувати</SecondaryButton>
              </div>
            </Card>

            <KpiGrid items={s.kpis} min={200} />

            <div className="flex flex-wrap items-start gap-6">
              <Card className="flex flex-[999_1_560px] flex-col gap-4">
                <Tabs
                  value={tab}
                  onChange={setTab}
                  tabs={[
                    { id: "act", label: "Активність" },
                    { id: "svc", label: "Послуги" },
                    { id: "sch", label: "Графік" },
                  ]}
                />
                {tab === "act" ? (
                  <>
                    <div>
                      <h2 className="mb-1 text-base font-semibold">Візитів по днях, останні 14 днів</h2>
                      <div className="mb-3 text-[13px] text-(--muted)">Графік закритих візитів</div>
                      <ul className="m-0 flex h-[140px] list-none items-end gap-2 p-0" aria-label="Візитів по днях">
                        {s.activity.map((d, i) => (
                          <li key={`${d.label}-${i}`} className="flex h-full min-w-0 flex-1 flex-col items-center justify-end gap-1" aria-label={`${d.label}: ${d.value}`}>
                            <span className="text-[11px] font-semibold">{d.value || ""}</span>
                            <span
                              className={`w-full rounded-t ${i === s.activity.length - 1 ? "bg-(--accent)" : "bg-[#B9A1C8]"}`}
                              style={{ height: Math.round((d.value / maxVisits) * 100) + (d.value ? 0 : 3) }}
                            />
                            <span className="text-[11px] text-(--muted)">{d.label}</span>
                          </li>
                        ))}
                      </ul>
                    </div>
                    <h2 className="mt-2 text-base font-semibold">Остання активність</h2>
                    <ul className="m-0 flex list-none flex-col p-0">
                      {s.feed.map((f) => (
                        <li key={f.id} className="flex items-start gap-3 border-t border-(--line) py-3">
                          <Badge tone={FEED_TONE[f.kind]} className="min-w-[84px] flex-none text-center !text-xs">
                            {f.kindLabel}
                          </Badge>
                          <span className="min-w-0 flex-1 text-sm">{f.text}</span>
                          <span className="flex-none text-[13px] text-(--muted)">{f.when}</span>
                        </li>
                      ))}
                    </ul>
                  </>
                ) : null}
                {tab === "svc" ? (
                  <Table headers={["Послуга", "Тривалість", "Ціна", "Виконано за місяць"]} minWidth={520}>
                    {s.services.map((r) => (
                      <tr key={r.name} className="border-t border-(--line)">
                        <td className={`${TD} font-semibold`}>{r.name}</td>
                        <td className={`${TD} text-(--muted)`}>{r.duration}</td>
                        <td className={TD}>{r.price}</td>
                        <td className={TD}>{r.count}</td>
                      </tr>
                    ))}
                  </Table>
                ) : null}
                {tab === "sch" ? (
                  <ul className="m-0 flex list-none flex-col p-0">
                    {s.schedule.map((r) => (
                      <li key={r.day} className="flex flex-wrap items-center gap-3 border-t border-(--line) py-3">
                        <span className="w-10 font-semibold">{r.day}</span>
                        <span className="flex-[1_1_160px] text-sm">{r.hours}</span>
                        <Badge tone={r.off ? "neutral" : r.location === "Центр" ? "vip" : "ok"}>{r.location}</Badge>
                      </li>
                    ))}
                  </ul>
                ) : null}
              </Card>

              <aside className="flex min-w-0 flex-[1_1_300px] flex-col gap-4">
                <Card as="div" className="flex flex-col gap-2.5">
                  <h2 className="text-lg font-semibold">Показники місяця</h2>
                  {s.bars.map((b) => (
                    <BarRow key={b.label} label={b.label} value={b.value} pct={b.pct} />
                  ))}
                </Card>
                <Card as="div" className="flex flex-col gap-2.5">
                  <h2 className="text-lg font-semibold">Контакти й умови</h2>
                  {s.contacts.map((c) => (
                    <KeyValue key={c.label} label={c.label} value={c.value} />
                  ))}
                </Card>
              </aside>
            </div>
          </>
        ) : null}
      </QueryState>
    </>
  );
}
