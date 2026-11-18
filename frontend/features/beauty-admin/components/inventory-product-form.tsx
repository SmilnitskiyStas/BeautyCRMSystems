"use client";

import { useEffect, useId, useRef, useState, type FormEvent } from "react";
import {
  useInventoryCategories,
  useInventoryProduct,
  useResetLocationSetting,
  useSaveProduct,
  useSetLocationSetting,
  useSuppliers,
} from "../hooks/use-inventory";
import {
  PURPOSE_LABEL,
  UNITS,
  humanizeInventoryError,
  numberToField,
  parseOptionalMinStock,
  toProductRequest,
  unitShort,
  validateProduct,
  type ProductErrors,
  type ProductFormValues,
} from "../inventory/logic";
import type { InventoryScope, Product, ProductPurpose, StockUnit } from "../inventory/types";
import { ErrorSummary, InputField, RequiredNote, SelectField, quantityInputMode, useFocusInvalid } from "./inventory-parts";
import { CheckRow, ErrorBanner, SuccessBanner } from "./staff-parts";
import { Card, PrimaryButton, SecondaryButton } from "./ui";

const valuesOf = (p: Product | null): ProductFormValues => ({
  name: p?.name ?? "",
  category: p?.category ?? "",
  unit: p?.unit ?? "pcs",
  barcode: p?.barcode ?? "",
  purpose: p?.purpose ?? "both",
  supplierId: p?.supplier?.id ?? "",
  purchasePrice: numberToField(p?.purchasePrice),
  minStock: numberToField(p?.minStock),
  trackExpiry: p?.trackExpiry ?? false,
  isActive: p?.isActive ?? true,
  note: p?.note ?? "",
});

/** Налаштування товару по закладах (мін. залишок закладу, вимкнення), §32.5: `inventory.manage >= own` + заклад у scope. */
function LocationSettings({ product, scope }: { product: Product; scope: InventoryScope }) {
  const detail = useInventoryProduct(product.id);
  const save = useSetLocationSetting(product.id);
  const reset = useResetLocationSetting(product.id);
  const [drafts, setDrafts] = useState<Record<string, { minStock: string; isEnabled: boolean }>>({});
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [message, setMessage] = useState<string | null>(null);
  const [serverError, setServerError] = useState<string | null>(null);

  const locations = scope.locations;
  if (detail.isPending) return <p role="status" className="text-sm text-(--muted)">Завантажуємо налаштування закладів…</p>;
  if (detail.isError) return <ErrorBanner>Не вдалося завантажити налаштування закладів.</ErrorBanner>;

  const settingOf = (id: string) => detail.data.locationSettings.find((s) => s.locationId === id);
  const draftOf = (id: string) => drafts[id] ?? { minStock: numberToField(settingOf(id)?.minStock), isEnabled: settingOf(id)?.isEnabled ?? true };

  async function saveOne(locationId: string) {
    const d = draftOf(locationId);
    const parsed = parseOptionalMinStock(d.minStock, product.unit);
    if (parsed.error) {
      setErrors((e) => ({ ...e, [locationId]: parsed.error! }));
      return;
    }
    setErrors((e) => ({ ...e, [locationId]: "" }));
    setServerError(null);
    try {
      await save.mutateAsync({ locationId, minStock: parsed.value, isEnabled: d.isEnabled });
      setDrafts((cur) => {
        const { [locationId]: _drop, ...rest } = cur;
        void _drop;
        return rest;
      });
      setMessage("Налаштування закладу збережено.");
    } catch (e) {
      setServerError(humanizeInventoryError(e));
    }
  }

  async function resetOne(locationId: string) {
    setServerError(null);
    try {
      await reset.mutateAsync(locationId);
      setDrafts((cur) => {
        const { [locationId]: _drop, ...rest } = cur;
        void _drop;
        return rest;
      });
      setMessage("Повернуто мережеві значення.");
    } catch (e) {
      setServerError(humanizeInventoryError(e));
    }
  }

  return (
    <section aria-label="Налаштування по закладах" className="flex flex-col gap-3">
      <h3 className="text-base font-semibold">По закладах</h3>
      <p className="text-[13px] text-(--muted)">
        Мінімальний залишок закладу замінює мережевий; порожньо - мережевий, «0» - без порогу. Вимкнений товар не приймається в закладі.
      </p>
      {locations.map((l) => {
        const d = draftOf(l.id);
        const has = settingOf(l.id) !== undefined;
        return (
          <div key={l.id} role="group" aria-label={`Заклад ${l.name}`} className="flex flex-col gap-2 rounded-xl border border-(--line-strong) p-3">
            <p className="text-sm font-semibold">{l.name}</p>
            <InputField
              label={`Мін. залишок у «${l.name}»`}
              inputMode={quantityInputMode(product.unit)}
              value={d.minStock}
              suffix={unitShort(product.unit)}
              disabled={!l.canManage}
              onChange={(v) => setDrafts((cur) => ({ ...cur, [l.id]: { ...draftOf(l.id), minStock: v } }))}
              error={errors[l.id] || undefined}
            />
            <CheckRow
              label="Товар доступний у цьому закладі"
              checked={d.isEnabled}
              disabled={!l.canManage}
              onChange={(on) => setDrafts((cur) => ({ ...cur, [l.id]: { ...draftOf(l.id), isEnabled: on } }))}
            />
            {l.canManage ? (
              <div className="flex flex-wrap gap-2">
                <SecondaryButton disabled={save.isPending} onClick={() => void saveOne(l.id)} aria-label={`Зберегти налаштування закладу ${l.name}`}>
                  Зберегти
                </SecondaryButton>
                {has ? (
                  <SecondaryButton disabled={reset.isPending} onClick={() => void resetOne(l.id)} aria-label={`Повернути мережеві значення для закладу ${l.name}`}>
                    Повернути мережеві
                  </SecondaryButton>
                ) : null}
              </div>
            ) : null}
          </div>
        );
      })}
      <div>
        {message ? <SuccessBanner>{message}</SuccessBanner> : null}
        {serverError ? <ErrorBanner>{serverError}</ErrorBanner> : null}
      </div>
    </section>
  );
}

