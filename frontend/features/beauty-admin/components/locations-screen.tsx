"use client";

import { useId, useState, type FormEvent } from "react";
import { useAuth } from "@/features/beauty-auth/components/auth-provider";
import { humanizeError } from "@/features/beauty-auth/errors";
import { useCreateLocation, useManagedLocations, useUpdateLocation } from "../hooks/use-beauty-admin";
import { DEFAULT_TIMEZONE, timeZoneOptions, validateLocation, type LocationErrors } from "../locations-logic";
import type { BeautyLocation, LocationInput } from "../types";
import { CheckRow, ErrorBanner, SuccessBanner, TextField, ERROR_TEXT } from "./staff-parts";
import { Badge, Card, EmptyState, FIELD_INPUT, FIELD_LABEL, PageHeader, PrimaryButton, QueryState, SecondaryButton } from "./ui";

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

function LocationForm({ location, onClose }: { location: BeautyLocation | null; onClose: () => void }) {
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
        await create.mutateAsync(values);
        onClose();
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
    // Деактивація потребує підтвердження (слоти й онлайн-запис у закладі зникнуть).
    if (isEdit && wasActive && !values.isActive && !confirmingOff) {
      setConfirmingOff(true);
      return;
    }
    void save();
  }

  return (
    <Card as="div">
      <form onSubmit={submit} noValidate className="flex flex-col gap-5" aria-label={isEdit ? "Редагування закладу" : "Новий заклад"}>
        <h2 className="text-lg font-semibold">{isEdit ? `Заклад «${location.name}»` : "Новий заклад"}</h2>
        <div className="grid gap-4 sm:grid-cols-2">
          <TextField label="Назва" required autoComplete="off" maxLength={100} value={values.name} onChange={(v) => set("name", v)} error={errors.name} />
          <TextField label="Телефон" type="tel" autoComplete="off" value={values.phone} onChange={(v) => set("phone", v)} error={errors.phone} hint="Необов’язково" />
          <TextField label="Адреса" autoComplete="off" maxLength={200} value={values.address} onChange={(v) => set("address", v)} error={errors.address} hint="Необов’язково" />
          <TimeZoneField value={values.timezone} onChange={(v) => set("timezone", v)} error={errors.timezone} />
        </div>
        {isEdit ? (
          <fieldset className="m-0 flex flex-col gap-1 border-0 p-0">
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
          <SecondaryButton onClick={onClose}>{isEdit ? "Закрити" : "Скасувати"}</SecondaryButton>
        </div>
      </form>
    </Card>
  );
}

function LocationRow({ l, onEdit }: { l: BeautyLocation; onEdit: () => void }) {
  const update = useUpdateLocation(l.id);
  const active = l.isActive !== false;
  const [confirming, setConfirming] = useState(false);
  const [error, setError] = useState<string | null>(null);

  function setActive(next: boolean) {
    setError(null);
    update.mutate(inputOf(l, next), {
      onSuccess: () => setConfirming(false),
      onError: (e) => {
        setConfirming(false);
        setError(humanizeError(e, "Не вдалося змінити статус закладу."));
      },
    });
  }

  return (
    <li className="flex min-w-0 flex-col gap-3 rounded-2xl bg-white p-5">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h2 className="text-base font-semibold break-words">{l.name}</h2>
        {active ? <Badge tone="ok">Активний</Badge> : <Badge tone="neutral">Неактивний</Badge>}
      </div>
      <dl className="m-0 flex flex-col gap-1.5 text-sm">
        <div className="flex gap-2">
          <dt className="w-24 flex-none text-(--muted)">Адреса</dt>
          <dd className="m-0 min-w-0 break-words">{l.address || "—"}</dd>
        </div>
        <div className="flex gap-2">
          <dt className="w-24 flex-none text-(--muted)">Телефон</dt>
          <dd className="m-0 min-w-0">{l.phone || "—"}</dd>
        </div>
        <div className="flex gap-2">
          <dt className="w-24 flex-none text-(--muted)">Часова зона</dt>
          <dd className="m-0 min-w-0">{l.timezone ?? "—"}</dd>
        </div>
      </dl>
      {error ? <ErrorBanner>{error}</ErrorBanner> : null}
      {confirming ? (
        <div role="alertdialog" aria-label={`Деактивувати «${l.name}»`} className="flex flex-col gap-3 rounded-xl bg-[#FFF1CC] p-4 text-[#5C3900]">
          <p className="text-sm">
            Заклад зникне з онлайн-запису й слотів. Наявні записи й графіки зберігаються. Якщо є майбутні записи, деактивація не вдасться.
          </p>
          <div className="flex flex-wrap gap-2">
            <PrimaryButton disabled={update.isPending} onClick={() => setActive(false)}>
              {update.isPending ? "Деактивуємо…" : "Так, деактивувати"}
            </PrimaryButton>
            <SecondaryButton onClick={() => setConfirming(false)}>Залишити активним</SecondaryButton>
          </div>
        </div>
      ) : (
        <div className="flex flex-wrap gap-2">
          <SecondaryButton aria-label={`Редагувати «${l.name}»`} onClick={onEdit}>
            Редагувати
          </SecondaryButton>
          {active ? (
            <SecondaryButton aria-label={`Деактивувати «${l.name}»`} onClick={() => setConfirming(true)}>
              Деактивувати
            </SecondaryButton>
          ) : (
            <PrimaryButton aria-label={`Активувати «${l.name}»`} disabled={update.isPending} onClick={() => setActive(true)}>
              Активувати
            </PrimaryButton>
          )}
        </div>
      )}
    </li>
  );
}

export function LocationsScreen() {
  const { user } = useAuth();
  const isManager = user.role !== "specialist";
  const q = useManagedLocations(isManager);
  const [form, setForm] = useState<{ location: BeautyLocation | null } | null>(null);

  if (!isManager) {
    return (
      <>
        <PageHeader title="Заклади" />
        <Card>
          <p className="text-sm">Керувати закладами можуть лише власник і адміністратор.</p>
        </Card>
      </>
    );
  }

  const sorted = [...(q.data ?? [])].sort(
    (a, b) => Number(b.isActive !== false) - Number(a.isActive !== false) || a.name.localeCompare(b.name, "uk"),
  );

  return (
    <>
      <PageHeader
        title="Заклади"
        subtitle="Філії мережі: адреса, контакти й часова зона"
        actions={
          form ? null : (
            <PrimaryButton onClick={() => setForm({ location: null })}>Додати заклад</PrimaryButton>
          )
        }
      />
      {form ? <LocationForm key={form.location?.id ?? "new"} location={form.location} onClose={() => setForm(null)} /> : null}
      <QueryState isPending={q.isPending} isError={q.isError} onRetry={() => q.refetch()}>
        {sorted.length === 0 ? (
          <Card>
            <EmptyState>Закладів ще немає. Додайте перший.</EmptyState>
          </Card>
        ) : (
          <ul className="m-0 grid list-none gap-4 p-0" style={{ gridTemplateColumns: "repeat(auto-fill, minmax(min(100%, 320px), 1fr))" }} aria-label="Заклади">
            {sorted.map((l) => (
              <LocationRow key={l.id} l={l} onEdit={() => setForm({ location: l })} />
            ))}
          </ul>
        )}
      </QueryState>
    </>
  );
}
