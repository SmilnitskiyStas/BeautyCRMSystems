"use client";

import { useId, useState } from "react";
import { BeautyApiError } from "@/features/beauty-auth/errors";
import { useAuth } from "@/features/beauty-auth/components/auth-provider";
import { canEditInventoryNorms, canViewInventory } from "@/features/beauty-auth/permissions";
import { dateTimeLabel } from "../format";
import { useInventoryProducts } from "../hooks/use-inventory";
import { useDeleteServiceMaterials, useServiceMaterials, useSetServiceMaterials } from "../hooks/use-inventory-norms";
import { useLocations } from "../hooks/use-beauty-admin";
import { canBeNorm, formatQuantity, humanizeInventoryError, numberToField, unitShort, validateMaterialDraft, type MaterialDraftLine } from "../inventory/logic";
import type { MaterialSet, ProductRef } from "../inventory/types";
import { AiConfirmDialog } from "./ai-confirm-dialog";
import { ErrorSummary, InputField, SelectField, quantityInputMode, useFocusInvalid } from "./inventory-parts";
import { Badge, Card, FIELD_LABEL, FIELD_INPUT, PrimaryButton, QueryState, SecondaryButton } from "./ui";

const EMPTY_HINT = "Порожній набір закладу означає: у цьому закладі ця послуга матеріалів не списує.";

const toDraft = (set: MaterialSet | null | undefined): MaterialDraftLine[] => (set?.lines ?? []).map((l) => ({ productId: l.product.id, quantity: numberToField(l.quantity) }));

/** Читання: перелік матеріалів набору (без полів вводу). */
function ReadOnlySet({ title, set, emptyText }: { title: string; set: MaterialSet | null; emptyText: string }) {
  return (
    <section aria-label={title} data-testid="materials-scope" className="flex flex-col gap-2 rounded-xl border border-(--line) p-3.5">
      <h4 className="text-[15px] font-semibold">{title}</h4>
      {set === null ? (
        <p className="m-0 text-sm text-(--muted)">{emptyText}</p>
      ) : set.lines.length === 0 ? (
        <p className="m-0 text-sm text-(--muted)">{EMPTY_HINT}</p>
      ) : (
        <ul className="m-0 flex list-none flex-col gap-1 p-0 text-sm">
          {set.lines.map((l) => (
            <li key={l.product.id}>
              {l.product.name}: {formatQuantity(l.quantity, l.product.unit)}
              {!l.product.isActive ? <Badge tone="neutral" className="ml-2">Неактивний</Badge> : null}
            </li>
          ))}
        </ul>
      )}
      {set?.updatedBy ? <p className="m-0 text-xs text-(--muted)">Оновлено {set.updatedBy.name}, {dateTimeLabel(set.updatedAt)}</p> : null}
    </section>
  );
}

interface SetFormProps {
  serviceId: string;
  locationId: string | null;
  title: string;
  initial: MaterialSet | null;
  eligible: ProductRef[];
  onSaved?: () => void;
}

