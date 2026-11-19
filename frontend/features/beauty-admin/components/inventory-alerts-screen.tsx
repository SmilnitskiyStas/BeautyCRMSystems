"use client";

import Link from "next/link";
import { useState } from "react";
import { dateTimeLabel } from "../format";
import { useMarkAlertRead, useMarkAllAlertsRead, useStaffAlerts } from "../hooks/use-staff-alerts";
import { ALERT_KIND_LABEL, ALERT_STATUS_LABEL, alertSummary, humanizeInventoryError } from "../inventory/logic";
import type { AlertKind, AlertStatusFilter, StaffAlert } from "../inventory/types";
import { TelegramSettingsLink } from "./telegram-notifications-screen";
import { InventoryPage, LoadMore, LocationChips } from "./inventory-parts";
import { Badge, Card, Chip, EmptyState, QueryState, SecondaryButton, Table, TD } from "./ui";

const STATUS_FILTERS: { id: AlertStatusFilter; label: string }[] = [
  { id: "active", label: "Активні" },
  { id: "open", label: "Нові" },
  { id: "read", label: "Прочитані" },
  { id: "resolved", label: "Розв’язані" },
  { id: "all", label: "Усі" },
];
const KIND_FILTERS: { id: AlertKind | null; label: string }[] = [
  { id: null, label: "Усі види" },
  { id: "low_stock", label: ALERT_KIND_LABEL.low_stock },
  { id: "expiring", label: ALERT_KIND_LABEL.expiring },
  { id: "shortage", label: ALERT_KIND_LABEL.shortage },
];
const KIND_TONE = { low_stock: "wait", expiring: "promo", shortage: "promo" } as const;
const STATUS_TONE = { open: "now", read: "neutral", resolved: "ok" } as const;

/** Посилання «куди дивитись»: нестача й низький залишок - залишки, термін - партії; завжди ще й каталог товарів. */
function alertLinks(a: StaffAlert): { href: string; label: string }[] {
  const main = a.kind === "expiring" ? { href: "/beauty/inventory/batches", label: `Партії: ${a.product.name}` } : { href: "/beauty/inventory", label: `Залишки: ${a.location.name}` };
  return [main, { href: "/beauty/inventory/products", label: `Товар: ${a.product.name}` }];
}

function AlertsList({ status, kind, locationId }: { status: AlertStatusFilter; kind: AlertKind | null; locationId: string | null }) {
  const q = useStaffAlerts({ status, ...(kind ? { kind } : {}), ...(locationId ? { locationId } : {}), take: 50 });
  const read = useMarkAlertRead();
  const readAll = useMarkAllAlertsRead();
  const [note, setNote] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const hasOpen = q.items.some((a) => a.status === "open");

  const markOne = (a: StaffAlert) => {
    setError(null);
    setNote(null);
    read.mutate(a.id, {
      onSuccess: () => setNote(`Позначено прочитаним: ${a.product.name}.`),
      onError: (e) => setError(humanizeInventoryError(e)),
    });
  };
  const markAll = () => {
    setError(null);
    setNote(null);
    readAll.mutate(locationId ?? undefined, {
      onSuccess: (updated) => setNote(updated > 0 ? `Позначено прочитаними: ${updated}.` : "Нових сповіщень немає."),
      onError: (e) => setError(humanizeInventoryError(e)),
    });
  };

  return (
    <>
      <div className="flex flex-wrap items-center gap-3">
        <SecondaryButton onClick={markAll} disabled={readAll.isPending || !hasOpen}>
          {readAll.isPending ? "Позначаємо…" : locationId ? "Позначити всі прочитаними в закладі" : "Позначити всі прочитаними"}
        </SecondaryButton>
        <p role="status" className="m-0 text-sm text-(--muted)">
          {note}
        </p>
      </div>
      {error ? (
        <p role="alert" className="rounded-xl bg-[#FBE4E4] px-3.5 py-2.5 text-sm text-[#8A1F1F]">
          {error}
        </p>
      ) : null}
      <QueryState isPending={q.isPending} isError={q.isError} onRetry={() => q.refetch()}>
        {q.items.length === 0 ? (
          <Card>
            <EmptyState>Сповіщень за цими умовами немає.</EmptyState>
          </Card>
        ) : (
          <Card>
            <Table caption="Сповіщення складу" headers={["Вид і стан", "Заклад", "Товар", "Що сталося", "Створено", "Дії"]} minWidth={860}>
              {q.items.map((a) => (
                <tr key={a.id} data-testid="alert-row" className={`border-t border-(--line) align-top ${a.status === "open" ? "bg-[#FFF8E6]" : ""}`}>
                  <td className={TD}>
                    <span className="flex flex-col items-start gap-1.5">
                      <Badge tone={KIND_TONE[a.kind]}>{ALERT_KIND_LABEL[a.kind]}</Badge>
                      <Badge tone={STATUS_TONE[a.status]}>{ALERT_STATUS_LABEL[a.status]}</Badge>
                    </span>
                  </td>
                  <td className={TD}>{a.location.name}</td>
                  <th scope="row" className={`${TD} text-left font-semibold`}>
                    {a.product.name}
                  </th>
                  <td className={TD}>{alertSummary(a)}</td>
                  <td className={TD}>
                    {dateTimeLabel(a.createdAt)}
                    {a.readBy ? <span className="block text-[13px] text-(--muted)">Прочитав(ла): {a.readBy.name}</span> : null}
                  </td>
                  <td className={TD}>
                    <span className="flex flex-col items-start gap-1.5">
                      {a.status === "open" ? (
                        <SecondaryButton onClick={() => markOne(a)} disabled={read.isPending} aria-label={`Позначити прочитаним: ${ALERT_KIND_LABEL[a.kind]}, ${a.product.name}, ${a.location.name}`}>
                          Прочитано
                        </SecondaryButton>
                      ) : null}
                      {alertLinks(a).map((l) => (
                        <Link key={l.href} href={l.href} className="inline-flex min-h-11 items-center text-sm font-semibold text-(--accent) underline">
                          {l.label}
                        </Link>
                      ))}
                    </span>
                  </td>
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
  );
}

/** `/beauty/inventory/alerts` (§32.19): сповіщення складу - низький залишок, термін, нестача. Спільний стан «прочитано» на tenant. */
export function InventoryAlertsScreen() {
  const [status, setStatus] = useState<AlertStatusFilter>("active");
  const [kind, setKind] = useState<AlertKind | null>(null);
  const [location, setLocation] = useState<string | null>(null);
  return (
    <InventoryPage title="Сповіщення складу" subtitle="Низький залишок, терміни придатності та нестача після витрати на послуги">
      {(scope) => (
        <>
          <TelegramSettingsLink />
          <LocationChips locations={scope.locations} value={location} onChange={setLocation} />
          <div className="flex flex-wrap gap-2" role="group" aria-label="Стан сповіщення">
            {STATUS_FILTERS.map((f) => (
              <Chip key={f.id} pressed={status === f.id} onClick={() => setStatus(f.id)} variant="soft">
                {f.label}
              </Chip>
            ))}
          </div>
          <div className="flex flex-wrap gap-2" role="group" aria-label="Вид сповіщення">
            {KIND_FILTERS.map((f) => (
              <Chip key={f.id ?? "all"} pressed={kind === f.id} onClick={() => setKind(f.id)} variant="soft">
                {f.label}
              </Chip>
            ))}
          </div>
          <AlertsList key={`${status}|${kind}|${location}`} status={status} kind={kind} locationId={location} />
        </>
      )}
    </InventoryPage>
  );
}
