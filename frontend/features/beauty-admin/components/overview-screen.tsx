"use client";

import { useState } from "react";
import { useLocations, useOverview } from "../hooks/use-beauty-admin";
import type { LocationId } from "../types";
import {
  Avatar,
  Badge,
  Card,
  CLIENT_TAG_VIEW,
  Chip,
  KpiGrid,
  PageHeader,
  QueryState,
  RowLink,
  STATUS_VIEW,
  Table,
  TD,
  TextLink,
} from "./ui";
import { initials } from "../format";

export function OverviewScreen() {
  const [location, setLocation] = useState<LocationId | null>(null);
  const locations = useLocations();
  const overview = useOverview(location);

  return (
    <>
      <PageHeader
        title="Огляд на сьогодні"
        subtitle={overview.data?.dateLabel ?? "Мережа закладів"}
        actions={
          <div className="flex flex-wrap gap-2" role="group" aria-label="Заклад">
            <Chip pressed={location === null} onClick={() => setLocation(null)}>
              Усі заклади
            </Chip>
            {locations.data?.map((l) => (
              <Chip key={l.id} pressed={location === l.id} onClick={() => setLocation(l.id)}>
                {l.name}
              </Chip>
            ))}
          </div>
        }
      />
      <QueryState isPending={overview.isPending} isError={overview.isError} onRetry={() => overview.refetch()}>
        {overview.data ? (
          <>
            <KpiGrid items={overview.data.kpis} />
            <div className="flex flex-wrap items-start gap-6">
              <Card className="flex-[999_1_560px]">
                <h2 className="mb-3.5 text-lg font-semibold">Записи на сьогодні</h2>
                {overview.data.rows.length === 0 ? (
                  <p className="text-sm text-(--muted)">У цьому закладі сьогодні записів немає.</p>
                ) : (
                  <Table headers={["Час", "Клієнт", "Послуга", "Спеціаліст", "Заклад", "Статус"]}>
                    {overview.data.rows.map((r) => {
                      const st = STATUS_VIEW[r.status];
                      return (
                        <tr key={r.id} className="border-t border-(--line)">
                          <td className={`${TD} font-semibold`}>{r.time}</td>
                          <td className={TD}>
                            {r.clientId ? <RowLink href={`/beauty/clients/${r.clientId}`}>{r.clientName}</RowLink> : r.clientName}
                          </td>
                          <td className={TD}>{r.serviceName}</td>
                          <td className={TD}>
                            <RowLink href={`/beauty/staff/${r.specialistId}`}>{r.specialistName}</RowLink>
                          </td>
                          <td className={`${TD} text-(--muted)`}>{r.locationName}</td>
                          <td className={TD}>
                            <Badge tone={st.tone}>{st.label}</Badge>
                          </td>
                        </tr>
                      );
                    })}
                  </Table>
                )}
              </Card>
              <Card className="flex flex-[1_1_320px] flex-col gap-1">
                <h2 className="mb-2.5 text-lg font-semibold">Клієнти</h2>
                {overview.data.clients.map((c) => {
                  const tag = CLIENT_TAG_VIEW[c.tag];
                  return (
                    <div key={c.id} className="flex items-center gap-3 border-t border-(--line) py-2.5">
                      <Avatar text={initials(c.name)} />
                      <span className="flex min-w-0 flex-1 flex-col">
                        <RowLink href={`/beauty/clients/${c.id}`}>
                          <span className="text-[15px] font-semibold no-underline">{c.name}</span>
                        </RowLink>
                        <span className="text-[13px] text-(--muted)">{c.meta}</span>
                      </span>
                      <Badge tone={tag.tone} className="flex-none !text-xs">
                        {tag.label}
                      </Badge>
                    </div>
                  );
                })}
                <TextLink href="/beauty/clients">Усі клієнти</TextLink>
              </Card>
            </div>
          </>
        ) : null}
      </QueryState>
    </>
  );
}