/** Одна область (мережа або заклад): рядки «товар + кількість», збереження повною заміною, видалення з підтвердженням. */
function SetForm({ serviceId, locationId, title, initial, eligible, onSaved }: SetFormProps) {
  const uid = useId();
  const save = useSetServiceMaterials(serviceId);
  const remove = useDeleteServiceMaterials(serviceId);
  const [draft, setDraft] = useState<MaterialDraftLine[]>(() => toDraft(initial));
  const initialKey = initial ? `${initial.updatedAt}|${initial.lines.map((l) => `${l.product.id}:${l.quantity}`).join(",")}` : "none";
  const [seenKey, setSeenKey] = useState(initialKey);
  if (seenKey !== initialKey) {
    setSeenKey(initialKey);
    setDraft(toDraft(initial));
  }
  const [lineErrors, setLineErrors] = useState<Record<number, string>>({});
  const [formError, setFormError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);
  const [confirming, setConfirming] = useState(false);
  const [attachInvalid, requestInvalid] = useFocusInvalid<HTMLFormElement>();

  const allowEmpty = locationId !== null;
  // Товар, що вже в наборі, лишається в списку, навіть якщо став неактивним/«для продажу» (інакше рядок не відобразити).
  const byId = new Map<string, ProductRef>(eligible.map((p) => [p.id, p]));
  for (const l of initial?.lines ?? []) if (!byId.has(l.product.id)) byId.set(l.product.id, l.product);
  const usedIds = new Set(draft.map((d) => d.productId));
  const errCount = Object.keys(lineErrors).length + (formError ? 1 : 0);

  const change = (next: MaterialDraftLine[]) => {
    setDraft(next);
    setSaved(false);
    setFormError(null);
    setLineErrors({});
  };

  function submit() {
    const r = validateMaterialDraft(draft, byId, allowEmpty);
    setLineErrors(r.errors.lines);
    setFormError(r.errors.form ?? null);
    if (!r.lines) {
      requestInvalid();
      return;
    }
    save.mutate(
      { locationId, lines: r.lines },
      {
        onSuccess: () => {
          setSaved(true);
          onSaved?.();
        },
        onError: (e) => {
          const line = e instanceof BeautyApiError ? e.body?.line : undefined;
          if (typeof line === "number" && Number.isInteger(line) && line >= 0 && line < draft.length) setLineErrors({ [line]: humanizeInventoryError(e) });
          else setFormError(humanizeInventoryError(e, "Не вдалося зберегти норму. Спробуйте ще раз."));
          requestInvalid();
        },
      },
    );
  }

  return (
    <form
      ref={attachInvalid}
      aria-label={title}
      data-testid="materials-scope"
      className="flex flex-col gap-3 rounded-xl border border-(--line) p-3.5"
      noValidate
      onSubmit={(e) => {
        e.preventDefault();
        submit();
      }}
    >
      <h4 className="text-[15px] font-semibold">{title}</h4>
      <ErrorSummary count={errCount} />
      {draft.length === 0 ? <p className="m-0 text-sm text-(--muted)">{allowEmpty ? EMPTY_HINT : "Матеріалів ще немає. Додайте перший."}</p> : null}
      <ul className="m-0 flex list-none flex-col gap-3 p-0">
        {draft.map((d, i) => {
          const p = byId.get(d.productId);
          const e = lineErrors[i];
          return (
            <li key={i} className="grid grid-cols-1 items-start gap-2 sm:grid-cols-[minmax(0,1fr)_minmax(0,180px)_auto]">
              <SelectField
                label={`Матеріал ${i + 1}`}
                value={d.productId}
                placeholder="Оберіть товар"
                onChange={(v) => change(draft.map((x, j) => (j === i ? { ...x, productId: v } : x)))}
                options={[...byId.values()]
                  .filter((x) => x.id === d.productId || !usedIds.has(x.id))
                  .map((x) => ({ value: x.id, label: `${x.name} (${unitShort(x.unit)})`, disabled: !canBeNorm(x) && x.id !== d.productId }))}
                {...(e && !p ? { error: e } : {})}
              />
              <InputField
                label={`Кількість ${i + 1}${p ? `, ${unitShort(p.unit)}` : ""}`}
                inputMode={quantityInputMode(p?.unit)}
                value={d.quantity}
                suffix={p ? unitShort(p.unit) : undefined}
                onChange={(v) => change(draft.map((x, j) => (j === i ? { ...x, quantity: v } : x)))}
                {...(e && p ? { error: e } : {})}
              />
              <div className="sm:pt-7">
                <SecondaryButton onClick={() => change(draft.filter((_, j) => j !== i))} aria-label={`Прибрати матеріал ${i + 1}${p ? `: ${p.name}` : ""}`}>
                  Прибрати
                </SecondaryButton>
              </div>
              {p && !p.isActive ? (
                <p role="note" className="m-0 text-[13px] text-[#5C3900] sm:col-span-3">
                  Товар неактивний: у списанні він пропускається.
                </p>
              ) : null}
            </li>
          );
        })}
      </ul>
      <div>
        <SecondaryButton onClick={() => change([...draft, { productId: "", quantity: "" }])} disabled={draft.length >= 50}>
          Додати матеріал
        </SecondaryButton>
      </div>
      {formError ? (
        <p id={`${uid}-form-e`} role="alert" className="m-0 rounded-xl bg-[#FBE4E4] px-3 py-2 text-sm text-[#8A1F1F]">
          {formError}
        </p>
      ) : null}
      {saved ? (
        <p role="status" className="m-0 rounded-xl bg-[#DDF3E6] px-3 py-2 text-sm text-[#145A32]">
          Збережено. Діє на візити, завершені після цього моменту; минулі списання не змінюються.
        </p>
      ) : null}
      {initial?.updatedBy ? <p className="m-0 text-xs text-(--muted)">Оновлено {initial.updatedBy.name}, {dateTimeLabel(initial.updatedAt)}</p> : null}
      {confirming ? (
        <AiConfirmDialog
          title={locationId === null ? "Видалити мережеву норму?" : "Видалити норму закладу?"}
          confirmLabel="Видалити"
          danger
          busy={remove.isPending}
          onCancel={() => setConfirming(false)}
          onConfirm={() =>
            remove.mutate(locationId, {
              onSuccess: () => setConfirming(false),
              onError: (e) => {
                setConfirming(false);
                setFormError(humanizeInventoryError(e, "Не вдалося видалити. Спробуйте ще раз."));
              },
            })
          }
        >
          <p>
            {locationId === null ? "Заклади без власного набору перестануть списувати матеріали для цієї послуги." : "Заклад знову використовуватиме мережевий набір."} Минулі списання не змінюються.
          </p>
        </AiConfirmDialog>
      ) : null}
      <div className="flex flex-wrap gap-2">
        <PrimaryButton type="submit" disabled={save.isPending}>
          {save.isPending ? "Зберігаємо…" : "Зберегти норму"}
        </PrimaryButton>
        {initial && !confirming ? (
          <SecondaryButton onClick={() => setConfirming(true)} aria-label={`Видалити: ${title}`}>
            Видалити
          </SecondaryButton>
        ) : null}
      </div>
    </form>
  );
}

