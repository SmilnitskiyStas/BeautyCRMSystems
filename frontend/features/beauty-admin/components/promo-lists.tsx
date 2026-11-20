"use client";

import { useEffect, useRef, useState } from "react";
import { useLocations, usePriceList, usePromoPreview } from "../hooks/use-beauty-admin";
import { useCreateCoupon, usePromotions, useRevokeCoupon, useSharedCoupons } from "../hooks/use-marketing";
import { addDaysIso, durationLabel, money, todayIso } from "../format";
import { couponFormErrors, dmy, formatDiscount, humanizeMarketingError, ymdToDmy } from "../marketing/logic";
import { dateToIso } from "../marketing/wizard";
import type { CouponInput, PromotionItem, SharedCoupon } from "../types";
import { ErrorLine } from "./marketing-notices";
import { ModalDialog } from "./modal-dialog";
import { PromoForm, type CreatedPromo } from "./promo-form";
import { Badge, Card, EmptyState, FIELD_INPUT, FIELD_LABEL, LinkButton, PrimaryButton, QueryState, SecondaryButton, Table, TD } from "./ui";
import type { Tone } from "./ui";

// ---------- статуси ----------

export type PromoStatus = "active" | "ended" | "scheduled" | "disabled";

/** Статус акції за `isActive` і датами; завершена має пріоритет над вимкненою. */
export function promoStatus(p: Pick<PromotionItem, "isActive" | "startsAt" | "endsAt">, nowMs: number): PromoStatus {
  const ends = p.endsAt ? Date.parse(p.endsAt) : NaN;
  const starts = p.startsAt ? Date.parse(p.startsAt) : NaN;
  if (!Number.isNaN(ends) && ends < nowMs) return "ended";
  if (!p.isActive) return "disabled";
  if (!Number.isNaN(starts) && starts > nowMs) return "scheduled";
  return "active";
}

const PROMO_STATUS_VIEW: Record<PromoStatus, { label: string; tone: Tone }> = {
  active: { label: "Активна", tone: "ok" },
  scheduled: { label: "Запланована", tone: "now" },
  ended: { label: "Завершена", tone: "neutral" },
  disabled: { label: "Вимкнена", tone: "wait" },
};

function periodLabel(startsAt: string | null, endsAt: string | null): string {
  if (startsAt && endsAt) return `${dmy(startsAt)} – ${dmy(endsAt)}`;
  if (endsAt) return `до ${dmy(endsAt)}`;
  if (startsAt) return `з ${dmy(startsAt)}`;
  return "без обмеження строку";
}

// ---------- «Прайс-лист» ----------

export function PricesTab() {
  const locations = useLocations();
  const activeLocationIds = (locations.data ?? []).filter((l) => l.isActive !== false).map((l) => l.id);
  const preview = usePromoPreview({ name: "", from: "", to: "", percent: 20, locationIds: activeLocationIds, serviceIds: [] });
  return (
    <Card>
      <h2 className="mb-3.5 text-lg font-semibold">Прайс-лист</h2>
      <QueryState isPending={preview.isPending} isError={preview.isError} onRetry={() => preview.refetch()}>
        <Table headers={["Послуга", "Тривалість", "Ціна мережі", "З акцією", "Заклади"]} minWidth={600}>
          {preview.data?.rows.map((r) => (
            <tr key={r.serviceId} className="border-t border-(--line)">
              <td className={`${TD} font-semibold`}>{r.name}</td>
              <td className={`${TD} text-(--muted)`}>{durationLabel(r.durationMinutes)}</td>
              <td className={TD}>{money(r.price)}</td>
              <td className={`${TD} font-semibold ${r.promoPrice !== null ? "text-(--promo)" : "text-(--muted)"}`}>
                {r.promoPrice !== null ? money(r.promoPrice) : "—"}
              </td>
              <td className={`${TD} text-(--muted)`}>{r.where}</td>
            </tr>
          ))}
        </Table>
      </QueryState>
    </Card>
  );
}

// ---------- «Акції» ----------

