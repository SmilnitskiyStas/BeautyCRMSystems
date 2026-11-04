"use client";

import { useEffect, useId, useRef, useState, type FormEvent } from "react";
import { BeautyApiError, humanizeError } from "@/features/beauty-auth/errors";
import { useCreateService, useLocations, useStaff } from "../hooks/use-beauty-admin";
import {
  hasErrors,
  newProgress,
  parsePrice,
  PartialCreateError,
  validateServiceForm,
  type CreateProgress,
  type ServiceFormErrors,
  type ServiceFormValues,
} from "../service-create";
import { CheckRow, ErrorBanner, TextField } from "./staff-parts";
import { Card, FIELD_INPUT, FIELD_LABEL, PrimaryButton, SecondaryButton } from "./ui";

const EMPTY: ServiceFormValues = { name: "", category: "", duration: "", description: "", price: "", locationPrices: {} };

function DescriptionField({ value, onChange, disabled }: { value: string; onChange: (v: string) => void; disabled?: boolean }) {
  const id = useId();
  return (
    <div className="flex flex-col gap-1.5 sm:col-span-2">
      <label htmlFor={id} className={FIELD_LABEL}>
        Опис
      </label>
      <textarea
        id={id}
        rows={3}
        maxLength={500}
        value={value}
        disabled={disabled}
        onChange={(e) => onChange(e.target.value)}
        aria-describedby={`${id}-hint`}
        className={`${FIELD_INPUT} disabled:bg-(--page) disabled:text-(--muted)`}
      />
      <p id={`${id}-hint`} className="text-[13px] text-(--muted)">
        Необов’язково
      </p>
    </div>
  );
}

/**
 * Форма нової послуги: основні поля, мережева ціна, ціни по закладах (порожнє = мережева) і майстри (мультивибір).
 * Створення - кілька запитів; після збою на наступному кроці повтор не створює послугу вдруге (`progress`),
 * а всі поля блокуються, щоб зміна значення не ігнорувалась мовчки.
 */
