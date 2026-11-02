"use client";

import { useId, useState } from "react";
import { dateRangeLabel } from "../format";
import { usePromotions, useRevokeCoupon, useSharedCoupons } from "../hooks/use-marketing";
import { dmy, formatDiscount, humanizeMarketingError, isMarketingDisabled } from "../marketing/logic";
import type { PromoAccess } from "../types";
import { ErrorLine } from "./marketing-notices";
import { ModalDialog } from "./modal-dialog";
import { Badge, Card, EmptyState, FIELD_INPUT, FIELD_LABEL, LinkButton, PrimaryButton, SecondaryButton, Table, TD } from "./ui";

export interface AccessValue {
  access: PromoAccess;
  code: string;
  maxUses: string;
  from: string;
  to: string;
}

/** Поле «Доступ» акції (§26.6, §26.12) + загальний код: код, ліміт використань, строк. */
export function AccessFields({
  value,
  onChange,
  errors,
  marketingOn,
}: {
  value: AccessValue;
  onChange: (v: AccessValue) => void;
  errors: { code?: string; maxUses?: string; period?: string };
  marketingOn: boolean;
}) {
  const ids = { code: useId(), max: useId(), from: useId(), to: useId() };
  const set = (patch: Partial<AccessValue>) => onChange({ ...value, ...patch });
  return (
    <>
      <fieldset className="m-0 flex min-w-0 flex-col gap-2 border-0 p-0">
        <legend className={`${FIELD_LABEL} mb-2 p-0`}>Доступ</legend>
        <label className="flex min-h-11 cursor-pointer items-start gap-3 text-[15px]">
          <input type="radio" name="promo-access" checked={value.access === "public"} onChange={() => set({ access: "public" })} className="mt-1" />
          <span className="flex flex-col">
            <span className="font-semibold">Для всіх</span>
            <span className="text-[13px] text-(--muted)">Знижку бачать усі клієнти в публічному записі.</span>
          </span>
        </label>
        {marketingOn ? (
          <label className="flex min-h-11 cursor-pointer items-start gap-3 text-[15px]">
            <input type="radio" name="promo-access" checked={value.access === "code_only"} onChange={() => set({ access: "code_only" })} className="mt-1" />
            <span className="flex flex-col">
              <span className="font-semibold">Лише за кодом (купон)</span>
              <span className="text-[13px] text-(--muted)">Акція не видна в каталозі й діє тільки за промокодом.</span>
            </span>
          </label>
        ) : (
          <p data-testid="code-only-unavailable" className="m-0 text-[13px] text-(--muted)">
            Акції «Лише за кодом» недоступні: розсилки й купони не підключені для вашого закладу.
          </p>
        )}
      </fieldset>

      {value.access === "code_only" && marketingOn ? (
        <fieldset className="m-0 flex min-w-0 flex-col gap-3 rounded-xl border border-(--line-strong) p-3.5">
          <legend className={`${FIELD_LABEL} px-1`}>Загальний код</legend>
          <div className="flex flex-col gap-1.5">
            <label htmlFor={ids.code} className={FIELD_LABEL}>Код (4–20 символів, латиниця й цифри)</label>
            <input
              id={ids.code}
              type="text"
              autoComplete="off"
              maxLength={20}
              value={value.code}
              onChange={(e) => set({ code: e.target.value.toUpperCase() })}
              aria-invalid={errors.code ? "true" : "false"}
              aria-describedby={errors.code ? `${ids.code}-e` : undefined}
              className={FIELD_INPUT}
            />
            {errors.code ? <span id={`${ids.code}-e`} className="text-[13px] text-[#8A1F1F]">{errors.code}</span> : null}
          </div>
          <div className="flex flex-col gap-1.5">
            <label htmlFor={ids.max} className={FIELD_LABEL}>Ліміт використань (необов&apos;язково)</label>
            <input
              id={ids.max}
              type="number"
              inputMode="numeric"
              min={1}
              max={100000}
              value={value.maxUses}
              onChange={(e) => set({ maxUses: e.target.value })}
              aria-invalid={errors.maxUses ? "true" : "false"}
              className={FIELD_INPUT}
            />
            {errors.maxUses ? <span className="text-[13px] text-[#8A1F1F]">{errors.maxUses}</span> : null}
          </div>
          <div className="flex flex-wrap gap-3">
            <div className="flex min-w-[140px] flex-1 flex-col gap-1.5">
              <label htmlFor={ids.from} className={FIELD_LABEL}>Код діє з</label>
              <input id={ids.from} type="date" value={value.from} onChange={(e) => set({ from: e.target.value })} className={FIELD_INPUT} />
            </div>
            <div className="flex min-w-[140px] flex-1 flex-col gap-1.5">
              <label htmlFor={ids.to} className={FIELD_LABEL}>Код діє до (включно)</label>
              <input id={ids.to} type="date" value={value.to} onChange={(e) => set({ to: e.target.value })} className={FIELD_INPUT} />
            </div>
          </div>
          {errors.period ? <span role="alert" className="text-[13px] text-[#8A1F1F]">{errors.period}</span> : null}
          <p className="m-0 text-[13px] text-(--muted)">Персональні одноразові коди для кожного клієнта створюються під час розсилки.</p>
        </fieldset>
      ) : null}
    </>
  );
}

