"use client";

import { useId, useState, type FormEvent } from "react";
import { humanizeError } from "@/features/beauty-auth/errors";
import { useCreateLocation, useUpdateLocation } from "../hooks/use-beauty-admin";
import { DEFAULT_TIMEZONE, timeZoneOptions, validateLocation, type LocationErrors } from "../locations-logic";
import type { BeautyLocation, LocationInput } from "../types";
import { CheckRow, ErrorBanner, SuccessBanner, TextField, ERROR_TEXT } from "./staff-parts";
import { Card, FIELD_INPUT, FIELD_LABEL, PrimaryButton, SecondaryButton } from "./ui";


const inputOf = (l: BeautyLocation, isActive = l.isActive !== false): LocationInput => ({
  name: l.name,
  address: l.address ?? "",
  phone: l.phone ?? "",
  timezone: l.timezone ?? DEFAULT_TIMEZONE,
  isActive,
});

/** Поле часової зони: пошук по IANA-зонах через datalist (друк `Kyiv` -> `Europe/Kyiv`). */
function TimeZoneField({ value, onChange, error }: { value: string; onChange: (v: string) => void; error?: string }) {
  const id = useId();
  const listId = `${id}-list`;
  const describedBy = [`${id}-hint`, error ? `${id}-err` : ""].filter(Boolean).join(" ");
  return (
    <div className="flex flex-col gap-1.5">
      <label htmlFor={id} className={FIELD_LABEL}>
        Часова зона<span aria-hidden="true"> *</span>
      </label>
      <input
        id={id}
        type="text"
        list={listId}
        required
        autoComplete="off"
        spellCheck={false}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        aria-invalid={error ? true : undefined}
        aria-describedby={describedBy}
        className={`${FIELD_INPUT} aria-[invalid=true]:border-[#8A1F1F]`}
      />
      <datalist id={listId}>
        {timeZoneOptions().map((z) => (
          <option key={z} value={z} />
        ))}
      </datalist>
      <p id={`${id}-hint`} className="text-[13px] text-(--muted)">
        Почніть вводити назву міста, наприклад Kyiv або Warsaw.
      </p>
      {error ? (
        <p id={`${id}-err`} className={ERROR_TEXT}>
          {error}
        </p>
      ) : null}
    </div>
  );
}

/**
 * Форма закладу. Створення (§18.3) - мінімальна: назва, адреса, часова зона; редагування (сторінка закладу) -
 * ще телефон і активність. Деактивація потребує підтвердження (слоти й онлайн-запис зникнуть).
 */
export function LocationForm({
  location,
  onClose,
  onCreated,
}: {
  location: BeautyLocation | null;
  onClose?: () => void;
  onCreated?: (created: BeautyLocation) => void;
}) {
  const create = useCreateLocation();
  const update = useUpdateLocation(location?.id ?? "");
  const isEdit = location !== null;
  const wasActive = location ? location.isActive !== false : true;
  const [values, setValues] = useState<LocationInput>(
    location ? inputOf(location) : { name: "", address: "", phone: "", timezone: DEFAULT_TIMEZONE, isActive: true },
  );
  const [errors, setErrors] = useState<LocationErrors>({});
  const [serverError, setServerError] = useState<string | null>(null);
  const [confirmingOff, setConfirmingOff] = useState(false);
  const [saved, setSaved] = useState<string | null>(null);
  const set = <K extends keyof LocationInput>(k: K, v: LocationInput[K]) => {
    setValues((cur) => ({ ...cur, [k]: v }));
    setSaved(null);
    if (k === "isActive") setConfirmingOff(false);
  };
  const pending = create.isPending || update.isPending;

  async function save() {
    setServerError(null);
    try {
      if (location) {
        await update.mutateAsync(values);
        setSaved("Зміни збережено.");
        setConfirmingOff(false);
      } else {
        const created = await create.mutateAsync(values);
        onCreated?.(created);
        onClose?.();
      }
    } catch (err) {
      setConfirmingOff(false);
      setServerError(humanizeError(err, "Не вдалося зберегти заклад. Спробуйте ще раз."));
    }
  }

  function submit(e: FormEvent) {
    e.preventDefault();
    setSaved(null);
    const next = validateLocation(values);
    setErrors(next);
    if (Object.keys(next).length > 0) return;
    if (isEdit && wasActive && !values.isActive && !confirmingOff) {
      setConfirmingOff(true);
      return;
    }
    void save();
  }

  const body = (
    <form onSubmit={submit} noValidate className="flex flex-col gap-5" aria-label={isEdit ? "Основне про заклад" : "Новий заклад"}>
      {isEdit ? null : <h2 className="text-lg font-semibold">Новий заклад</h2>}
      <div className="grid gap-4 sm:grid-cols-2">
        <TextField label="Назва" required autoComplete="off" maxLength={100} value={values.name} onChange={(v) => set("name", v)} error={errors.name} />
        {isEdit ? (
          <TextField label="Телефон" type="tel" autoComplete="off" value={values.phone} onChange={(v) => set("phone", v)} error={errors.phone} hint="Необов’язково" />
        ) : null}
        <TextField label="Адреса" autoComplete="off" maxLength={200} value={values.address} onChange={(v) => set("address", v)} error={errors.address} hint="Необов’язково" />
        <TimeZoneField value={values.timezone} onChange={(v) => set("timezone", v)} error={errors.timezone} />
      </div>
      {isEdit ? (
        <fieldset className="m-0 flex min-w-0 flex-col gap-1 border-0 p-0">
          <legend className="mb-1 text-[13px] font-semibold">Статус</legend>
          <CheckRow
            label="Заклад активний"
            hint="Неактивний заклад не показується в онлайн-записі й у слотах. Наявні записи й графіки зберігаються."
            checked={values.isActive}
            onChange={(on) => set("isActive", on)}
          />
        </fieldset>
      ) : null}
      {confirmingOff ? (
        <div role="alertdialog" aria-label="Підтвердження деактивації" className="flex flex-col gap-3 rounded-xl bg-[#FFF1CC] p-4 text-[#5C3900]">
          <p className="text-sm">
            Заклад зникне з онлайн-запису. Якщо є майбутні записи, система не дозволить деактивувати — спершу перенесіть або скасуйте їх.
          </p>
          <div className="flex flex-wrap gap-2">
            <PrimaryButton disabled={pending} onClick={() => void save()}>
              Так, деактивувати
            </PrimaryButton>
            <SecondaryButton onClick={() => set("isActive", true)}>Залишити активним</SecondaryButton>
          </div>
        </div>
      ) : null}
      {serverError ? <ErrorBanner>{serverError}</ErrorBanner> : null}
      {saved ? <SuccessBanner>{saved}</SuccessBanner> : null}
      <div className="flex flex-wrap gap-2">
        <PrimaryButton type="submit" disabled={pending}>
          {pending ? "Зберігаємо…" : isEdit ? "Зберегти" : "Додати заклад"}
        </PrimaryButton>
        {onClose ? <SecondaryButton onClick={onClose}>{isEdit ? "Закрити" : "Скасувати"}</SecondaryButton> : null}
      </div>
    </form>
  );
  return isEdit ? body : <Card as="div">{body}</Card>;
}
