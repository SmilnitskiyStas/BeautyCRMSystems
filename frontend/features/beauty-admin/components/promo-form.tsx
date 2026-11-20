"use client";

import { useEffect, useRef, useState } from "react";
import { useCreatePromotion, useLocations, usePriceList, usePromoPreview } from "../hooks/use-beauty-admin";
import { useCreateCoupon } from "../hooks/use-marketing";
import { addDaysIso, money, todayIso } from "../format";
import { couponFormErrors, humanizeMarketingError, humanizePromoError, promoPeriodError, ymdToDmy } from "../marketing/logic";
import { dateToIso } from "../marketing/wizard";
import type { CouponInput, PromoDraft } from "../types";
import { AccessFields, type AccessValue } from "./promo-access";
import { Card, Chip, FIELD_INPUT, FIELD_LABEL, SecondaryButton } from "./ui";

const PERCENTS = [10, 15, 20, 30];

function toggle(list: string[], id: string): string[] {
  return list.includes(id) ? list.filter((x) => x !== id) : [...list, id];
}

/** Результат створення акції: показується банером над списком після закриття форми. */
export interface CreatedPromo {
  id: string;
  name: string;
  from: string;
  to: string;
  couponError?: string;
  /** Для повтору, якщо акцію створено, а код - ні. */
  couponInput?: CouponInput;
}

