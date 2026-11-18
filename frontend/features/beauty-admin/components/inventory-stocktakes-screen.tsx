"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useState } from "react";
import { BeautyApiError } from "@/features/beauty-auth/errors";
import { useCreateStocktake, useStocktakes } from "../hooks/use-inventory";
import { humanizeInventoryError } from "../inventory/logic";
import type { StocktakeStatus } from "../inventory/types";
import { dateTimeLabel } from "../format";
import { InventoryPage, LoadMore, LocationChips, SelectField, locationName } from "./inventory-parts";
import { ErrorBanner } from "./staff-parts";
import { Badge, Card, EmptyState, PrimaryButton, QueryState, Table, TD } from "./ui";

export const STOCKTAKE_STATUS: Record<StocktakeStatus, { label: string; tone: "wait" | "ok" | "neutral" }> = {
  draft: { label: "Чернетка", tone: "wait" },
  posted: { label: "Проведено", tone: "ok" },
  cancelled: { label: "Скасовано", tone: "neutral" },
};

/** `/beauty/inventory/stocktakes`: список інвентаризацій; нова створює чернетку (одна на заклад, `409 stocktake_in_progress`). */
export function InventoryStocktakesScreen() {
  const router = useRouter();
  const [location, setLocation] = useState<string | null>(null);
  const [status, setStatus] = useState("");
  const [newLocation, setNewLocation] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [resumeId, setResumeId] = useState<string | null>(null);
  const create = useCreateStocktake();
  const q = useStocktakes({ ...(location ? { locationId: location } : {}), ...(status ? { status: status as StocktakeStatus } : {}), take: 50 });

  async function start(defaultLocationId: string) {
    setError(null);
    setResumeId(null);
    const locationId = newLocation || defaultLocationId;
    try {
      const st = await create.mutateAsync({ locationId });
      router.push(`/beauty/inventory/stocktakes/${encodeURIComponent(st.id)}`);
    } catch (e) {
      if (e instanceof BeautyApiError && e.code === "stocktake_in_progress" && typeof e.body?.stocktakeId === "string") setResumeId(e.body.stocktakeId);
      setError(humanizeInventoryError(e, "Не вдалося створити інвентаризацію. Спробуйте ще раз."));
    }
  }

  return (
    <InventoryPage title="Інвентаризації" subtitle="Підрахунок фактичних залишків. Різниці проводяться рухами, історія не змінюється">
      {(scope) => {
        const manageable = scope.locations.filter((l) => l.canManage);
        return (
          <>
            {manageable.length > 0 ? (
              <Card as="div" className="flex flex-wrap items-end gap-3">
                <div className="min-w-[220px] flex-1">
                  <SelectField
                    label="Заклад нової інвентаризації"
                    value={newLocation || manageable[0]!.id}
                    onChange={setNewLocation}
                    options={manageable.map((l) => ({ value: l.id, label: l.name }))}
                  />
                </div>
                <PrimaryButton disabled={create.isPending} onClick={() => void start(manageable[0]!.id)}>
                  {create.isPending ? "Створюємо…" : "Нова інвентаризація"}
                </PrimaryButton>
              </Card>
            ) : null}
            <div className="flex flex-col gap-2">
              {error ? <ErrorBanner>{error}</ErrorBanner> : null}
              {resumeId ? (
                <Link href={`/beauty/inventory/stocktakes/${encodeURIComponent(resumeId)}`} className="inline-flex min-h-11 items-center text-sm font-semibold text-(--accent) underline">
                  Відкрити поточну інвентаризацію
                </Link>
              ) : null}
            </div>
            <LocationChips locations={scope.locations} value={location} onChange={setLocation} />
            <div className="max-w-[260px]">
              <SelectField
                label="Статус"
                value={status}
                onChange={setStatus}
                placeholder="Усі"
                options={(Object.keys(STOCKTAKE_STATUS) as StocktakeStatus[]).map((s) => ({ value: s, label: STOCKTAKE_STATUS[s].label }))}
              />
            </div>
            <QueryState isPending={q.isPending} isError={q.isError} onRetry={() => q.refetch()}>
              {q.items.length === 0 ? (
                <Card>
                  <EmptyState>Інвентаризацій ще немає.</EmptyState>
                </Card>
              ) : (
                <Card>
                  <Table caption="Інвентаризації" headers={["Створено", "Заклад", "Статус", "Рахунок", "Хто створив", "Дії"]} minWidth={720}>
                    {q.items.map((s) => (
                      <tr key={s.id} className="border-t border-(--line)">
                        <td className={TD}>{dateTimeLabel(s.createdAt, scope.locations.find((l) => l.id === s.locationId)?.timezone)}</td>
                        <td className={TD}>{locationName(scope.locations, s.locationId)}</td>
                        <td className={TD}>
                          <Badge tone={STOCKTAKE_STATUS[s.status].tone}>{STOCKTAKE_STATUS[s.status].label}</Badge>
                        </td>
                        <td className={TD}>
                          {s.countedCount} із {s.lineCount}
                        </td>
                        <td className={TD}>{s.createdBy.name || "—"}</td>
                        <td className={TD}>
                          <Link
                            href={`/beauty/inventory/stocktakes/${encodeURIComponent(s.id)}`}
                            aria-label={`Відкрити інвентаризацію від ${dateTimeLabel(s.createdAt)}`}
                            className="inline-flex min-h-11 items-center text-sm font-semibold text-(--accent) underline"
                          >
                            Відкрити
                          </Link>
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
      }}
    </InventoryPage>
  );
}
