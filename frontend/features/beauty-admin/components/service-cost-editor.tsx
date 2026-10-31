"use client";

import { useId, useState } from "react";
import { BeautyApiError, humanizeError } from "@/features/beauty-auth/errors";
import { dateTimeLabel, moneyExact } from "../format";
import { useDeleteServiceCost, useServiceCostDetail, useSetServiceCost } from "../hooks/use-finance";
import { useLocations } from "../hooks/use-beauty-admin";
import { lossWarning, toFormValues, validateCostForm, type CostFormErrors } from "../service-costs";
import type { LocationId, ServiceCost } from "../types";
import { AiConfirmDialog } from "./ai-confirm-dialog";
import { Card, FIELD_INPUT, FIELD_LABEL, PrimaryButton, QueryState, SecondaryButton } from "./ui";

interface ScopeProps {
  serviceId: string;
  /** `null` - мережевий рядок. */
  locationId: LocationId | null;
  title: string;
  initial?: ServiceCost;
  /** Мережева ціна послуги - для попередження про збитковість (ціни закладів у цьому екрані недоступні). */
  price?: number;
  onSaved?: () => void;
}

/** Одна область: мережа або заклад. Окремі поля, валідація, помилки 422 під полями, видалення з підтвердженням. */
function ScopeForm({ serviceId, locationId, title, initial, price, onSaved }: ScopeProps) {
  const uid = useId();
  const save = useSetServiceCost(serviceId);
  const remove = useDeleteServiceCost(serviceId);
  const [values, setValues] = useState(() => toFormValues(initial));
  // Серверне значення змінилося (збереження, видалення, оновлення іншим адміном) - форма підхоплює його, не втрачаючи повідомлення «Збережено».
  const initialKey = initial ? `${initial.updatedAt}|${initial.costAmount}|${initial.masterPercent}` : "none";
  const [seenKey, setSeenKey] = useState(initialKey);
  if (seenKey !== initialKey) {
    setSeenKey(initialKey);
    setValues(toFormValues(initial));
  }
  const [errors, setErrors] = useState<CostFormErrors>({});
  const [saved, setSaved] = useState(false);
  const [formError, setFormError] = useState<string | null>(null);
  const [confirming, setConfirming] = useState(false);

  const warning = lossWarning(price, values);
  const dirty = !initial || values.cost.trim() !== toFormValues(initial).cost || values.percent.trim() !== toFormValues(initial).percent;

  const change = (patch: Partial<typeof values>) => {
    setValues((v) => ({ ...v, ...patch }));
    setSaved(false);
    setFormError(null);
    setErrors({});
  };

  function submit() {
    const r = validateCostForm(values);
    setErrors(r.errors);
    if (!r.value) return;
    setFormError(null);
    save.mutate(
      { locationId, ...r.value },
      {
        onSuccess: () => {
          setSaved(true);
          onSaved?.();
        },
        onError: (e) => {
          // 422 показуємо під відповідним полем, решту - над кнопками.
          if (e instanceof BeautyApiError && e.code === "invalid_cost") setErrors({ cost: humanizeError(e) });
          else if (e instanceof BeautyApiError && e.code === "invalid_master_percent") setErrors({ percent: humanizeError(e) });
          else setFormError(humanizeError(e, "Не вдалося зберегти. Спробуйте ще раз."));
        },
      },
    );
  }

  return (
    <form
      aria-label={title}
      data-testid="cost-scope"
      className="flex flex-col gap-3 rounded-xl border border-(--line) p-3.5"
      onSubmit={(e) => {
        e.preventDefault();
        submit();
      }}
      noValidate
    >
      <h4 className="text-[15px] font-semibold">{title}</h4>
      <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
        <div className="flex flex-col gap-1">
          <label htmlFor={`${uid}-cost`} className={FIELD_LABEL}>
            Собівартість, ₴
          </label>
          <input
            id={`${uid}-cost`}
            inputMode="decimal"
            autoComplete="off"
            value={values.cost}
            onChange={(e) => change({ cost: e.target.value })}
            aria-invalid={errors.cost ? true : undefined}
            aria-describedby={errors.cost ? `${uid}-cost-e` : undefined}
            className={FIELD_INPUT}
          />
          {errors.cost ? (
            <p id={`${uid}-cost-e`} role="alert" className="m-0 text-[13px] font-semibold text-[#9B1C1C]">
              {errors.cost}
            </p>
          ) : null}
        </div>
        <div className="flex flex-col gap-1">
          <label htmlFor={`${uid}-pct`} className={FIELD_LABEL}>
            Відсоток майстру, %
          </label>
          <input
            id={`${uid}-pct`}
            inputMode="decimal"
            autoComplete="off"
            value={values.percent}
            placeholder="необов’язково"
            onChange={(e) => change({ percent: e.target.value })}
            aria-invalid={errors.percent ? true : undefined}
            aria-describedby={`${uid}-pct-h${errors.percent ? ` ${uid}-pct-e` : ""}`}
            className={FIELD_INPUT}
          />
          <p id={`${uid}-pct-h`} className="m-0 text-xs text-(--muted)">
            Виплата майстру, % від ціни продажу (0–100).
          </p>
          {errors.percent ? (
            <p id={`${uid}-pct-e`} role="alert" className="m-0 text-[13px] font-semibold text-[#9B1C1C]">
              {errors.percent}
            </p>
          ) : null}
        </div>
      </div>

      {warning ? (
        <p role="note" data-testid="loss-warning" className="m-0 rounded-xl bg-[#FFF1CC] px-3 py-2 text-sm text-[#5C3900]">
          {warning}
        </p>
      ) : null}
      {initial?.updatedBy ? (
        <p className="m-0 text-xs text-(--muted)">
          Оновлено {initial.updatedBy.name}, {dateTimeLabel(initial.updatedAt)}
        </p>
      ) : null}
      {formError ? (
        <p role="alert" className="m-0 rounded-xl bg-[#FBE4E4] px-3 py-2 text-sm text-[#8A1F1F]">
          {formError}
        </p>
      ) : null}
      {saved ? (
        <p role="status" className="m-0 rounded-xl bg-[#DDF3E6] px-3 py-2 text-sm text-[#145A32]">
          Збережено. Діє на записи, завершені після цього моменту.
        </p>
      ) : null}

      {confirming ? (
        <AiConfirmDialog
          title={locationId === null ? "Видалити мережеву собівартість?" : "Видалити значення для закладу?"}
          confirmLabel="Видалити"
          danger
          busy={remove.isPending}
          onCancel={() => setConfirming(false)}
          onConfirm={() =>
            remove.mutate(locationId, {
              onSuccess: () => setConfirming(false),
              onError: (e) => {
                setConfirming(false);
                setFormError(humanizeError(e, "Не вдалося видалити. Спробуйте ще раз."));
              },
            })
          }
        >
          <p>
            {locationId === null
              ? "Нові завершені записи без значення закладу рахуватимуться без собівартості."
              : "Заклад знову використовуватиме мережеву собівартість."}{" "}
            Минулі записи не змінюються.
          </p>
        </AiConfirmDialog>
      ) : null}

      <div className="flex flex-wrap gap-2">
        <PrimaryButton type="submit" disabled={save.isPending || (!dirty && !!initial)}>
          {save.isPending ? "Зберігаємо…" : "Зберегти"}
        </PrimaryButton>
        {initial && !confirming ? (
          <SecondaryButton onClick={() => setConfirming(true)} aria-label={`Видалити: ${title}`}>
            {locationId === null ? "Очистити" : "Видалити"}
          </SecondaryButton>
        ) : null}
      </div>
    </form>
  );
}