export function OffersTab({ marketingOn, canCreate, canCodes }: { marketingOn: boolean; canCreate: boolean; canCodes: boolean }) {
  const promos = usePromotions();
  const locations = useLocations();
  const priceList = usePriceList();
  const createCoupon = useCreateCoupon();
  const [formOpen, setFormOpen] = useState(false);
  const [created, setCreated] = useState<CreatedPromo | null>(null);
  const [now] = useState(() => Date.now());
  const addBtn = useRef<HTMLButtonElement>(null);
  const wasOpen = useRef(false);

  // Після закриття форми фокус повертається на кнопку «Додати акцію».
  useEffect(() => {
    if (wasOpen.current && !formOpen) addBtn.current?.focus();
    wasOpen.current = formOpen;
  }, [formOpen]);

  const locName = (id: string) => locations.data?.find((l) => l.id === id)?.name;
  const where = (p: PromotionItem) => {
    if (p.locationIds.length === 0) return "Усі заклади";
    return p.locationIds.map((id) => locName(id) ?? "—").join(", ");
  };
  const services = (p: PromotionItem) => {
    if (p.serviceIds.length === 0) return "Усі послуги";
    const names = p.serviceIds.map((id) => priceList.data?.find((s) => s.serviceId === id)?.name).filter((n): n is string => !!n);
    return names.length > 0 && names.length <= 3 ? names.join(", ") : `${p.serviceIds.length} послуг`;
  };

  async function retryCoupon(c: CreatedPromo) {
    if (!c.couponInput) return;
    try {
      await createCoupon.mutateAsync(c.couponInput);
      setCreated({ ...c, couponError: undefined, couponInput: undefined });
    } catch (e) {
      setCreated({ ...c, couponError: humanizeMarketingError(e, "Не вдалося створити код.") });
    }
  }

  const list = promos.data ?? [];
  return (
    <Card as="div" className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h2 className="text-lg font-semibold">Акції</h2>
        {canCreate && !formOpen ? (
          <PrimaryButton ref={addBtn} onClick={() => { setCreated(null); setFormOpen(true); }}>
            Додати акцію
          </PrimaryButton>
        ) : null}
      </div>

      {formOpen ? (
        <PromoForm
          marketingOn={marketingOn}
          onClose={() => setFormOpen(false)}
          onCreated={(c) => {
            setCreated(c);
            setFormOpen(false);
          }}
        />
      ) : null}

      {created ? (
        <div role="status" data-testid="promo-created" className="flex flex-col gap-2 rounded-xl bg-[#DDF3E6] px-3 py-2 text-sm text-[#145A32]">
          <span>Акцію «{created.name}» запущено: {ymdToDmy(created.from)} - {ymdToDmy(created.to)}.</span>
          {marketingOn ? (
            <span>
              <LinkButton href={`/beauty/campaigns/new?promotionId=${encodeURIComponent(created.id)}`} ariaLabel={`Розіслати клієнтам: ${created.name}`}>
                Розіслати клієнтам
              </LinkButton>
            </span>
          ) : null}
        </div>
      ) : null}
      {created?.couponError ? (
        <div role="alert" className="flex flex-col gap-2 rounded-xl bg-[#FBE4E4] px-3 py-2 text-sm text-[#8A1F1F]">
          <span>Акцію створено, але код не збережено: {created.couponError}</span>
          {canCodes && created.couponInput ? (
            <span>
              <SecondaryButton disabled={createCoupon.isPending} onClick={() => void retryCoupon(created)}>
                Створити код ще раз
              </SecondaryButton>
            </span>
          ) : null}
        </div>
      ) : null}

      <QueryState isPending={promos.isPending} isError={promos.isError} onRetry={() => promos.refetch()}>
        {list.length === 0 ? (
          <EmptyState>Акцій ще немає. Натисніть «Додати акцію», щоб запустити першу знижку на послуги.</EmptyState>
        ) : (
          <Table headers={["Назва", "Знижка", "Період", "Де діє", "Послуги", "Доступ", "Статус"]} minWidth={860} caption="Акції">
            {list.map((p) => {
              const st = PROMO_STATUS_VIEW[promoStatus(p, now)];
              return (
                <tr key={p.id} data-testid="promotion-item" className="border-t border-(--line) align-top">
                  <td className={`${TD} font-semibold`}>
                    {p.name}
                    {p.activeCampaigns > 0 ? (
                      <span className="mt-1 block">
                        <Badge tone="now">В активній розсилці: {p.activeCampaigns}</Badge>
                      </span>
                    ) : null}
                  </td>
                  <td className={`${TD} whitespace-nowrap font-semibold text-(--promo)`}>−{formatDiscount(p.discountType, p.discountValue)}</td>
                  <td className={`${TD} whitespace-nowrap`}>{periodLabel(p.startsAt, p.endsAt)}</td>
                  <td className={`${TD} text-(--muted)`}>{where(p)}</td>
                  <td className={`${TD} text-(--muted)`}>{services(p)}</td>
                  <td className={TD}>{p.access === "code_only" ? <Badge tone="vip">Лише за кодом</Badge> : "Для всіх"}</td>
                  <td className={TD}><Badge tone={st.tone}>{st.label}</Badge></td>
                </tr>
              );
            })}
          </Table>
        )}
      </QueryState>
    </Card>
  );
}