export function ServiceForm({ onClose, onCreated }: { onClose: () => void; onCreated: (name: string) => void }) {
  const create = useCreateService();
  const locations = useLocations();
  const staff = useStaff();
  const progress = useRef<CreateProgress>(newProgress());
  const inFlight = useRef(false);
  const heading = useRef<HTMLHeadingElement>(null);
  const formRef = useRef<HTMLFormElement>(null);
  const statusRef = useRef<HTMLDivElement>(null);
  const confirmRef = useRef<HTMLDivElement>(null);
  const headingId = useId();
  const locHintId = useId();
  const [values, setValues] = useState<ServiceFormValues>(EMPTY);
  const [errors, setErrors] = useState<ServiceFormErrors>({ locationPrices: {} });
  const [focusTick, setFocusTick] = useState(0);
  const [selected, setSelected] = useState<string[]>([]);
  const [serverError, setServerError] = useState<string | null>(null);
  /** Текст часткового збою - у постійному контейнері role=status. */
  const [partial, setPartial] = useState<string | null>(null);
  /** Послугу вже створено (збій на наступному кроці): усі поля заблоковано, кнопка - «Повторити». */
  const [created, setCreated] = useState(false);
  const [confirmClose, setConfirmClose] = useState(false);

  useEffect(() => {
    heading.current?.focus();
  }, []);

  // Після невдалої валідації фокус - на перше невалідне поле (після рендера з aria-invalid).
  useEffect(() => {
    if (focusTick > 0) formRef.current?.querySelector<HTMLElement>('[aria-invalid="true"]')?.focus();
  }, [focusTick]);

  useEffect(() => {
    if (partial) statusRef.current?.focus();
  }, [partial]);

  useEffect(() => {
    if (confirmClose) confirmRef.current?.focus();
  }, [confirmClose]);

  const set = <K extends keyof ServiceFormValues>(k: K, v: ServiceFormValues[K]) => setValues((cur) => ({ ...cur, [k]: v }));
  const setLocPrice = (id: string, v: string) => setValues((cur) => ({ ...cur, locationPrices: { ...cur.locationPrices, [id]: v } }));
  const toggle = (id: string, on: boolean) => setSelected((cur) => (on ? [...cur, id] : cur.filter((x) => x !== id)));

  const locationList = locations.data ?? [];
  const masters = (staff.data ?? []).filter((m) => m.isActive);
  const errorCount = (errors.name ? 1 : 0) + (errors.duration ? 1 : 0) + (errors.price ? 1 : 0) + Object.keys(errors.locationPrices).length;

  /** Підпис закладу; для однакових назв додається адреса або номер. */
  const locationLabel = (l: { name: string; address?: string | null }, index: number) => {
    const dup = locationList.filter((x) => x.name === l.name).length > 1;
    const suffix = dup ? ` (${l.address?.trim() || `№${index + 1}`})` : "";
    return `Ціна: ${l.name}${suffix}, ₴ (необов’язково)`;
  };

  async function submit(e: FormEvent) {
    e.preventDefault();
    if (create.isPending || inFlight.current) return;
    setServerError(null);
    const next = validateServiceForm(values);
    setErrors(next);
    if (hasErrors(next)) {
      setFocusTick((t) => t + 1);
      return;
    }
    const plan = {
      input: {
        name: values.name,
        description: values.description,
        category: values.category,
        durationMinutes: Number(values.duration.trim()),
        isActive: true,
      },
      networkPrice: parsePrice(values.price)!,
      locationPrices: locationList.flatMap((l) => {
        const raw = values.locationPrices[l.id] ?? "";
        return raw.trim() ? [{ locationId: l.id, locationName: l.name, price: parsePrice(raw)! }] : [];
      }),
      specialists: masters.filter((m) => selected.includes(m.id)).map((m) => ({ id: m.id, name: m.name })),
    };
    inFlight.current = true;
    setPartial(null);
    try {
      await create.mutateAsync({ plan, progress: progress.current });
      onCreated(values.name.trim());
      onClose();
    } catch (err) {
      if (err instanceof PartialCreateError) {
        setCreated(true);
        const forbidden = err.specialistId && err.reason instanceof BeautyApiError && err.reason.status === 403 && err.reason.code === "forbidden_role";
        if (forbidden) {
          // На клієнті немає ознаки ролі користувача, до якого прив'язано майстра: пропускаємо його при повторі.
          progress.current.specialistsSkipped.push(err.specialistId!);
          setPartial(
            `Послугу створено, але ${err.step}: цього майстра змінює лише власник. Натисніть «Повторити» — цього майстра буде пропущено, решту кроків буде завершено. Послугу майстру можна додати пізніше на його сторінці.`,
          );
        } else {
          setPartial(`Послугу створено, але ${err.step}: ${humanizeError(err.reason, "спробуйте ще раз")} Натисніть «Повторити», щоб завершити решту кроків — послуга не дублюватиметься.`);
        }
      } else {
        setServerError(humanizeError(err, "Не вдалося створити послугу. Спробуйте ще раз."));
      }
    } finally {
      inFlight.current = false;
    }
  }

  return (
    <Card as="div">
      <form ref={formRef} onSubmit={(e) => void submit(e)} noValidate className="flex flex-col gap-5" aria-labelledby={headingId}>
        <h2 id={headingId} ref={heading} tabIndex={-1} className="text-lg font-semibold outline-none">
          Нова послуга
        </h2>
        <p className="text-[13px] text-(--muted)">* — обов’язкові поля</p>
        <div className="grid gap-4 sm:grid-cols-2">
          <TextField label="Назва" required autoComplete="off" maxLength={100} disabled={created} value={values.name} onChange={(v) => set("name", v)} error={errors.name} />
          <TextField label="Категорія" autoComplete="off" maxLength={100} disabled={created} value={values.category} onChange={(v) => set("category", v)} hint="Необов’язково, наприклад «Волосся»" />
          <TextField
            label="Тривалість, хв"
            required
            autoComplete="off"
            disabled={created}
            value={values.duration}
            onChange={(v) => set("duration", v)}
            error={errors.duration}
            hint="Ціле число хвилин, від 5 до 720; стільки часу блокується в календарі майстра"
          />
          <TextField label="Мережева ціна, ₴" required autoComplete="off" disabled={created} value={values.price} onChange={(v) => set("price", v)} error={errors.price} hint="Діє в усіх закладах, де немає окремої ціни" />
          <DescriptionField value={values.description} disabled={created} onChange={(v) => set("description", v)} />
        </div>

        <div className="flex flex-col gap-3">
          {locations.isPending ? (
            <p className="text-sm text-(--muted)">Завантажуємо заклади…</p>
          ) : locations.isError ? (
            <p className="text-sm text-(--muted)">Не вдалося завантажити заклади. Діятиме мережева ціна; ціни закладів можна задати пізніше.</p>
          ) : locationList.length === 0 ? (
            <p className="text-sm text-(--muted)" data-testid="no-locations">
              Закладів ще немає — діятиме мережева ціна. Додайте заклад у розділі «Заклади», щоб задавати окремі ціни.
            </p>
          ) : (
            <fieldset aria-describedby={locHintId} className="m-0 flex min-w-0 flex-col gap-3 border-0 p-0">
              <legend className="mb-1 text-[13px] font-semibold">Ціни по закладах</legend>
              <p id={locHintId} className="text-[13px] text-(--muted)">
                Порожнє поле — діє мережева ціна.
              </p>
              <div className="grid gap-4 sm:grid-cols-2">
                {locationList.map((l, i) => (
                  <TextField
                    key={l.id}
                    label={locationLabel(l, i)}
                    autoComplete="off"
                    disabled={created}
                    value={values.locationPrices[l.id] ?? ""}
                    onChange={(v) => setLocPrice(l.id, v)}
                    error={errors.locationPrices[l.id]}
                  />
                ))}
              </div>
            </fieldset>
          )}
        </div>

        <div className="flex flex-col gap-1">
          {staff.isPending ? (
            <p className="text-sm text-(--muted)">Завантажуємо майстрів…</p>
          ) : staff.isError ? (
            <p className="text-sm text-(--muted)">Не вдалося завантажити майстрів. Призначити послугу можна пізніше на сторінці майстра.</p>
          ) : masters.length === 0 ? (
            <p className="text-sm text-(--muted)">Активних майстрів ще немає.</p>
          ) : (
            <fieldset className="m-0 flex min-w-0 flex-col gap-1 border-0 p-0">
              <legend className="mb-1 text-[13px] font-semibold">Майстри</legend>
              <p className="text-[13px] text-(--muted)">Необов’язково. Послугу буде додано до наявних послуг обраних майстрів.</p>
              {masters.map((m) => (
                <CheckRow key={m.id} label={m.name} hint={m.position ?? undefined} disabled={created} checked={selected.includes(m.id)} onChange={(on) => toggle(m.id, on)} />
              ))}
            </fieldset>
          )}
        </div>

        {errorCount > 0 ? (
          <p role="alert" className="rounded-xl bg-[#FBE4E4] px-3.5 py-2.5 text-sm text-[#8A1F1F]">
            {`Послугу не збережено: виправте поля з помилками (${errorCount}).`}
          </p>
        ) : null}
        {serverError ? <ErrorBanner>{serverError}</ErrorBanner> : null}
        {/* Постійний контейнер: оголошення «Зберігаємо…» і часткового збою не губляться. */}
        <div
          ref={statusRef}
          role="status"
          aria-live="polite"
          tabIndex={-1}
          className={partial ? "rounded-xl bg-[#FBE4E4] px-3.5 py-2.5 text-sm text-[#8A1F1F] outline-none" : "sr-only"}
        >
          {create.isPending ? "Зберігаємо послугу…" : (partial ?? "")}
        </div>
        {confirmClose ? (
          <div ref={confirmRef} tabIndex={-1} role="group" aria-label="Підтвердження закриття" className="flex flex-col gap-2 rounded-xl border-2 border-[#8A1F1F] p-3.5 outline-none">
            <p className="text-sm font-semibold">
              Закрити форму? Послугу вже створено, але ціни або майстрів не завершено. Закриття втратить незавершені кроки, а нова «Нова послуга» створить дубль.
            </p>
            <div className="flex flex-wrap gap-2">
              <SecondaryButton onClick={onClose}>Так, закрити</SecondaryButton>
              <PrimaryButton onClick={() => setConfirmClose(false)}>Повернутися до форми</PrimaryButton>
            </div>
          </div>
        ) : null}
        <div className="flex flex-wrap gap-2">
          <PrimaryButton type="submit" aria-disabled={create.isPending || undefined} className="aria-disabled:cursor-not-allowed aria-disabled:opacity-60">
            {create.isPending ? "Зберігаємо…" : created ? "Повторити" : "Створити послугу"}
          </PrimaryButton>
          <SecondaryButton onClick={created ? () => setConfirmClose(true) : onClose}>{created ? "Закрити" : "Скасувати"}</SecondaryButton>
        </div>
      </form>
    </Card>
  );
}
