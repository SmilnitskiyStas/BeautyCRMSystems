"use client";

import Link from "next/link";
import { useEffect, useId, useMemo, useRef, useState } from "react";
import { useLocations, usePriceList } from "../hooks/use-beauty-admin";
import { useAudiencePreview, useSharedCoupons } from "../hooks/use-marketing";
import {
  EXCLUDED_REASONS,
  NAME_MAX,
  PLACEHOLDERS,
  SEGMENTS,
  TEMPLATE_MAX,
  dmy,
  formatDiscount,
  placeholderAllowed,
  sampleMessage,
  fixedLines,
  usedPlaceholders,
  validateTemplate,
} from "../marketing/logic";
import { couponModeFor, retemplate, segmentOf, type WizardForm } from "../marketing/wizard";
import type { AudiencePreview, CouponMode, PromotionItem, SegmentKind } from "../types";
import { dateRangeLabel } from "../format";
import { ErrorLine } from "./marketing-notices";
import { Badge, FIELD_INPUT, FIELD_LABEL } from "./ui";

interface StepProps {
  form: WizardForm;
  setForm: (f: WizardForm) => void;
}

const useDebounced = <T,>(value: T, ms: number): T => {
  const [v, setV] = useState(value);
  useEffect(() => {
    const t = setTimeout(() => setV(value), ms);
    return () => clearTimeout(t);
  }, [value, ms]);
  return v;
};

const radioCls = (on: boolean) =>
  `flex min-h-11 cursor-pointer items-start gap-3 rounded-xl border-2 p-3 text-left ${on ? "border-(--accent) bg-(--tint)" : "border-(--line-strong) bg-white"}`;

const periodLabel = (p: PromotionItem) =>
  p.startsAt && p.endsAt ? dateRangeLabel(p.startsAt.slice(0, 10), p.endsAt.slice(0, 10)) : p.endsAt ? `до ${dmy(p.endsAt)}` : "без обмеження строку";

// ---------- 1. Акція ----------

export function StepPromo({ form, setForm, promotions }: StepProps & { promotions: PromotionItem[] }) {
  const nameId = useId();
  const choose = (type: "promo" | "news", promo?: PromotionItem) => {
    const next: WizardForm = {
      ...form,
      type,
      promotionId: promo?.id ?? "",
      couponMode: couponModeFor(type, promo, form.couponMode),
      sharedCouponId: "",
      name: form.name.trim() === "" && promo ? `Розсилка: ${promo.name}` : form.name,
    };
    setForm(retemplate(form, next));
  };
  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-col gap-1.5">
        <label htmlFor={nameId} className={FIELD_LABEL}>
          Назва розсилки (бачите лише ви)
        </label>
        <input id={nameId} type="text" maxLength={NAME_MAX} value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} className={FIELD_INPUT} />
      </div>
      <fieldset className="m-0 flex min-w-0 flex-col gap-2 border-0 p-0">
        <legend className={`${FIELD_LABEL} mb-2 p-0`}>Про що розсилка</legend>
        <label className={radioCls(form.type === "news")}>
          <input type="radio" name="promo-choice" checked={form.type === "news"} onChange={() => choose("news")} className="mt-1" />
          <span className="flex flex-col gap-0.5">
            <span className="text-[15px] font-semibold">Новина (без акції)</span>
            <span className="text-[13px] text-(--muted)">Текст без знижки. Отримають лише клієнти з повною згодою.</span>
          </span>
        </label>
        {promotions.map((p) => (
          <label key={p.id} className={`${radioCls(form.type === "promo" && form.promotionId === p.id)} ${p.isActive ? "" : "opacity-60"}`}>
            <input
              type="radio"
              name="promo-choice"
              disabled={!p.isActive}
              checked={form.type === "promo" && form.promotionId === p.id}
              onChange={() => choose("promo", p)}
              className="mt-1"
            />
            <span className="flex min-w-0 flex-col gap-0.5">
              <span className="flex flex-wrap items-center gap-2 text-[15px] font-semibold">
                {p.name}
                {p.access === "code_only" ? <Badge tone="vip">Лише за кодом</Badge> : null}
                {!p.isActive ? <Badge tone="neutral">Вимкнена</Badge> : null}
                {p.activeCampaigns > 0 ? <Badge tone="now">В активній розсилці</Badge> : null}
              </span>
              <span className="text-[13px] text-(--muted)">
                Знижка {formatDiscount(p.discountType, p.discountValue)} · {periodLabel(p)}
              </span>
            </span>
          </label>
        ))}
        {promotions.length === 0 ? (
          <p className="m-0 text-sm text-(--muted)">
            Акцій ще немає. Створіть її в розділі <Link href="/beauty/promos" className="font-semibold text-(--accent) underline">«Ціни та акції»</Link>.
          </p>
        ) : null}
      </fieldset>
    </div>
  );
}

