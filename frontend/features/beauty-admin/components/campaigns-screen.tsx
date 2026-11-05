"use client";

import { useState } from "react";
import { useAuth } from "@/features/beauty-auth/components/auth-provider";
import { canUseMarketing } from "@/features/beauty-auth/permissions";
import { dateTimeLabel } from "../format";
import { useCampaigns } from "../hooks/use-marketing";
import { CAMPAIGN_STATUSES, STATUS_LABEL, STATUS_TONE, TYPE_LABEL, isMarketingDisabled } from "../marketing/logic";
import type { CampaignStatus } from "../types";
import { ErrorLine, MarketingDisabledNotice, MarketingForbiddenNotice } from "./marketing-notices";
import { Badge, Card, EmptyState, LinkButton, PageHeader, RowLink, SecondaryButton, Table, TD, Tabs } from "./ui";

type Filter = CampaignStatus | "all";

/** `/beauty/campaigns` (§26.12): список розсилок зі статусами; специфікація - лише owner/admin. */
export function CampaignsScreen() {
  const { user } = useAuth();
  const allowed = canUseMarketing(user.role);
  const [filter, setFilter] = useState<Filter>("all");
  const q = useCampaigns(filter, allowed);

  if (!allowed) return <MarketingForbiddenNotice />;
  const items = q.data?.pages.flatMap((p) => p.items) ?? [];

  return (
    <>
      <PageHeader title="Розсилки" subtitle="Акції та новини клієнтам у Telegram: тільки за згодою і лише після вашого підтвердження." actions={q.isError && isMarketingDisabled(q.error) ? null : <LinkButton href="/beauty/campaigns/new">Нова розсилка</LinkButton>} />
      {q.isError && isMarketingDisabled(q.error) ? (
        <MarketingDisabledNotice />
      ) : (
        <Card className="flex flex-col gap-4">
          <Tabs
            value={filter}
            onChange={setFilter}
            tabs={[{ id: "all" as Filter, label: "Усі" }, ...CAMPAIGN_STATUSES.map((s) => ({ id: s as Filter, label: STATUS_LABEL[s] }))]}
          />
          {q.isPending ? (
            <div role="status" aria-live="polite" className="text-sm text-(--muted)">
              Завантаження…
            </div>
          ) : q.isError ? (
            <ErrorLine error={q.error} fallback="Не вдалося завантажити розсилки." onRetry={() => q.refetch()} />
          ) : items.length === 0 ? (
            <EmptyState>
              {filter === "all" ? "Розсилок ще немає. Створіть першу: оберіть акцію, аудиторію й текст." : "Немає розсилок з таким статусом."}
            </EmptyState>
          ) : (
            <>
              <Table headers={["Назва", "Акція", "Статус", "Одержувачів", "Надіслано", "Дата"]} minWidth={640}>
                {items.map((c) => (
                  <tr key={c.id} data-testid="campaign-row" className="border-t border-(--line)">
                    <td className={`${TD} font-semibold`}>
                      <RowLink href={`/beauty/campaigns/${c.id}`}>{c.name}</RowLink>
                    </td>
                    <td className={`${TD} text-(--muted)`}>{c.promotion?.name ?? (c.type === "news" ? TYPE_LABEL.news : "—")}</td>
                    <td className={TD}>
                      <Badge tone={STATUS_TONE[c.status]}>{STATUS_LABEL[c.status]}</Badge>
                    </td>
                    <td className={TD}>{c.recipientsTotal ?? "—"}</td>
                    <td className={TD}>{c.status === "draft" || c.status === "scheduled" ? "—" : c.sent}</td>
                    <td className={`${TD} text-(--muted)`}>{c.scheduledAt ? dateTimeLabel(c.scheduledAt) : dateTimeLabel(c.createdAt)}</td>
                  </tr>
                ))}
              </Table>
              {q.hasNextPage ? (
                <div>
                  <SecondaryButton disabled={q.isFetchingNextPage} onClick={() => void q.fetchNextPage()}>
                    {q.isFetchingNextPage ? "Завантажуємо…" : "Показати ще"}
                  </SecondaryButton>
                </div>
              ) : null}
            </>
          )}
        </Card>
      )}
    </>
  );
}