// ---------- «Коди» ----------

function couponStatus(c: SharedCoupon, nowMs: number): { label: string; tone: Tone } {
  if (c.status === "revoked") return { label: "Відкликано", tone: "neutral" };
  if (Date.parse(c.validTo) < nowMs) return { label: "Строк минув", tone: "neutral" };
  if (Date.parse(c.validFrom) > nowMs) return { label: "Ще не діє", tone: "now" };
  return { label: "Діє", tone: "ok" };
}

export function CodesTab({ canManage }: { canManage: boolean }) {
  const coupons = useSharedCoupons();
  const promos = usePromotions();
  const revoke = useRevokeCoupon();
  const [target, setTarget] = useState<{ id: string; code: string } | null>(null);
  const [adding, setAdding] = useState(false);
  const [now] = useState(() => Date.now());
  const addBtn = useRef<HTMLButtonElement>(null);
  const list = coupons.data ?? [];
  const codeOnlyPromos = (promos.data ?? []).filter((p) => p.access === "code_only");

  return (
    <Card as="div" className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h2 className="text-lg font-semibold">Коди</h2>
        {canManage ? <PrimaryButton ref={addBtn} onClick={() => setAdding(true)}>Додати код</PrimaryButton> : null}
      </div>
      <QueryState isPending={coupons.isPending} isError={coupons.isError} onRetry={() => coupons.refetch()}>
        {list.length === 0 ? (
          <EmptyState>Загальних кодів ще немає. Код створюється для акції з доступом «Лише за кодом».</EmptyState>
        ) : (
          <Table headers={["Код", "Акція", "Період", "Використано", "Статус", ""]} minWidth={680} caption="Коди акцій">
            {list.map((c) => {
              const st = couponStatus(c, now);
              return (
                <tr key={c.id} data-testid="coupon-row" className="border-t border-(--line)">
                  <td className={`${TD} font-semibold`}>{c.code}</td>
                  <td className={`${TD} text-(--muted)`}>{promos.data?.find((p) => p.id === c.promotionId)?.name ?? "—"}</td>
                  <td className={`${TD} whitespace-nowrap`}>{dmy(c.validFrom)} – {dmy(c.validTo, undefined, true)}</td>
                  <td className={TD}>{c.usesCount}{c.maxUses ? ` / ${c.maxUses}` : ""}</td>
                  <td className={TD}><Badge tone={st.tone}>{st.label}</Badge></td>
                  <td className={TD}>
                    {canManage && c.status === "active" ? (
                      <SecondaryButton aria-label={`Відкликати код ${c.code}`} onClick={() => setTarget({ id: c.id, code: c.code })}>Відкликати</SecondaryButton>
                    ) : null}
                  </td>
                </tr>
              );
            })}
          </Table>
        )}
      </QueryState>
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
      {adding ? <CouponDialog promotions={codeOnlyPromos} onClose={() => setAdding(false)} /> : null}
    </Card>
  );
}