function AddOverride({ serviceId, taken, price }: { serviceId: string; taken: Set<string>; price?: number }) {
  const locations = useLocations();
  const [adding, setAdding] = useState(false);
  const [locationId, setLocationId] = useState("");
  const selectId = useId();
  const free = (locations.data ?? []).filter((l) => !taken.has(l.id));
  const chosen = free.find((l) => l.id === locationId);

  if (!adding) {
    return (
      <div>
        <SecondaryButton disabled={free.length === 0} onClick={() => setAdding(true)}>
          Додати для закладу
        </SecondaryButton>
        {free.length === 0 && locations.isSuccess ? <p className="mt-1 text-xs text-(--muted)">Для всіх закладів значення вже задано.</p> : null}
      </div>
    );
  }
  return (
    <div className="flex flex-col gap-3">
      <div className="flex flex-col gap-1">
        <label htmlFor={selectId} className={FIELD_LABEL}>
          Заклад
        </label>
        <select id={selectId} value={locationId} onChange={(e) => setLocationId(e.target.value)} className={FIELD_INPUT}>
          <option value="">Оберіть заклад</option>
          {free.map((l) => (
            <option key={l.id} value={l.id}>
              {l.name}
            </option>
          ))}
        </select>
      </div>
      {chosen ? (
        <ScopeForm
          key={chosen.id}
          serviceId={serviceId}
          locationId={chosen.id}
          title={`Заклад: ${chosen.name}`}
          price={price}
          onSaved={() => {
            setAdding(false);
            setLocationId("");
          }}
        />
      ) : null}
      <div>
        <SecondaryButton
          onClick={() => {
            setAdding(false);
            setLocationId("");
          }}
        >
          Скасувати
        </SecondaryButton>
      </div>
    </div>
  );
}

/**
 * Розділ «Собівартість і виплата майстру» картки послуги (§24.8.2). Показується лише owner/admin (екран перевіряє роль,
 * для specialist запитів немає). Мережеве значення + override закладів; зміна не перераховує завершені записи.
 */
export function ServiceCostEditor({ serviceId, serviceName, price, onClose }: { serviceId: string; serviceName: string; price?: number; onClose: () => void }) {
  const q = useServiceCostDetail(serviceId);
  const d = q.data;
  return (
    <Card as="div" className="flex flex-col gap-4" >
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="flex min-w-0 flex-col gap-1">
          <h2 className="text-lg font-semibold break-words">Собівартість і виплата майстру: {serviceName}</h2>
          {price !== undefined ? <p className="m-0 text-sm text-(--muted)">Мережева ціна: {moneyExact(price)}</p> : null}
        </div>
        <SecondaryButton onClick={onClose}>Закрити</SecondaryButton>
      </div>
      <p className="m-0 rounded-xl bg-(--page) px-3.5 py-2.5 text-sm">Діє на записи, завершені після збереження; минулі записи не змінюються.</p>
      <QueryState isPending={q.isPending} isError={q.isError} onRetry={() => q.refetch()}>
        {d ? (
          <>
            <ScopeForm key="network" serviceId={serviceId} locationId={null} title="Для всієї мережі" initial={d.network} price={price} />
            <section className="flex flex-col gap-3" aria-label="Значення для закладів">
              <h3 className="text-[15px] font-semibold">Окремо для закладів</h3>
              {d.locations.length === 0 ? <p className="m-0 text-sm text-(--muted)">Заклади використовують мережеве значення.</p> : null}
              {d.locations.map((l) => (
                <ScopeForm key={l.locationId} serviceId={serviceId} locationId={l.locationId} title={`Заклад: ${l.locationName}`} initial={l} price={price} />
              ))}
              <AddOverride serviceId={serviceId} taken={new Set(d.locations.map((l) => l.locationId))} price={price} />
            </section>
          </>
        ) : null}
      </QueryState>
    </Card>
  );
}
