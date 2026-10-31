"use client";

import { useState } from "react";
import { useAuth } from "@/features/beauty-auth/components/auth-provider";
import { BeautyApiError } from "@/features/beauty-auth/errors";
import { canUseMarketing } from "@/features/beauty-auth/permissions";
import { downloadCsv } from "../analytics/csv";
import { dateTimeLabel, money } from "../format";
import { useCampaign, useCampaignCouponSummary, useCampaignStats, useCancelCampaign, usePromotions } from "../hooks/use-marketing";
import { useLocations, usePriceList } from "../hooks/use-beauty-admin";
import { STATUS_LABEL, STATUS_TONE, SEGMENTS, TYPE_LABEL, campaignStatsCsv, humanizeMarketingError, isMarketingDisabled, skipLabel } from "../marketing/logic";
import type { CampaignDto, CampaignStats } from "../types";
import { CampaignWizard } from "./campaign-wizard";
import { ErrorLine, MarketingDisabledNotice, MarketingForbiddenNotice } from "./marketing-notices";
import { ModalDialog } from "./modal-dialog";
import { Badge, Card, KeyValue, KpiGrid, PageHeader, PrimaryButton, ProgressBar, QueryState, SecondaryButton, TextLink } from "./ui";

const COUPON_MODE_TEXT = { none: "Без купона", personal: "Персональні одноразові коди", shared: "Загальний код" } as const;

function segmentSummary(c: CampaignDto, serviceName?: string): string {
  const s = c.segment;
  const label = SEGMENTS.find((x) => x.kind === s.kind)?.label ?? s.kind;
  if (s.kind === "lapsed") return `${label}: ${s.days} дн.${serviceName ? `, послуга «${serviceName}»` : ""}`;
  if (s.kind === "frequent") return `${label}: від ${s.minVisits} візитів за ${s.periodDays} дн.`;
  return label;
}

function StatsBlock({ stats, coupons }: { stats: CampaignStats; coupons?: { issued: number; redeemedActive: number; released: number; revoked: number } }) {
  const failed = stats.byStatus.failed;
  const skippedEntries = Object.entries(stats.skipped).filter(([, n]) => n > 0);
  return (
    <div className="flex flex-col gap-4">
      <KpiGrid
        min={170}
        items={[
          { label: "Одержувачів", value: String(stats.recipients), note: "у знімку на момент підтвердження" },
          { label: "Відправлено", value: String(stats.byStatus.sent), note: `очікують: ${stats.byStatus.pending + stats.byStatus.queued}` },
          {
            label: "Помилки доставки",
            value: String(failed),
            note: failed > 0 ? `бот заблоковано: ${stats.failed.botBlocked}, чат не знайдено: ${stats.failed.chatNotFound}, інше: ${stats.failed.other}` : "немає",
          },
          { label: "Пропущено", value: String(stats.byStatus.skipped), note: "клієнти, яким не надсилали" },
          { label: "Натиснули «Записатися»", value: String(stats.clicks), note: "кнопка під повідомленням" },
          { label: "Записів з розсилки", value: String(stats.bookings), note: `з них за купоном: ${stats.bookingsWithCoupon}` },
          { label: "Записи за купоном", value: String(stats.couponRedemptionsActive), note: "активні використання" },
          { label: "Виручка з розсилки", value: money(stats.revenue), note: "завершені записи" },
          { label: "Відписались", value: String(stats.unsubscribedAfter), note: "після розсилки" },
        ]}
      />
      {skippedEntries.length > 0 ? (
        <ul aria-label="Причини, чому не надсилали" className="m-0 flex list-none flex-col gap-1 p-0 text-sm">
          {skippedEntries.map(([k, n]) => (
            <li key={k} className="flex justify-between gap-3 border-t border-(--line) pt-1.5">
              <span>{skipLabel(k)}</span>
              <strong>{n}</strong>
            </li>
          ))}
        </ul>
      ) : null}
      {coupons ? (
        <p data-testid="coupon-summary" className="m-0 text-sm text-(--muted)">
          Купони: видано {coupons.issued}, використано {coupons.redeemedActive}, повернено {coupons.released}, відкликано {coupons.revoked}.
        </p>
      ) : null}
      <p data-testid="viewed-note" className="m-0 rounded-xl bg-(--page) p-3 text-[13px] text-(--muted)">
        Показник «Переглянуто» недоступний: Telegram не повідомляє про перегляди й прочитання повідомлень.
      </p>
    </div>
  );
}