// ---------- перегляд аудиторії (кроки 2 і 5) ----------

export function AudienceBreakdown({ data, fetching, intervalDays = 7 }: { data: AudiencePreview; fetching?: boolean; intervalDays?: number }) {
  return (
    <section aria-label="Попередній перегляд аудиторії" data-testid="audience-preview" className="flex flex-col gap-3 rounded-xl bg-(--page) p-4">
      <p aria-live="polite" aria-busy={fetching ? "true" : "false"} className="m-0 text-[15px]">
        Повідомлення отримають: <strong data-testid="audience-recipients" className="text-xl">{data.recipients}</strong> з {data.matched} клієнтів у сегменті
      </p>
      <p className="m-0 text-[13px] text-(--muted)">Показуємо лише кількості: імен і контактів клієнтів тут немає.</p>
      <ul className="m-0 flex list-none flex-col gap-1.5 p-0">
        {EXCLUDED_REASONS.map((r) => (
          <li key={r.key} data-testid={`excluded-${r.key}`} className="flex flex-col gap-0.5 border-t border-(--line) pt-1.5 text-sm">
            <span className="flex justify-between gap-3">
              <span className="font-semibold">Не отримають: {r.label.toLowerCase()}</span>
              <strong>{data.excluded[r.key]}</strong>
            </span>
            <span className="text-[13px] text-(--muted)">{r.key === "frequency" ? `Не більше одного маркетингового повідомлення на клієнта за ${intervalDays} днів.` : r.hint}</span>
          </li>
        ))}
      </ul>
    </section>
  );
}

// ---------- 2. Аудиторія ----------