/** Форма «Нова акція» (відкривається кнопкою «Додати акцію»): назва, знижка, заклади, послуги, доступ/код, період. */
export function PromoForm({ marketingOn, onClose, onCreated }: { marketingOn: boolean; onClose: () => void; onCreated: (c: CreatedPromo) => void }) {
  const [today] = useState(() => todayIso());
  const [name, setName] = useState("");
  const [percent, setPercent] = useState(20);
  // null = користувач ще не чіпав вибір: діють усі активні заклади з API.
  const [pickedLocations, setPickedLocations] = useState<string[] | null>(null);
  const [serviceIds, setServiceIds] = useState<string[]>([]);
  const [from, setFrom] = useState(today);
  const [to, setTo] = useState(() => addDaysIso(today, 7));
  const [access, setAccess] = useState<AccessValue>({ access: "public", code: "", maxUses: "", from: "", to: "" });
  const nameRef = useRef<HTMLInputElement>(null);
  const createCoupon = useCreateCoupon();

  useEffect(() => {
    nameRef.current?.focus();
  }, []);

  const locations = useLocations();
  const priceList = usePriceList();
  // Для перерахунку цін назва й дати не потрібні — не перезапитуємо на кожну літеру.
  const activeLocationIds = (locations.data ?? []).filter((l) => l.isActive !== false).map((l) => l.id);
  const locationIds = pickedLocations ?? activeLocationIds;
  const preview = usePromoPreview({ name: "", from: "", to: "", percent, locationIds, serviceIds });
  const create = useCreatePromotion();

  const codeOnly = marketingOn && access.access === "code_only";
  // Строк коду за замовчуванням збігається з періодом акції.
  const couponPeriod = { from: access.from || from, to: access.to || to };
  const couponErrors = codeOnly ? couponFormErrors({ code: access.code, maxUses: access.maxUses, ...couponPeriod }) : {};
  const periodError = promoPeriodError(from, to, today);
  const canLaunch = name.trim().length > 0 && locationIds.length > 0 && serviceIds.length > 0 && !periodError && Object.keys(couponErrors).length === 0;
  const hint = !name.trim() ? "Вкажіть назву акції." : locationIds.length === 0 ? "Оберіть хоча б один заклад." : serviceIds.length === 0 ? "Оберіть хоча б одну послугу." : null;
  const draft: PromoDraft = { name: name.trim(), percent, locationIds, serviceIds, from, to, access: codeOnly ? "code_only" : "public" };

  async function launch() {
    let id: string;
    try {
      id = (await create.mutateAsync(draft)).id;
    } catch {
      return; // стан помилки показує create.isError
    }
    const base = { id, name: draft.name, from, to };
    if (!codeOnly) return onCreated(base);
    const input: CouponInput = {
      promotionId: id,
      code: access.code.trim(),
      validFrom: dateToIso(couponPeriod.from, false),
      validTo: dateToIso(couponPeriod.to, true),
      ...(access.maxUses.trim() ? { maxUses: Number(access.maxUses) } : {}),
    };
    try {
      await createCoupon.mutateAsync(input);
      onCreated(base);
    } catch (e) {
      onCreated({ ...base, couponError: humanizeMarketingError(e, "Не вдалося створити код."), couponInput: input });
    }
  }

  const busy = create.isPending || createCoupon.isPending;

  return (
    <div className="grid items-start gap-5 rounded-2xl border border-(--line-strong) bg-white p-5 lg:grid-cols-[minmax(0,1fr)_320px]">
      <form
        aria-label="Нова акція"
        className="flex min-w-0 flex-col gap-[18px]"
        onSubmit={(e) => {
          e.preventDefault();
          if (canLaunch && !busy) void launch();
        }}
      >
        <h2 className="text-lg font-semibold">Нова акція</h2>
        <div className="flex flex-col gap-1.5">
          <label htmlFor="promo-name" className={FIELD_LABEL}>Назва</label>
          <input ref={nameRef} id="promo-name" type="text" maxLength={200} placeholder="Наприклад, «Осінній манікюр»" value={name} onChange={(e) => setName(e.target.value)} className={FIELD_INPUT} />
        </div>

        <fieldset className="m-0 flex min-w-0 flex-col gap-2 border-0 p-0">
          <legend className={`${FIELD_LABEL} mb-2 p-0`}>Знижка</legend>
          <div className="flex flex-wrap gap-2">
            {PERCENTS.map((p) => (
              <Chip key={p} variant="square" pressed={percent === p} onClick={() => setPercent(p)}>
                −{p}%
              </Chip>
            ))}
          </div>
        </fieldset>

        <fieldset className="m-0 flex min-w-0 flex-col gap-2 border-0 p-0">
          <legend className={`${FIELD_LABEL} mb-2 p-0`}>Де діє</legend>
          <div className="flex flex-wrap gap-2">
            {locations.data?.map((l) => (
              <Chip key={l.id} pressed={locationIds.includes(l.id)} onClick={() => setPickedLocations(toggle(locationIds, l.id))}>
                {l.name}
              </Chip>
            ))}
          </div>
        </fieldset>

        <fieldset className="m-0 flex min-w-0 flex-col gap-2 border-0 p-0">
          <legend className={`${FIELD_LABEL} mb-2 p-0`}>Для яких послуг</legend>
          <div className="flex flex-wrap gap-2">
            {priceList.data?.map((s) => (
              <Chip key={s.serviceId} variant="soft" pressed={serviceIds.includes(s.serviceId)} onClick={() => setServiceIds(toggle(serviceIds, s.serviceId))}>
                {s.name}
              </Chip>
            ))}
          </div>
        </fieldset>

        <AccessFields value={access} onChange={setAccess} errors={couponErrors} marketingOn={marketingOn} />

        <div className="flex flex-wrap gap-3">
          <div className="flex min-w-[140px] flex-1 flex-col gap-1.5">
            <label htmlFor="d-from" className={FIELD_LABEL}>Діє з</label>
            <input id="d-from" type="date" min={today} value={from} onChange={(e) => setFrom(e.target.value)} aria-invalid={periodError ? true : undefined} className={FIELD_INPUT} />
          </div>
          <div className="flex min-w-[140px] flex-1 flex-col gap-1.5">
            <label htmlFor="d-to" className={FIELD_LABEL}>Діє по</label>
            <input id="d-to" type="date" min={from || today} value={to} onChange={(e) => setTo(e.target.value)} aria-invalid={periodError ? true : undefined} className={FIELD_INPUT} />
          </div>
        </div>
        {periodError ? <p role="alert" data-testid="promo-period-error" className="text-[13px] text-[#8A1F1F]">{periodError}</p> : null}

        <div className="flex flex-wrap gap-3">
          <button
            type="submit"
            disabled={!canLaunch || busy}
            className="min-h-[52px] min-w-[200px] flex-1 cursor-pointer rounded-[26px] border-0 bg-(--accent) text-base font-semibold text-white disabled:cursor-not-allowed disabled:opacity-60"
          >
            {busy ? "Запускаємо…" : "Запустити акцію"}
          </button>
          <SecondaryButton disabled={busy} onClick={onClose} className="min-h-[52px]">
            Скасувати
          </SecondaryButton>
        </div>
        {hint ? <p className="text-[13px] text-(--muted)">{hint}</p> : null}
        {create.isError ? (
          <p role="alert" className="text-sm text-[#8A1F1F]">
            {humanizePromoError(create.error)}
          </p>
        ) : null}
      </form>

      <Card as="div" className="flex flex-col gap-3">
        <div className="text-[13px] font-semibold text-(--muted)">Так акцію побачить клієнт</div>
        <div className="flex flex-col gap-2 rounded-2xl border-2 border-(--promo) bg-[#FFF8F3] p-4">
          <span className="self-start rounded-lg bg-[#FDE9DC] px-2 py-1 text-[13px] font-semibold text-(--promo)">
            −{percent}% до {ymdToDmy(to) || "…"}
          </span>
          <span className="text-base font-semibold">Манікюр + гель-лак</span>
          <span className="text-[13px] text-(--muted)">{preview.data?.locationSummary ?? "…"}</span>
          <span className="flex items-baseline gap-2.5">
            <span className="text-xl font-semibold">{preview.data ? money(preview.data.samplePrice) : "…"}</span>
            <span className="text-sm text-(--muted) line-through">{preview.data ? money(preview.data.sampleOriginal) : ""}</span>
          </span>
        </div>
      </Card>
    </div>
  );
}