/** «Мої акції»: доступ, активні розсилки та перехід у майстер розсилки з вибраною акцією. */
export function PromotionsCard({ marketingOn }: { marketingOn: boolean }) {
  const q = usePromotions();
  const list = q.data ?? [];
  const inCampaign = list.some((p) => p.activeCampaigns > 0);
  return (
    <Card as="div" className="flex flex-col gap-3">
      <h2 className="text-lg font-semibold">Мої акції</h2>
      {q.isPending ? <div role="status" className="text-sm text-(--muted)">Завантаження…</div> : null}
      {q.isError && !isMarketingDisabled(q.error) ? <ErrorLine error={q.error} onRetry={() => q.refetch()} /> : null}
      {q.isSuccess && list.length === 0 ? <EmptyState>Акцій ще немає.</EmptyState> : null}
      <ul className="m-0 flex list-none flex-col gap-2.5 p-0">
        {list.map((p) => (
          <li key={p.id} data-testid="promotion-item" className="flex flex-wrap items-center justify-between gap-3 rounded-xl border border-(--line) p-3.5">
            <div className="flex min-w-0 flex-col gap-1">
              <span className="flex flex-wrap items-center gap-2 text-[15px] font-semibold">
                {p.name}
                {p.access === "code_only" ? <Badge tone="vip">Лише за кодом</Badge> : null}
                {p.activeCampaigns > 0 ? <Badge tone="now">В активній розсилці</Badge> : null}
                {!p.isActive ? <Badge tone="neutral">Вимкнена</Badge> : null}
              </span>
              <span className="text-[13px] text-(--muted)">
                {formatDiscount(p.discountType, p.discountValue)} · {p.startsAt && p.endsAt ? dateRangeLabel(p.startsAt.slice(0, 10), p.endsAt.slice(0, 10)) : p.endsAt ? `до ${dmy(p.endsAt)}` : "без обмеження строку"}
              </span>
            </div>
            {marketingOn && p.isActive ? <LinkButton href={`/beauty/campaigns/new?promotionId=${encodeURIComponent(p.id)}`} ariaLabel={`Розіслати клієнтам: ${p.name}`}>Розіслати клієнтам</LinkButton> : null}
          </li>
        ))}
      </ul>
      {inCampaign ? (
        <p data-testid="promotion-in-campaign-note" className="m-0 rounded-xl bg-[#FFF1CC] px-3.5 py-2.5 text-[13px] text-[#5C3900]">
          Поки розсилка запущена, змінювати знижку, період, послуги, заклади й доступ акції не можна: лише вимкнути її.
        </p>
      ) : null}
    </Card>
  );
}

/** «Коди»: загальні коди акцій - строк, лічильник використань, відкликання (за підтвердженням). */
export function CodesCard() {
  const coupons = useSharedCoupons();
  const promos = usePromotions();
  const revoke = useRevokeCoupon();
  const [target, setTarget] = useState<{ id: string; code: string } | null>(null);
  if (coupons.isError && isMarketingDisabled(coupons.error)) return null;
  const list = coupons.data ?? [];
  if (coupons.isSuccess && list.length === 0) return null;
  return (
    <Card as="div" className="flex flex-col gap-3">
      <h2 className="text-lg font-semibold">Коди</h2>
      {coupons.isPending ? <div role="status" className="text-sm text-(--muted)">Завантаження…</div> : null}
      {coupons.isError ? <ErrorLine error={coupons.error} onRetry={() => coupons.refetch()} /> : null}
      {list.length > 0 ? (
        <Table headers={["Код", "Акція", "Діє до", "Використано", "Статус", ""]} minWidth={560}>
          {list.map((c) => (
            <tr key={c.id} data-testid="coupon-row" className="border-t border-(--line)">
              <td className={`${TD} font-semibold`}>{c.code}</td>
              <td className={`${TD} text-(--muted)`}>{promos.data?.find((p) => p.id === c.promotionId)?.name ?? "—"}</td>
              <td className={TD}>{dmy(c.validTo, undefined, true)}</td>
              <td className={TD}>{c.usesCount}{c.maxUses ? ` / ${c.maxUses}` : ""}</td>
              <td className={TD}><Badge tone={c.status === "active" ? "ok" : "neutral"}>{c.status === "active" ? "Діє" : "Відкликано"}</Badge></td>
              <td className={TD}>
                {c.status === "active" ? <SecondaryButton aria-label={`Відкликати код ${c.code}`} onClick={() => setTarget({ id: c.id, code: c.code })}>Відкликати</SecondaryButton> : null}
              </td>
            </tr>
          ))}
        </Table>
      ) : null}
      {target ? (
        <ModalDialog
          title={`Відкликати код ${target.code}?`}
          busy={revoke.isPending}
          onClose={() => setTarget(null)}
          actions={
            <>
              <PrimaryButton disabled={revoke.isPending} onClick={() => revoke.mutate(target.id, { onSuccess: () => setTarget(null) })}>
                {revoke.isPending ? "Відкликаємо…" : "Відкликати"}
              </PrimaryButton>
              <SecondaryButton disabled={revoke.isPending} onClick={() => setTarget(null)}>Скасувати</SecondaryButton>
            </>
          }
        >
          <p className="m-0">Після відкликання код перестане працювати для нових записів. Уже створені записи не змінюються.</p>
          {revoke.isError ? <p role="alert" className="m-0 text-[#8A1F1F]">{humanizeMarketingError(revoke.error)}</p> : null}
        </ModalDialog>
      ) : null}
    </Card>
  );
}