export function StepAudience({ form, setForm, invalid }: StepProps & { invalid: string | null }) {
  const locations = useLocations();
  const services = usePriceList();
  const ids = { days: useId(), service: useId(), visits: useId(), period: useId(), loc: useId() };
  const query = useMemo(
    () => ({ type: form.type, ...(form.promotionId && form.type === "promo" ? { promotionId: form.promotionId } : {}), segment: segmentOf(form), ...(form.locationId ? { locationId: form.locationId } : {}) }),
    [form],
  );
  const debounced = useDebounced(query, 300);
  const preview = useAudiencePreview(debounced, invalid === null);
  const set = (patch: Partial<WizardForm>) => setForm({ ...form, ...patch });
  return (
    <div className="flex flex-col gap-4">
      <fieldset className="m-0 flex min-w-0 flex-col gap-2 border-0 p-0">
        <legend className={`${FIELD_LABEL} mb-2 p-0`}>Кому надіслати</legend>
        {SEGMENTS.map((s) => (
          <label key={s.kind} className={radioCls(form.kind === s.kind)}>
            <input type="radio" name="segment" checked={form.kind === s.kind} onChange={() => set({ kind: s.kind as SegmentKind })} className="mt-1" />
            <span className="flex flex-col gap-0.5">
              <span className="text-[15px] font-semibold">{s.label}</span>
              <span className="text-[13px] text-(--muted)">{s.hint}</span>
            </span>
          </label>
        ))}
      </fieldset>

      {form.kind === "lapsed" ? (
        <div className="flex flex-wrap gap-3">
          <div className="flex min-w-[160px] flex-1 flex-col gap-1.5">
            <label htmlFor={ids.days} className={FIELD_LABEL}>Не були, днів (1–730)</label>
            <input id={ids.days} type="number" inputMode="numeric" min={1} max={730} value={form.days} onChange={(e) => set({ days: e.target.value })} className={FIELD_INPUT} />
          </div>
          <div className="flex min-w-[200px] flex-1 flex-col gap-1.5">
            <label htmlFor={ids.service} className={FIELD_LABEL}>Послуга (необов&apos;язково)</label>
            <select id={ids.service} value={form.serviceId} onChange={(e) => set({ serviceId: e.target.value })} className={FIELD_INPUT}>
              <option value="">Будь-яка послуга</option>
              {services.data?.map((s) => (
                <option key={s.serviceId} value={s.serviceId}>{s.name}</option>
              ))}
            </select>
          </div>
        </div>
      ) : null}
      {form.kind === "frequent" ? (
        <div className="flex flex-wrap gap-3">
          <div className="flex min-w-[160px] flex-1 flex-col gap-1.5">
            <label htmlFor={ids.visits} className={FIELD_LABEL}>Візитів не менше (1–100)</label>
            <input id={ids.visits} type="number" inputMode="numeric" min={1} max={100} value={form.minVisits} onChange={(e) => set({ minVisits: e.target.value })} className={FIELD_INPUT} />
          </div>
          <div className="flex min-w-[160px] flex-1 flex-col gap-1.5">
            <label htmlFor={ids.period} className={FIELD_LABEL}>За останні, днів (1–730)</label>
            <input id={ids.period} type="number" inputMode="numeric" min={1} max={730} value={form.periodDays} onChange={(e) => set({ periodDays: e.target.value })} className={FIELD_INPUT} />
          </div>
        </div>
      ) : null}

      <div className="flex max-w-[360px] flex-col gap-1.5">
        <label htmlFor={ids.loc} className={FIELD_LABEL}>Заклад</label>
        <select id={ids.loc} value={form.locationId} onChange={(e) => set({ locationId: e.target.value })} className={FIELD_INPUT}>
          <option value="">Усі заклади</option>
          {locations.data?.map((l) => (
            <option key={l.id} value={l.id}>{l.name}</option>
          ))}
        </select>
      </div>

      {invalid ? (
        <p role="alert" className="m-0 text-sm text-[#8A1F1F]">{invalid}</p>
      ) : preview.data ? (
        <AudienceBreakdown data={preview.data} fetching={preview.isFetching} />
      ) : preview.isError ? (
        <ErrorLine error={preview.error} fallback="Не вдалося порахувати аудиторію." onRetry={() => preview.refetch()} />
      ) : (
        <div role="status" aria-live="polite" className="text-sm text-(--muted)">Рахуємо аудиторію…</div>
      )}
    </div>
  );
}

// ---------- 3. Текст ----------

export interface SampleInfo {
  promo?: PromotionItem;
  serviceNames: string[];
  tz?: string;
  sharedCode?: string;
  couponFrom?: string;
  couponTo?: string;
}

export function sampleFor(form: WizardForm, info: SampleInfo): string {
  const p = info.promo;
  return sampleMessage(
    { type: form.type, couponMode: form.couponMode, template: form.template },
    {
      ...(p ? { discount: formatDiscount(p.discountType, p.discountValue), services: info.serviceNames } : {}),
      ...(form.type === "promo" ? { validFrom: p?.startsAt ? dmy(p.startsAt, info.tz) : undefined, validTo: p?.endsAt ? dmy(p.endsAt, info.tz, true) : undefined } : {}),
      ...(info.sharedCode ? { code: info.sharedCode } : {}),
    },
  );
}

