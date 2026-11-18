"use client";

import { useState } from "react";
import { useBatches } from "../hooks/use-inventory";
import { BATCH_STATUS_VIEW, expiryLabel, formatMoney, formatQuantity, formatYmd } from "../inventory/logic";
import { todayInZone } from "../../beauty-shared/zone-time";
import type { BatchStatus } from "../inventory/types";
import { InventoryPage, LoadMore, LocationChips, locationName } from "./inventory-parts";
import { Badge, Card, Chip, EmptyState, QueryState, Table, TD } from "./ui";

type StatusFilter = BatchStatus | "all";
const FILTERS: { id: StatusFilter; label: string }[] = [
  { id: "active", label: "Діючі" },
  { id: "expiring", label: "Скоро спливають" },
  { id: "expired", label: "Прострочені" },
  { id: "empty", label: "Вичерпані" },
  { id: "all", label: "Усі" },
];

const ROW_TINT: Record<BatchStatus, string> = { active: "", expiring: "bg-[#FFF8E6]", expired: "bg-[#FDEBEB]", empty: "text-(--muted)" };

/** `/beauty/inventory/batches`: партії у порядку FEFO; наближення й прострочення позначені текстом і кольором (не лише кольором). */
export function InventoryBatchesScreen() {
  const [location, setLocation] = useState<string | null>(null);
  const [status, setStatus] = useState<StatusFilter>("active");
  const q = useBatches({ ...(location ? { locationId: location } : {}), status, take: 50 });

  return (
    <InventoryPage title="Партії й терміни" subtitle="Партії за принципом «перший прострочується - перший іде»">
      {(scope) => {
        const tzOf = (id: string) => scope.locations.find((l) => l.id === id)?.timezone;
        return (
          <>
            <LocationChips locations={scope.locations} value={location} onChange={setLocation} />
            <div className="flex flex-wrap gap-2" role="group" aria-label="Статус партії">
              {FILTERS.map((f) => (
                <Chip key={f.id} variant="soft" pressed={status === f.id} onClick={() => setStatus(f.id)}>
                  {f.label}
                </Chip>
              ))}
            </div>
            <QueryState isPending={q.isPending} isError={q.isError} onRetry={() => q.refetch()}>
              {q.items.length === 0 ? (
                <Card>
                  <EmptyState>Партій за цими умовами немає.</EmptyState>
                </Card>
              ) : (
                <Card>
                  <Table
                    caption="Партії й терміни придатності"
                    headers={["Товар", "Заклад", "Номер партії", "Надійшла", "Термін придатності", "Залишок", ...(scope.costs !== "none" ? ["Ціна за од."] : []), "Статус"]}
                    minWidth={900}
                  >
                    {q.items.map((b) => {
                      const view = BATCH_STATUS_VIEW[b.status];
                      return (
                        <tr key={b.id} className={`border-t border-(--line) ${ROW_TINT[b.status]}`}>
                          <th scope="row" className={`${TD} text-left font-semibold`}>
                            {b.product.name}
                          </th>
                          <td className={TD}>{locationName(scope.locations, b.locationId)}</td>
                          <td className={TD}>{b.lotNumber ?? "—"}</td>
                          <td className={TD}>{formatYmd(b.receivedAt.slice(0, 10))}</td>
                          <td className={TD}>
                            {b.expiresOn ? formatYmd(b.expiresOn) : "—"}
                            <span className="block text-[13px] text-(--muted)">{expiryLabel(b.expiresOn, todayInZone(tzOf(b.locationId), new Date()))}</span>
                          </td>
                          <td className={TD}>
                            {formatQuantity(b.quantity, b.product.unit)}
                            <span className="block text-[13px] text-(--muted)">із {formatQuantity(b.initialQuantity, b.product.unit)}</span>
                          </td>
                          {scope.costs !== "none" ? <td className={TD}>{b.unitCost === null || b.unitCost === undefined ? "—" : formatMoney(b.unitCost)}</td> : null}
                          <td className={TD}>
                            <Badge tone={view.tone}>{view.label}</Badge>
                          </td>
                        </tr>
                      );
                    })}
                  </Table>
                  <div className="mt-3">
                    <LoadMore hasMore={q.hasNextPage} loading={q.isFetchingNextPage} onClick={() => void q.fetchNextPage()} shown={q.items.length} />
                  </div>
                </Card>
              )}
            </QueryState>
          </>
        );
      }}
    </InventoryPage>
  );
}
