"use client";

import Link from "next/link";
import { useState } from "react";
import { useStock, useStockSummary } from "../hooks/use-inventory";
import { balanceLabel, formatQuantity, formatYmd, valueLabel } from "../inventory/logic";
import type { InventoryScope, StockRow } from "../inventory/types";
import { InputField, InventoryPage, LoadMore, LocationChips, locationName } from "./inventory-parts";
import { OperationDialog, type OperationKindForm } from "./inventory-operation-dialog";
import { SuccessBanner } from "./staff-parts";
import { Badge, Card, Chip, EmptyState, PrimaryButton, QueryState, SecondaryButton, Table, TD } from "./ui";

type StateFilter = "all" | "low" | "expiring" | "out_of_stock";
const STATE_LABEL: Record<StateFilter, string> = { all: "Усі", low: "Низький залишок", expiring: "Закінчується термін", out_of_stock: "Немає в наявності" };

const DONE_TEXT: Record<OperationKindForm, string> = { receipt: "Надходження збережено.", write_off: "Списання збережено.", transfer: "Переміщення виконано." };

function RowBadges({ r, soonDays }: { r: StockRow; soonDays: number }) {
  return (
    <span className="flex flex-wrap gap-1.5">
      {r.quantity === 0 ? <Badge tone="neutral">Немає</Badge> : null}
      {r.isLow && r.quantity > 0 ? <Badge tone="wait">Мало</Badge> : null}
      {r.shortageQuantity > 0 ? <Badge tone="promo">{r.balance < 0 ? "У мінусі" : "Є нестача"}</Badge> : null}
      {r.expiredQuantity > 0 ? <Badge tone="promo">Є прострочене</Badge> : null}
      {r.expiringQuantity > 0 ? <Badge tone="wait">{`Спливає за ${soonDays} діб`}</Badge> : null}
      {!r.isEnabled ? <Badge tone="neutral">Вимкнено в закладі</Badge> : null}
    </span>
  );
}

function LocationStock({ scope, locationId, search, state }: { scope: InventoryScope; locationId: string; search: string; state: StateFilter }) {
  const q = useStock({
    locationId,
    ...(search.trim() ? { search: search.trim() } : {}),
    ...(state === "low" || state === "out_of_stock" ? { state } : {}),
    ...(state === "expiring" ? { expiringWithinDays: scope.expiringSoonDays } : {}),
    take: 50,
  });
  const rows = q.data?.items ?? [];
  const showValue = scope.costs !== "none";
  return (
    <QueryState isPending={q.isPending} isError={q.isError} onRetry={() => q.refetch()}>
      {rows.length === 0 ? (
        <Card>
          <EmptyState>Товарів за цими умовами немає.</EmptyState>
        </Card>
      ) : (
        <Card>
          <Table caption="Залишки закладу" headers={["Товар", "Залишок", "Мін. залишок", "Найближчий термін", "Стан", ...(showValue ? ["Вартість"] : [])]} minWidth={760}>
            {rows.map((r) => (
              <tr key={r.product.id} className={`border-t border-(--line) ${r.isLow || r.shortageQuantity > 0 || r.expiredQuantity > 0 ? "bg-[#FFF8E6]" : ""}`}>
                <th scope="row" className={`${TD} text-left font-semibold`}>
                  {r.product.name}
                  {r.product.category ? <span className="block text-[13px] font-normal text-(--muted)">{r.product.category}</span> : null}
                </th>
                <td className={TD}>
                  {formatQuantity(r.quantity, r.product.unit)}
                  {r.shortageQuantity > 0 ? (
                    <span className="block text-[13px] font-semibold text-[#8A1F1F]" data-testid="stock-shortage">
                      Нестача: {formatQuantity(r.shortageQuantity, r.product.unit)}. Сальдо: {balanceLabel(r.balance, r.product.unit)}
                    </span>
                  ) : null}
                </td>
                <td className={TD}>{r.minStock !== null && r.minStock > 0 ? formatQuantity(r.minStock, r.product.unit) : "—"}</td>
                <td className={TD}>{formatYmd(r.nearestExpiresOn)}</td>
                <td className={TD}>
                  <RowBadges r={r} soonDays={scope.expiringSoonDays} />
                </td>
                {showValue ? <td className={TD}>{valueLabel(r.value, r.valueIncomplete) ?? "—"}</td> : null}
              </tr>
            ))}
          </Table>
          <div className="mt-3">
            <LoadMore hasMore={q.hasNextPage} loading={q.isFetchingNextPage} onClick={() => void q.fetchNextPage()} shown={rows.length} />
          </div>
        </Card>
      )}
    </QueryState>
  );
}