function AddOverride({ serviceId, taken, eligible }: { serviceId: string; taken: Set<string>; eligible: ProductRef[] }) {
  const locations = useLocations();
  const [adding, setAdding] = useState(false);
  const [locationId, setLocationId] = useState("");
  const selectId = useId();
  const free = (locations.data ?? []).filter((l) => !taken.has(l.id));
  const chosen = free.find((l) => l.id === locationId);
  const reset = () => {
    setAdding(false);
    setLocationId("");
  };
  if (!adding) {
    return (
      <div>
        <SecondaryButton disabled={free.length === 0} onClick={() => setAdding(true)}>
          Додати набір для закладу
        </SecondaryButton>
        {free.length === 0 && locations.isSuccess ? <p className="mt-1 text-xs text-(--muted)">Для всіх закладів набір уже задано.</p> : null}
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
      {chosen ? <SetForm key={chosen.id} serviceId={serviceId} locationId={chosen.id} title={`Заклад: ${chosen.name}`} initial={null} eligible={eligible} onSaved={reset} /> : null}
      <div>
        <SecondaryButton onClick={reset}>Скасувати</SecondaryButton>
      </div>
    </div>
  );
}

/**
 * Розділ «Норми матеріалів» картки послуги (§32.16). Перегляд - `inventory.view`, редагування - `inventory.manage = all` (+ owner/admin на сервері).
 * Набір закладу ЗАМІНЮЄ мережевий повністю; порожній набір закладу = нічого не списується. У нормі лише товари `salon_use|both`.
 */
export function ServiceMaterialsEditor({ serviceId, serviceName, onClose }: { serviceId: string; serviceName: string; onClose: () => void }) {
  const { user } = useAuth();
  const canView = canViewInventory(user);
  const canEdit = canEditInventoryNorms(user);
  const q = useServiceMaterials(serviceId, canView);
  const productsQ = useInventoryProducts({ active: "true", take: 200 }, canView && canEdit);
  const eligible: ProductRef[] = productsQ.items
    .map((p): ProductRef => ({ id: p.id, name: p.name, unit: p.unit, purpose: p.purpose, category: p.category, barcode: p.barcode, isActive: p.isActive }))
    .filter(canBeNorm);
  const d = q.data;
  const disabled = q.error instanceof BeautyApiError && q.error.code === "module_disabled";

  return (
    <Card as="div" className="flex flex-col gap-4">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <h2 className="min-w-0 text-lg font-semibold break-words">Норми матеріалів: {serviceName}</h2>
        <SecondaryButton onClick={onClose}>Закрити</SecondaryButton>
      </div>
      <p className="m-0 rounded-xl bg-(--page) px-3.5 py-2.5 text-sm">
        Норма - скільки матеріалу списується зі складу, коли візит завершено. Набір закладу замінює мережевий повністю. {EMPTY_HINT} У нормі лише товари «для роботи закладу» або «і те, і інше».
      </p>
      {!canView ? (
        <p role="note" className="m-0 text-sm">
          Норми матеріалів доступні лише за правом перегляду складу.
        </p>
      ) : disabled ? (
        <p role="alert" className="m-0 text-sm">
          {humanizeInventoryError(q.error)}
        </p>
      ) : (
        <QueryState isPending={q.isPending} isError={q.isError} onRetry={() => q.refetch()}>
          {d ? (
            <>
              {!canEdit ? <p role="note" className="m-0 text-sm text-(--muted)">Перегляд: змінювати норми може лише власник чи адміністратор.</p> : null}
              {canEdit ? (
                <SetForm key="network" serviceId={serviceId} locationId={null} title="Для всієї мережі" initial={d.network} eligible={eligible} />
              ) : (
                <ReadOnlySet title="Для всієї мережі" set={d.network} emptyText="Мережевої норми немає." />
              )}
              <section className="flex flex-col gap-3" aria-label="Норми для закладів">
                <h3 className="text-[15px] font-semibold">Окремо для закладів</h3>
                {d.locations.length === 0 ? <p className="m-0 text-sm text-(--muted)">Заклади використовують мережевий набір.</p> : null}
                {d.locations.map((l) =>
                  canEdit ? (
                    <SetForm key={l.locationId} serviceId={serviceId} locationId={l.locationId} title={`Заклад: ${l.locationName}`} initial={l} eligible={eligible} />
                  ) : (
                    <ReadOnlySet key={l.locationId} title={`Заклад: ${l.locationName}`} set={l} emptyText="" />
                  ),
                )}
                {canEdit ? <AddOverride serviceId={serviceId} taken={new Set(d.locations.map((l) => l.locationId!))} eligible={eligible} /> : null}
              </section>
            </>
          ) : null}
        </QueryState>
      )}
    </Card>
  );
}