/**
 * Форма товару (§32.5/§32.7). Закупівельна ціна - лише за `inventory.costs = edit` (інакше поле не показується і не
 * надсилається); для штук/упаковок мін. залишок цілий; одиницю не можна змінити, коли товар має рух (`unit_locked`).
 */
export function ProductForm({ product, scope, onClose }: { product: Product | null; scope: InventoryScope; onClose: () => void }) {
  const isEdit = product !== null;
  const save = useSaveProduct(product?.id ?? null);
  const categories = useInventoryCategories();
  const suppliers = useSuppliers({ active: "all", take: 200 });
  const listId = useId();
  const [values, setValues] = useState<ProductFormValues>(valuesOf(product));
  const [errors, setErrors] = useState<ProductErrors>({});
  const [serverError, setServerError] = useState<string | null>(null);
  const [saved, setSaved] = useState<string | null>(null);
  const [confirmingOff, setConfirmingOff] = useState(false);
  const [attachInvalid, requestInvalid] = useFocusInvalid<HTMLFormElement>();
  const confirmHeading = useRef<HTMLParagraphElement>(null);
  const canEditCosts = scope.costs === "edit";
  const readOnly = isEdit ? !scope.canEditCatalog : false;
  const set = <K extends keyof ProductFormValues>(k: K, v: ProductFormValues[K]) => {
    setValues((cur) => ({ ...cur, [k]: v }));
    setSaved(null);
    if (k === "isActive") setConfirmingOff(false);
  };
  const wasActive = product?.isActive ?? true;
  // Блок підтвердження з'являється на місці кнопки, що мала фокус: переносимо фокус на його текст.
  useEffect(() => {
    if (confirmingOff) confirmHeading.current?.focus();
  }, [confirmingOff]);

  async function doSave() {
    setServerError(null);
    try {
      await save.mutateAsync(toProductRequest(values, { canEditCosts, isEdit }));
      setConfirmingOff(false);
      if (isEdit) setSaved("Зміни збережено.");
      else onClose();
    } catch (e) {
      setConfirmingOff(false);
      setServerError(humanizeInventoryError(e, "Не вдалося зберегти товар. Спробуйте ще раз."));
    }
  }

  function submit(e: FormEvent) {
    e.preventDefault();
    setSaved(null);
    const next = validateProduct(values, { canEditCosts });
    setErrors(next);
    if (Object.keys(next).length > 0) {
      requestInvalid();
      return;
    }
    if (isEdit && wasActive && !values.isActive && !confirmingOff) {
      setConfirmingOff(true);
      return;
    }
    void doSave();
  }

  const formBody = (
    <form ref={attachInvalid} onSubmit={submit} noValidate className="flex flex-col gap-5" aria-label={isEdit ? `Товар «${product.name}»` : "Новий товар"}>
      <h2 className="text-lg font-semibold">{isEdit ? product.name : "Новий товар"}</h2>
      {readOnly ? <p className="text-sm text-(--muted)">Каталог товарів змінює лише керівник з правом «Усі заклади й каталог товарів». Нижче можна налаштувати свої заклади.</p> : null}
      {readOnly ? null : <RequiredNote />}
      <ErrorSummary count={Object.keys(errors).length} />
      <div className="grid gap-4 sm:grid-cols-2">
        <InputField label="Назва" required maxLength={200} value={values.name} onChange={(v) => set("name", v)} error={errors.name} disabled={readOnly} />
        <div>
          <InputField label="Категорія" list={listId} maxLength={100} value={values.category} onChange={(v) => set("category", v)} error={errors.category} disabled={readOnly} hint="Вільний текст; можна обрати з підказок" />
          <datalist id={listId}>
            {(categories.data ?? []).map((c) => (
              <option key={c} value={c} />
            ))}
          </datalist>
        </div>
        <SelectField
          label="Одиниця виміру"
          required
          value={values.unit}
          onChange={(v) => set("unit", v as StockUnit)}
          options={UNITS.map((u) => ({ value: u.id, label: `${u.label} (${u.short})` }))}
          disabled={readOnly || (isEdit && product.hasMovements)}
          hint={isEdit && product.hasMovements ? "Товар уже має рух: одиницю змінити не можна." : "Для штук і упаковок кількості цілі."}
        />
        <InputField label="Штрихкод" maxLength={64} value={values.barcode} onChange={(v) => set("barcode", v)} error={errors.barcode} disabled={readOnly} hint="Літери, цифри та . _ -" />
        <SelectField
          label="Призначення"
          required
          value={values.purpose}
          onChange={(v) => set("purpose", v as ProductPurpose)}
          options={(Object.keys(PURPOSE_LABEL) as ProductPurpose[]).map((p) => ({ value: p, label: PURPOSE_LABEL[p] }))}
          disabled={readOnly}
        />
        <SelectField
          label="Постачальник"
          value={values.supplierId}
          onChange={(v) => set("supplierId", v)}
          placeholder="Не вказано"
          options={suppliers.items.map((s) => ({ value: s.id, label: s.isActive ? s.name : `${s.name} (неактивний)` }))}
          disabled={readOnly}
        />
        {canEditCosts ? (
          <InputField label="Закупівельна ціна" inputMode="decimal" value={values.purchasePrice} onChange={(v) => set("purchasePrice", v)} suffix="₴" error={errors.purchasePrice} disabled={readOnly} hint="За одиницю виміру" />
        ) : null}
        <InputField
          label="Мінімальний залишок (мережа)"
          inputMode={quantityInputMode(values.unit)}
          value={values.minStock}
          onChange={(v) => set("minStock", v)}
          suffix={unitShort(values.unit)}
          error={errors.minStock}
          disabled={readOnly}
          hint="Порожньо або 0 - без порогу"
        />
      </div>
      <InputField label="Примітка" maxLength={500} value={values.note} onChange={(v) => set("note", v)} error={errors.note} disabled={readOnly} />
      <fieldset className="m-0 flex min-w-0 flex-col gap-1 border-0 p-0">
        <legend className="mb-1 text-[13px] font-semibold">Параметри</legend>
        <CheckRow label="Відстежувати термін придатності" hint="Для таких товарів у надходженні термін обов’язковий." checked={values.trackExpiry} onChange={(on) => set("trackExpiry", on)} disabled={readOnly} />
        {isEdit ? <CheckRow label="Товар активний" hint="Неактивний товар не приймається в надходженні; залишки лишаються." checked={values.isActive} onChange={(on) => set("isActive", on)} disabled={readOnly} /> : null}
      </fieldset>
      {confirmingOff ? (
        <div role="group" aria-labelledby="confirm-off-t" aria-describedby="confirm-off-d" className="flex flex-col gap-3 rounded-xl bg-[#FFF1CC] p-4 text-[#5C3900]">
          <p id="confirm-off-t" ref={confirmHeading} tabIndex={-1} className="text-sm font-semibold outline-none">
            Деактивувати товар?
          </p>
          <p id="confirm-off-d" className="text-sm">Товар перестане прийматися в надходженнях. Залишки, партії й журнал збережуться; списання й переміщення лишаються доступними.</p>
          <div className="flex flex-wrap gap-2">
            <PrimaryButton disabled={save.isPending} onClick={() => void doSave()}>
              Так, деактивувати
            </PrimaryButton>
            <SecondaryButton onClick={() => set("isActive", true)}>Залишити активним</SecondaryButton>
          </div>
        </div>
      ) : null}
      <div className="flex flex-col gap-2">
        {serverError ? <ErrorBanner>{serverError}</ErrorBanner> : null}
        {saved ? <SuccessBanner>{saved}</SuccessBanner> : null}
      </div>
      <div className="flex flex-wrap gap-2">
        {readOnly ? null : (
          <PrimaryButton type="submit" disabled={save.isPending}>
            {save.isPending ? "Зберігаємо…" : isEdit ? "Зберегти" : "Додати товар"}
          </PrimaryButton>
        )}
        <SecondaryButton onClick={onClose}>{isEdit ? "Закрити" : "Скасувати"}</SecondaryButton>
      </div>
    </form>
  );

  return (
    <Card as="div" className="flex flex-col gap-6">
      {formBody}
      {isEdit && scope.locations.length > 0 ? <LocationSettings product={product} scope={scope} /> : null}
    </Card>
  );
}