/** `/beauty/campaigns/{id}`: чернетка відкривається в майстрі, решта - статус, прогрес і статистика (§26.11.5). */
export function CampaignDetailScreen({ id }: { id: string }) {
  const { user } = useAuth();
  const allowed = canUseMarketing(user.role);
  const q = useCampaign(id, allowed);
  const promos = usePromotions(allowed);
  const locations = useLocations(allowed);
  const services = usePriceList();
  const c = q.data;
  const stats = useCampaignStats(id, c?.status, allowed);
  const summary = useCampaignCouponSummary(id, allowed && !!c && c.status !== "draft" && c.couponMode !== "none");
  const cancel = useCancelCampaign();
  const [askCancel, setAskCancel] = useState(false);

  if (!allowed) return <MarketingForbiddenNotice />;
  if (q.isError && isMarketingDisabled(q.error)) {
    return (
      <>
        <TextLink href="/beauty/campaigns">← Усі розсилки</TextLink>
        <MarketingDisabledNotice />
      </>
    );
  }
  if (q.isError && q.error instanceof BeautyApiError && q.error.status === 404) {
    return (
      <>
        <TextLink href="/beauty/campaigns">← Усі розсилки</TextLink>
        <Card><p role="alert" className="m-0 text-sm">Розсилку не знайдено. Можливо, її видалено.</p></Card>
      </>
    );
  }
  if (c?.status === "draft") return <CampaignWizard campaign={c} />;

  const promo = promos.data?.find((p) => p.id === c?.promotionId);
  const serviceName = services.data?.find((s) => s.serviceId === c?.segment.serviceId)?.name;
  const sent = stats.data?.byStatus.sent ?? 0;
  const total = c?.recipientsTotal ?? stats.data?.recipients ?? 0;
  const pct = total > 0 ? Math.min(100, Math.round((sent / total) * 100)) : 0;
  const cancellable = c?.status === "scheduled" || c?.status === "sending";

  return (
    <>
      <TextLink href="/beauty/campaigns">← Усі розсилки</TextLink>
      <QueryState isPending={q.isPending} isError={q.isError} onRetry={() => q.refetch()}>
        {c ? (
          <>
            <PageHeader
              title={c.name}
              subtitle={
                <span className="flex flex-wrap items-center gap-2">
                  <Badge tone={STATUS_TONE[c.status]}>
                    <span data-testid="campaign-status">{STATUS_LABEL[c.status]}</span>
                  </Badge>
                  <span>{TYPE_LABEL[c.type]}</span>
                </span>
              }
              actions={
                cancellable ? <SecondaryButton onClick={() => setAskCancel(true)}>Скасувати кампанію</SecondaryButton> : null
              }
            />

            <div className="flex flex-wrap items-start gap-6">
              <Card className="flex flex-[1_1_340px] flex-col gap-2.5">
                <h2 className="text-lg font-semibold">Про розсилку</h2>
                <KeyValue label="Акція" value={promo?.name ?? (c.type === "news" ? "Новина без акції" : "—")} />
                <KeyValue label="Аудиторія" value={segmentSummary(c, serviceName)} />
                <KeyValue label="Заклад" value={c.locationId ? (locations.data?.find((l) => l.id === c.locationId)?.name ?? "—") : "Усі заклади"} />
                <KeyValue label="Купон" value={COUPON_MODE_TEXT[c.couponMode]} />
                {c.scheduledAt ? <KeyValue label={c.status === "scheduled" ? "Старт" : "Заплановано на"} value={dateTimeLabel(c.scheduledAt)} /> : null}
                {c.startedAt ? <KeyValue label="Почато" value={dateTimeLabel(c.startedAt)} /> : null}
                {c.finishedAt ? <KeyValue label="Завершено" value={dateTimeLabel(c.finishedAt)} /> : null}
                {c.confirmedAt ? <KeyValue label="Підтвердив(ла)" value={`${c.confirmedBy?.name ?? "—"}, ${dateTimeLabel(c.confirmedAt)}`} /> : null}
                {c.cancelledAt ? <KeyValue label="Скасував(ла)" value={`${c.cancelledBy?.name ?? "—"}, ${dateTimeLabel(c.cancelledAt)}`} /> : null}
                <div className="text-[13px] font-semibold text-(--muted)">Шаблон тексту</div>
                <div className="whitespace-pre-wrap break-words rounded-xl bg-(--page) p-3 text-sm">{c.template}</div>
              </Card>

              <Card className="flex flex-[999_1_480px] flex-col gap-4">
                <h2 className="text-lg font-semibold">Прогрес і статистика</h2>
                {total > 0 ? (
                  <div className="flex flex-col gap-1.5">
                    <div className="flex justify-between text-sm">
                      <span aria-live="polite" data-testid="progress-text">Надіслано {sent} з {total}</span>
                      <span>{pct}%</span>
                    </div>
                    <ProgressBar pct={pct} />
                  </div>
                ) : null}
                {stats.isPending && stats.fetchStatus !== "idle" ? <div role="status" className="text-sm text-(--muted)">Завантаження статистики…</div> : null}
                {stats.isError ? <ErrorLine error={stats.error} fallback="Не вдалося завантажити статистику." onRetry={() => stats.refetch()} /> : null}
                {stats.data ? (
                  <>
                    <StatsBlock stats={stats.data} coupons={c.couponMode !== "none" ? summary.data : undefined} />
                    <div>
                      <SecondaryButton onClick={() => downloadCsv(campaignStatsCsv(c, stats.data!, c.couponMode !== "none" ? summary.data : undefined))}>
                        Завантажити CSV
                      </SecondaryButton>
                    </div>
                  </>
                ) : null}
              </Card>
            </div>

            {cancel.isError && !askCancel ? <ErrorLine error={cancel.error} /> : null}
            {cancel.isSuccess ? (
              <p role="status" className="m-0 rounded-xl bg-[#DDF3E6] px-3.5 py-2.5 text-sm text-[#145A32]">Кампанію скасовано.</p>
            ) : null}

            {askCancel ? (
              <ModalDialog
                title="Скасувати кампанію?"
                busy={cancel.isPending}
                onClose={() => setAskCancel(false)}
                actions={
                  <>
                    <PrimaryButton
                      disabled={cancel.isPending}
                      onClick={() => cancel.mutate(id, { onSuccess: () => setAskCancel(false) })}
                    >
                      {cancel.isPending ? "Скасовуємо…" : "Скасувати кампанію"}
                    </PrimaryButton>
                    <SecondaryButton disabled={cancel.isPending} onClick={() => setAskCancel(false)}>Не скасовувати</SecondaryButton>
                  </>
                }
              >
                <p className="m-0">Повідомлення, які ще не надіслано, не підуть. Уже доставлені повідомлення і купони в них лишаються чинними.</p>
                {cancel.isError ? <p role="alert" className="m-0 text-[#8A1F1F]">{humanizeMarketingError(cancel.error)}</p> : null}
              </ModalDialog>
            ) : null}
          </>
        ) : null}
      </QueryState>
    </>
  );
}