function CouponDialog({ promotions, onClose }: { promotions: PromotionItem[]; onClose: () => void }) {
  const create = useCreateCoupon();
  const [today] = useState(() => todayIso());
  const [promotionId, setPromotionId] = useState(promotions[0]?.id ?? "");
  const [code, setCode] = useState("");
  const [maxUses, setMaxUses] = useState("");
  const [from, setFrom] = useState(today);
  const [to, setTo] = useState(() => addDaysIso(today, 30));
  const errors = couponFormErrors({ code, maxUses, from, to });
  const valid = !!promotionId && Object.keys(errors).length === 0;

  function submit() {
    if (!valid || create.isPending) return;
    const input: CouponInput = {
      promotionId,
      code: code.trim(),
      validFrom: dateToIso(from, false),
      validTo: dateToIso(to, true),
      ...(maxUses.trim() ? { maxUses: Number(maxUses) } : {}),
    };
    create.mutate(input, { onSuccess: onClose });
  }

  return (
    <ModalDialog
      title="Додати код"
      busy={create.isPending}
      onClose={onClose}
      actions={
        <>
          <PrimaryButton disabled={!valid || create.isPending} onClick={submit}>{create.isPending ? "Зберігаємо…" : "Створити код"}</PrimaryButton>
          <SecondaryButton disabled={create.isPending} onClick={onClose}>Скасувати</SecondaryButton>
        </>
      }
    >
      {promotions.length === 0 ? (
        <p role="note" className="m-0 text-(--muted)">Немає акцій з доступом «Лише за кодом». Створіть таку акцію на вкладці «Акції».</p>
      ) : (
        <form
          className="flex flex-col gap-3"
          onSubmit={(e) => {
            e.preventDefault();
            submit();
          }}
        >
          <div className="flex flex-col gap-1.5">
            <label htmlFor="cp-promo" className={FIELD_LABEL}>Акція</label>
            <select id="cp-promo" value={promotionId} onChange={(e) => setPromotionId(e.target.value)} className={FIELD_INPUT}>
              {promotions.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
            </select>
          </div>
          <div className="flex flex-col gap-1.5">
            <label htmlFor="cp-code" className={FIELD_LABEL}>Код (4–20 символів, латиниця й цифри)</label>
            <input id="cp-code" type="text" autoComplete="off" maxLength={20} value={code} onChange={(e) => setCode(e.target.value.toUpperCase())} aria-invalid={errors.code && code ? "true" : "false"} className={FIELD_INPUT} />
            {errors.code && code ? <span className="text-[13px] text-[#8A1F1F]">{errors.code}</span> : null}
          </div>
          <div className="flex flex-col gap-1.5">
            <label htmlFor="cp-max" className={FIELD_LABEL}>Ліміт використань (необов&apos;язково)</label>
            <input id="cp-max" type="number" inputMode="numeric" min={1} max={100000} value={maxUses} onChange={(e) => setMaxUses(e.target.value)} aria-invalid={errors.maxUses ? "true" : "false"} className={FIELD_INPUT} />
            {errors.maxUses ? <span className="text-[13px] text-[#8A1F1F]">{errors.maxUses}</span> : null}
          </div>
          <div className="flex flex-wrap gap-3">
            <div className="flex min-w-[140px] flex-1 flex-col gap-1.5">
              <label htmlFor="cp-from" className={FIELD_LABEL}>Код діє з</label>
              <input id="cp-from" type="date" value={from} onChange={(e) => setFrom(e.target.value)} className={FIELD_INPUT} />
            </div>
            <div className="flex min-w-[140px] flex-1 flex-col gap-1.5">
              <label htmlFor="cp-to" className={FIELD_LABEL}>Код діє до (включно)</label>
              <input id="cp-to" type="date" value={to} onChange={(e) => setTo(e.target.value)} className={FIELD_INPUT} />
            </div>
          </div>
          {errors.period ? <span role="alert" className="text-[13px] text-[#8A1F1F]">{errors.period}</span> : null}
          {create.isError ? <ErrorLine error={create.error} fallback="Не вдалося створити код." /> : null}
        </form>
      )}
    </ModalDialog>
  );
}