export function StepText({ form, setForm, info }: StepProps & { info: SampleInfo }) {
  const area = useRef<HTMLTextAreaElement>(null);
  const id = useId();
  const error = validateTemplate(form.template, { type: form.type, couponMode: form.couponMode });
  const couponRequired = form.couponMode !== "none";
  const ctx = {
    discount: info.promo ? formatDiscount(info.promo.discountType, info.promo.discountValue) : undefined,
    services: info.serviceNames,
    validFrom: info.promo?.startsAt ? dmy(info.promo.startsAt, info.tz) : undefined,
    validTo: info.promo?.endsAt ? dmy(info.promo.endsAt, info.tz, true) : undefined,
    code: info.sharedCode,
  };
  const length = [...form.template].length;

  function insert(token: string) {
    const el = area.current;
    const text = `{${token}}`;
    const start = el?.selectionStart ?? form.template.length;
    const end = el?.selectionEnd ?? start;
    const next = `${form.template.slice(0, start)}${text}${form.template.slice(end)}`;
    setForm({ ...form, template: next });
    requestAnimationFrame(() => {
      el?.focus();
      el?.setSelectionRange(start + text.length, start + text.length);
    });
  }

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-col gap-2">
        <label htmlFor={id} className={FIELD_LABEL}>Текст повідомлення</label>
        <div role="group" aria-label="Вставити підстановку" className="flex flex-wrap gap-2">
          {PLACEHOLDERS.map((p) => {
            const ok = placeholderAllowed(p, form.type, couponRequired);
            return (
              <button
                key={p.token}
                type="button"
                disabled={!ok}
                aria-label={`Вставити ${p.label} {${p.token}}`}
                title={ok ? undefined : p.needsCoupon ? "Потрібен купон" : "Лише для розсилки про акцію"}
                onClick={() => insert(p.token)}
                className="min-h-11 cursor-pointer rounded-xl border border-(--line-strong) bg-white px-3 text-sm font-semibold text-(--ink) disabled:cursor-not-allowed disabled:opacity-50"
              >
                {`{${p.token}}`} · {p.label}
              </button>
            );
          })}
        </div>
        <textarea
          id={id}
          ref={area}
          rows={5}
          value={form.template}
          onChange={(e) => setForm({ ...form, template: e.target.value })}
          aria-invalid={error ? "true" : "false"}
          aria-describedby={`${id}-count ${id}-err`}
          className="w-full resize-y rounded-xl border-2 border-(--line-strong) px-3.5 py-3 text-[15px] leading-snug"
        />
        <div className="flex flex-wrap justify-between gap-2 text-[13px]">
          <span id={`${id}-count`} aria-live="polite" className={length > TEMPLATE_MAX ? "font-semibold text-[#8A1F1F]" : "text-(--muted)"}>
            {length} / {TEMPLATE_MAX} символів
          </span>
          <span className="text-(--muted)">Використано підстановок: {usedPlaceholders(form.template).length}</span>
        </div>
        <p id={`${id}-err`} aria-live="polite" className="m-0 min-h-5 text-sm text-[#8A1F1F]">{error ?? ""}</p>
      </div>

      <div className="flex flex-col gap-1 rounded-xl bg-(--page) p-3.5 text-[13px] text-(--muted)" aria-label="Рядки, які додаються автоматично" role="group">
        <strong className="text-(--ink)">Додається автоматично (змінити не можна)</strong>
        {fixedLines({ type: form.type, couponMode: form.couponMode, template: form.template }, ctx).map((l) => (
          <span key={l} data-testid="fixed-line">{l}</span>
        ))}
      </div>

      <section aria-label="Приклад повідомлення" className="flex flex-col gap-2">
        <div className="text-[13px] font-semibold text-(--muted)">Приклад для клієнтки Олени</div>
        <div data-testid="sample-message" className="max-w-[480px] whitespace-pre-wrap break-words rounded-2xl rounded-bl-sm bg-(--tint) px-4 py-3 text-sm leading-snug">
          {sampleFor(form, info)}
        </div>
      </section>
    </div>
  );
}

// ---------- 4. Купон ----------

