"use client";

import { useRef, useState } from "react";
import { useAuth } from "@/features/beauty-auth/components/auth-provider";
import { canEditCosts } from "@/features/beauty-auth/permissions";
import { durationLabel, moneyExact } from "../format";
import { usePriceList } from "../hooks/use-beauty-admin";
import { useServiceCosts } from "../hooks/use-finance";
import { costSummary } from "../service-costs";
import { Badge, Card, PageHeader, QueryState, SecondaryButton, Table, TD } from "./ui";
import { ServiceCostEditor } from "./service-cost-editor";

function ServicesView() {
  const costs = useServiceCosts();
  const prices = usePriceList();
  const [selected, setSelected] = useState<string | null>(null);
  const openers = useRef(new Map<string, HTMLButtonElement>());
  const rows = costs.data ?? [];
  const priceOf = (id: string) => prices.data?.find((p) => p.serviceId === id);
  const current = rows.find((r) => r.serviceId === selected);

  function close() {
    const id = selected;
    setSelected(null);
    // Фокус повертається на кнопку рядка після закриття редактора.
    if (id) setTimeout(() => openers.current.get(id)?.focus(), 0);
  }

  return (
    <>
      <PageHeader title="Послуги" subtitle="Собівартість і виплата майстру: для чистого доходу в аналітиці. Бачать і змінюють лише власник та адміністратор." />
      <QueryState isPending={costs.isPending} isError={costs.isError} onRetry={() => costs.refetch()}>
        <Card>
          <Table headers={["Послуга", "Тривалість", "Ціна (мережа)", "Собівартість", "Заклади", ""]} minWidth={720}>
            {rows.map((r) => {
              const p = priceOf(r.serviceId);
              return (
                <tr key={r.serviceId} data-testid="service-row" className="border-t border-(--line)">
                  <td className={`${TD} font-semibold`}>{r.serviceName}</td>
                  <td className={`${TD} text-(--muted)`}>{p ? durationLabel(p.durationMinutes) : "—"}</td>
                  <td className={TD}>{p ? moneyExact(p.price) : "—"}</td>
                  <td className={TD} data-testid="service-cost-cell">
                    {costSummary(r.network)}
                  </td>
                  <td className={TD}>{r.locations.length > 0 ? <Badge tone="now">окремо: {r.locations.length}</Badge> : <span className="text-(--muted)">—</span>}</td>
                  <td className={TD}>
                    <SecondaryButton
                      ref={(el) => {
                        if (el) openers.current.set(r.serviceId, el);
                      }}
                      aria-label={`Собівартість: ${r.serviceName}`}
                      aria-expanded={selected === r.serviceId}
                      onClick={() => setSelected(r.serviceId)}
                    >
                      Змінити
                    </SecondaryButton>
                  </td>
                </tr>
              );
            })}
          </Table>
          {rows.length === 0 ? <p className="mt-3 text-sm text-(--muted)">Послуг ще немає.</p> : null}
        </Card>
      </QueryState>
      {current ? <ServiceCostEditor key={current.serviceId} serviceId={current.serviceId} serviceName={current.serviceName} price={priceOf(current.serviceId)?.price} onClose={close} /> : null}
    </>
  );
}

export function ServicesScreen() {
  const { user } = useAuth();
  if (!canEditCosts(user.role)) {
    return (
      <>
        <PageHeader title="Послуги" />
        <Card>
          <p role="note" className="text-sm">
            Собівартість послуг доступна лише власнику й адміністратору.
          </p>
        </Card>
      </>
    );
  }
  return <ServicesView />;
}