function SummaryStock({ scope, search, state }: { scope: InventoryScope; search: string; state: StateFilter }) {
  const q = useStockSummary({ ...(search.trim() ? { search: search.trim() } : {}), ...(state === "low" ? { lowOnly: true } : {}), take: 50 });
  let rows = q.data?.items ?? [];
  if (state === "out_of_stock") rows = rows.filter((r) => r.totalQuantity === 0);
  const showValue = scope.costs !== "none";
  const locations = q.data?.locations ?? [];
  return (
    <QueryState isPending={q.isPending} isError={q.isError} onRetry={() => q.refetch()}>
      {rows.length === 0 ? (
        <Card>
          <EmptyState>Товарів за цими умовами немає.</EmptyState>
        </Card>
      ) : (
        <Card>
          <Table caption="Залишки по мережі" headers={["Товар", "Усього", "По закладах", ...(showValue ? ["Вартість"] : [])]} minWidth={680}>
            {rows.map((r) => (
              <tr key={r.product.id} className={`border-t border-(--line) ${r.isLowAnywhere ? "bg-[#FFF8E6]" : ""}`}>
                <th scope="row" className={`${TD} text-left font-semibold`}>
                  {r.product.name}
                  {r.isLowAnywhere ? <Badge tone="wait" className="ml-2">Мало десь</Badge> : null}
                </th>
                <td className={TD}>
                  {formatQuantity(r.totalQuantity, r.product.unit)}
                  {r.totalShortage > 0 ? <span className="block text-[13px] font-semibold text-[#8A1F1F]">Нестача: {formatQuantity(r.totalShortage, r.product.unit)}</span> : null}
                </td>
                <td className={TD}>
                  <ul className="m-0 flex list-none flex-col gap-0.5 p-0">
                    {r.locations.map((l) => (
                      <li key={l.locationId}>
                        {locationName(locations, l.locationId)}: {formatQuantity(l.quantity, r.product.unit)}
                        {l.shortageQuantity > 0 ? <span className="font-semibold text-[#8A1F1F]"> (нестача {formatQuantity(l.shortageQuantity, r.product.unit)}, сальдо {balanceLabel(l.balance, r.product.unit)})</span> : null}
                        {l.isLow ? <span className="font-semibold text-[#7A4B00]"> (мало)</span> : null}
                      </li>
                    ))}
                  </ul>
                </td>
                {showValue ? <td className={TD}>{valueLabel(r.value, r.valueIncomplete) ?? "—"}</td> : null}
              </tr>
            ))}
          </Table>
          <div className="mt-3">
            <LoadMore hasMore={q.hasNextPage} loading={q.isFetchingNextPage} onClick={() => void q.fetchNextPage()} shown={rows.length} />
          </div>
        </Card>
      )}
    </QueryState>
  );
}

/** `/beauty/inventory`: залишки. Чіпи «Усі заклади» (`/stock/summary`) + заклади зі scope; фільтри стану. */
export function InventoryStockScreen() {
  const [location, setLocation] = useState<string | null>(null);
  const [search, setSearch] = useState("");
  const [state, setState] = useState<StateFilter>("all");
  const [dialog, setDialog] = useState<OperationKindForm | null>(null);
  const [done, setDone] = useState<string | null>(null);

  return (
    <InventoryPage title="Склад" subtitle="Залишки товарів по закладах, терміни придатності й мінімальні залишки">
      {(scope) => {
        const canManageAny = scope.locations.some((l) => l.canManage);
        const manageableSelected = location === null ? canManageAny : scope.locations.find((l) => l.id === location)?.canManage === true;
        // «Закінчується термін» має сенс лише для окремого закладу (дата береться за його зоною).
        const filters: StateFilter[] = location === null ? ["all", "low", "out_of_stock"] : ["all", "low", "expiring", "out_of_stock"];
        const effective = filters.includes(state) ? state : "all";
        return (
          <>
            <div className="flex flex-wrap items-start justify-between gap-3">
              <LocationChips locations={scope.locations} value={location} onChange={setLocation} />
              {canManageAny ? (
                <div className="flex flex-wrap gap-2" role="group" aria-label="Операції">
                  <PrimaryButton disabled={!manageableSelected} onClick={() => setDialog("receipt")}>
                    Надходження
                  </PrimaryButton>
                  <SecondaryButton disabled={!manageableSelected} onClick={() => setDialog("write_off")}>
                    Списання
                  </SecondaryButton>
                  <SecondaryButton disabled={!manageableSelected} onClick={() => setDialog("transfer")}>
                    Переміщення
                  </SecondaryButton>
                  <Link
                    href="/beauty/inventory/stocktakes"
                    className="inline-flex min-h-11 items-center rounded-[22px] border border-(--line-strong) bg-white px-5 text-sm font-semibold text-(--ink) no-underline"
                  >
                    Інвентаризація
                  </Link>
                </div>
              ) : null}
            </div>
            <div className="grid gap-3 sm:grid-cols-[minmax(0,320px)_1fr] sm:items-end">
              <InputField label="Пошук за назвою чи штрихкодом" value={search} onChange={setSearch} />
              <div className="flex flex-wrap gap-2" role="group" aria-label="Стан залишку">
                {filters.map((f) => (
                  <Chip key={f} pressed={effective === f} onClick={() => setState(f)} variant="soft">
                    {STATE_LABEL[f]}
                  </Chip>
                ))}
              </div>
            </div>
            {done ? <SuccessBanner>{done}</SuccessBanner> : null}
            {location === null ? <SummaryStock scope={scope} search={search} state={effective} /> : <LocationStock scope={scope} locationId={location} search={search} state={effective} />}
            {dialog ? (
              <OperationDialog
                kind={dialog}
                scope={scope}
                defaultLocationId={location}
                onClose={() => setDialog(null)}
                onDone={() => setDone(DONE_TEXT[dialog])}
              />
            ) : null}
          </>
        );
      }}
    </InventoryPage>
  );
}
