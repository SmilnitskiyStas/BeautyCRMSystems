"use client";

import { useState } from "react";
import { useMovements } from "../hooks/use-inventory";
import { MOVEMENT_LABEL, REASON_LABEL, formatMoney, formatSigned, isYmd } from "../inventory/logic";
import type { MovementType } from "../inventory/types";
import { dateTimeLabel } from "../format";
import { InputField, InventoryPage, LoadMore, LocationChips, SelectField, locationName } from "./inventory-parts";
import { Card, EmptyState, QueryState, Table, TD } from "./ui";

/** `/beauty/inventory/movements`: журнал рухів (append-only). Діапазон ≤ 366 діб; за замовчуванням останні 30 діб. */
export function InventoryMovementsScreen() {
  const [location, setLocation] = useState<string | null>(null);
  const [type, setType] = useState("");
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const rangeOk = (from === "" || isYmd(from)) && (to === "" || isYmd(to)) && !(from && to && from > to);
  const q = useMovements(
    {
      ...(location ? { locationId: location } : {}),
      ...(type ? { type: type as MovementType } : {}),
      ...(from && isYmd(from) ? { from: `${from}T00:00:00Z` } : {}),
      ...(to && isYmd(to) ? { to: `${to}T23:59:59Z` } : {}),
      take: 50,
    },
    rangeOk,
  );

  return (
    <InventoryPage title="Журнал рухів" subtitle="Усі надходження, списання, переміщення й коригування. Записи не редагуються">
      {(scope) => (
        <>
          <LocationChips locations={scope.locations} value={location} onChange={setLocation} />
          <div className="grid gap-3 sm:grid-cols-3">
            <SelectField
              label="Тип руху"
              value={type}
              onChange={setType}
              placeholder="Усі типи"
              options={(["receipt", "write_off", "transfer_out", "transfer_in", "stocktake_adjust"] as MovementType[]).map((t) => ({ value: t, label: MOVEMENT_LABEL[t] }))}
            />
            <InputField label="Від" type="date" value={from} onChange={setFrom} />
            <InputField label="До" type="date" value={to} onChange={setTo} error={rangeOk ? undefined : "Перевірте дати: «Від» не пізніше за «До»."} hint="Порожньо - останні 30 діб" />
          </div>
          <QueryState isPending={q.isPending && rangeOk} isError={q.isError} onRetry={() => q.refetch()}>
            {q.items.length === 0 ? (
              <Card>
                <EmptyState>Рухів за цими умовами немає.</EmptyState>
              </Card>
            ) : (
              <Card>
                <Table caption="Журнал рухів складу" headers={["Час", "Заклад", "Операція", "Товар", "Кількість", ...(scope.costs !== "none" ? ["Ціна за од."] : []), "Причина / коментар", "Хто"]} minWidth={980}>
                  {q.items.map((m) => (
                    <tr key={m.id} className="border-t border-(--line)">
                      <td className={TD}>{dateTimeLabel(m.occurredAt, scope.locations.find((l) => l.id === m.locationId)?.timezone)}</td>
                      <td className={TD}>
                        {locationName(scope.locations, m.locationId)}
                        {m.counterpartLocationId ? (
                          <span className="block text-[13px] text-(--muted)">
                            {m.type === "transfer_out" ? "до" : "з"} {locationName(scope.locations, m.counterpartLocationId)}
                          </span>
                        ) : null}
                      </td>
                      <td className={TD}>{MOVEMENT_LABEL[m.type]}</td>
                      <td className={`${TD} font-semibold`}>{m.product.name}</td>
                      <td className={`${TD} whitespace-nowrap ${m.quantity < 0 ? "text-[#8A1F1F]" : "text-[#145A32]"}`}>{formatSigned(m.quantity, m.product.unit)}</td>
                      {scope.costs !== "none" ? <td className={TD}>{m.unitCost === null || m.unitCost === undefined ? "—" : formatMoney(m.unitCost)}</td> : null}
                      <td className={TD}>
                        {[m.reason ? REASON_LABEL[m.reason] : null, m.note].filter(Boolean).join(": ") || "—"}
                      </td>
                      <td className={TD}>{m.createdBy?.name ?? "Система"}</td>
                    </tr>
                  ))}
                </Table>
                <div className="mt-3">
                  <LoadMore hasMore={q.hasNextPage} loading={q.isFetchingNextPage} onClick={() => void q.fetchNextPage()} shown={q.items.length} />
                </div>
              </Card>
            )}
          </QueryState>
        </>
      )}
    </InventoryPage>
  );
}