export function StepCoupon({ form, setForm, promo }: StepProps & { promo?: PromotionItem }) {
  const ids = { from: useId(), to: useId() };
  const needsCode = form.type === "promo" && promo?.access === "code_only";
  const coupons = useSharedCoupons(promo?.id, needsCode && form.couponMode === "shared");
  const set = (patch: Partial<WizardForm>) => setForm({ ...form, ...patch });
  const setMode = (mode: CouponMode) => setForm(retemplate(form, { ...form, couponMode: mode }));

  if (!needsCode) {
    return (
      <p data-testid="coupon-not-needed" className="m-0 rounded-xl bg-(--page) p-4 text-sm">
        {form.type === "news"
          ? "Для новини купон не потрібен."
          : "Ця акція публічна: знижка діє для всіх клієнтів автоматично, тому купон не потрібен. Щоб роздавати коди, створіть акцію «Лише за кодом»."}
      </p>
    );
  }
  const active = (coupons.data ?? []).filter((c) => c.status === "active");
  return (
    <div className="flex flex-col gap-4">
      <p className="m-0 text-sm text-(--muted)">Акція діє лише за кодом. Оберіть, як клієнти її отримають.</p>
      <fieldset className="m-0 flex min-w-0 flex-col gap-2 border-0 p-0">
        <legend className={`${FIELD_LABEL} mb-2 p-0`}>Тип купона</legend>
        <label className={radioCls(form.couponMode === "personal")}>
          <input type="radio" name="coupon-mode" checked={form.couponMode === "personal"} onChange={() => setMode("personal")} className="mt-1" />
          <span className="flex flex-col gap-0.5">
            <span className="text-[15px] font-semibold">Персональний одноразовий код</span>
            <span className="text-[13px] text-(--muted)">Кожен клієнт отримує свій код. Його бачить лише клієнт, у вашій системі лишаються тільки останні 3 символи.</span>
          </span>
        </label>
        <label className={radioCls(form.couponMode === "shared")}>
          <input type="radio" name="coupon-mode" checked={form.couponMode === "shared"} onChange={() => setMode("shared")} className="mt-1" />
          <span className="flex flex-col gap-0.5">
            <span className="text-[15px] font-semibold">Загальний код</span>
            <span className="text-[13px] text-(--muted)">Один код для всіх із лімітом використань і строком.</span>
          </span>
        </label>
      </fieldset>

      {form.couponMode === "personal" ? (
        <div className="flex flex-wrap gap-3">
          <div className="flex min-w-[160px] flex-1 flex-col gap-1.5">
            <label htmlFor={ids.from} className={FIELD_LABEL}>Коди діють з</label>
            <input id={ids.from} type="date" value={form.couponFrom} onChange={(e) => set({ couponFrom: e.target.value })} className={FIELD_INPUT} />
          </div>
          <div className="flex min-w-[160px] flex-1 flex-col gap-1.5">
            <label htmlFor={ids.to} className={FIELD_LABEL}>Коди діють до (включно)</label>
            <input id={ids.to} type="date" value={form.couponTo} onChange={(e) => set({ couponTo: e.target.value })} className={FIELD_INPUT} />
          </div>
        </div>
      ) : (
        <fieldset className="m-0 flex min-w-0 flex-col gap-2 border-0 p-0">
          <legend className={`${FIELD_LABEL} mb-2 p-0`}>Загальний код акції</legend>
          {coupons.isPending ? <div role="status" className="text-sm text-(--muted)">Завантаження кодів…</div> : null}
          {coupons.isError ? <ErrorLine error={coupons.error} onRetry={() => coupons.refetch()} /> : null}
          {active.map((c) => (
            <label key={c.id} className={radioCls(form.sharedCouponId === c.id)}>
              <input type="radio" name="shared-coupon" checked={form.sharedCouponId === c.id} onChange={() => set({ sharedCouponId: c.id })} className="mt-1" />
              <span className="flex flex-col gap-0.5">
                <span className="text-[15px] font-semibold">{c.code}</span>
                <span className="text-[13px] text-(--muted)">
                  до {dmy(c.validTo, undefined, true)} · використано {c.usesCount}
                  {c.maxUses ? ` з ${c.maxUses}` : ""}
                </span>
              </span>
            </label>
          ))}
          {coupons.isSuccess && active.length === 0 ? (
            <p className="m-0 text-sm text-(--muted)">
              Для цієї акції немає активних кодів. Створіть код у розділі <Link href="/beauty/promos" className="font-semibold text-(--accent) underline">«Ціни та акції»</Link>.
            </p>
          ) : null}
        </fieldset>
      )}
    </div>
  );
}
