"use client";

import { useEffect, useId, useMemo, useRef, useState, type FormEvent } from "react";
import { BeautyApiError, humanizeError } from "@/features/beauty-auth/errors";
import { MAX_CLOSURE_REASON, validateClosure } from "../closed-days";
import { addDaysIso, dateTimeLabel, todayIso } from "../format";
import { useAddClosure, useClosures, useDeleteClosure, useSetClosedWeekdays } from "../hooks/use-beauty-admin";
import type { BeautyLocation, ClosedDayConflict, ClosureInput, LocationClosure, Weekday } from "../types";
import { WEEKDAYS } from "../working-hours";
import { CheckRow, ErrorBanner, SuccessBanner } from "./staff-parts";
import { FIELD_INPUT, FIELD_LABEL, PrimaryButton, SecondaryButton } from "./ui";

/** Бекенд просить явного підтвердження: на нові вихідні є активні записи (§17). */
export const isConfirmNeeded = (e: unknown): e is BeautyApiError =>
  e instanceof BeautyApiError && e.status === 409 && e.code === "has_appointments_on_closed_days";

const fullDate = (ymd: string) =>
  new Date(`${ymd}T00:00:00Z`).toLocaleDateString("uk-UA", { day: "numeric", month: "short", year: "numeric", timeZone: "UTC" });
export const closureRangeLabel = (c: Pick<LocationClosure, "dateFrom" | "dateTo">) =>
  c.dateFrom === c.dateTo ? fullDate(c.dateFrom) : `${fullDate(c.dateFrom)} – ${fullDate(c.dateTo)}`;

/**
 * Діалог підтвердження: список активних записів на днях, що стають вихідними. Записи НЕ скасовуються автоматично -
 * адміністратор переносить їх сам. Фокус переходить на заголовок, щоб скрінрідер зачитав діалог.
 */
export function ClosedDaysConflictDialog({
  conflicts,
  timezone,
  busy,
  onConfirm,
  onCancel,
}: {
  conflicts: ClosedDayConflict[];
  timezone?: string;
  busy?: boolean;
  onConfirm: () => void;
  onCancel: () => void;
}) {
  const id = useId();
  const heading = useRef<HTMLHeadingElement>(null);
  useEffect(() => heading.current?.focus(), []);
  return (
    <div
      role="alertdialog"
      aria-labelledby={`${id}-title`}
      aria-describedby={`${id}-desc`}
      className="flex flex-col gap-3 rounded-xl bg-[#FFF1CC] p-4 text-[#5C3900]"
    >
      <h4 id={`${id}-title`} ref={heading} tabIndex={-1} className="text-base font-semibold outline-none">
        На ці дні є активні записи ({conflicts.length})
      </h4>
      <p id={`${id}-desc`} className="text-sm">
        Записи не скасовуються автоматично: після збереження їх потрібно перенести або скасувати вручну. Нові слоти й
        онлайн-запис на ці дні вимкнуться одразу.
      </p>
      {conflicts.length > 0 ? (
        <ul className="m-0 flex max-h-64 list-none flex-col gap-1.5 overflow-y-auto p-0 text-sm" aria-label="Записи на вихідних днях">
          {conflicts.map((c) => (
            <li key={c.appointmentId} className="rounded-lg bg-white/70 px-3 py-2">
              <span className="font-semibold">{dateTimeLabel(c.startsAt, timezone)}</span>
              {" · "}
              {c.serviceName}
              {c.specialistName ? <> · {c.specialistName}</> : null}
            </li>
          ))}
        </ul>
      ) : null}
      <div className="flex flex-wrap gap-2">
        <PrimaryButton disabled={busy} onClick={onConfirm}>
          {busy ? "Зберігаємо…" : "Підтвердити й зберегти вихідний"}
        </PrimaryButton>
        <SecondaryButton disabled={busy} onClick={onCancel}>
          Скасувати
        </SecondaryButton>
      </div>
    </div>
  );
}

type Pending =
  | { kind: "weekdays"; weekdays: Weekday[]; conflicts: ClosedDayConflict[] }
  | { kind: "closure"; input: ClosureInput; conflicts: ClosedDayConflict[] };

const sameSet = (a: Weekday[], b: Weekday[]) => a.length === b.length && a.every((x) => b.includes(x));

/**
 * Блок «Вихідні дні» закладу (§17): перемикачі днів тижня + закриття на дати.
 * `canEdit` - owner/admin; `showReason` - причину закриття бачать лише керівники (specialist читає без неї).
 */
export function ClosedDaysSection({
  location,
  canEdit,
  showReason,
}: {
  location: BeautyLocation;
  canEdit: boolean;
  showReason: boolean;
}) {
  const saved = useMemo(() => location.closedWeekdays ?? [], [location.closedWeekdays]);
  const [draft, setDraft] = useState<Weekday[] | null>(null);
  const current = draft ?? saved;
  const dirty = draft !== null && !sameSet(draft, saved);

  const setWeekdays = useSetClosedWeekdays(location.id);
  const addClosure = useAddClosure(location.id);
  const delClosure = useDeleteClosure(location.id);

  const [pending, setPending] = useState<Pending | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const range = useMemo(() => {
    const from = todayIso();
    return { from, to: addDaysIso(from, 365) };
  }, []);
  const closures = useClosures(location.id, range);

  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const [reason, setReason] = useState("");
  const [formError, setFormError] = useState<string | null>(null);
  const ids = useId();

  const toggle = (day: Weekday, on: boolean) => {
    setNotice(null);
    setError(null);
    setDraft(on ? [...new Set([...current, day])] : current.filter((d) => d !== day));
  };

  async function applyWeekdays(weekdays: Weekday[], confirm: boolean) {
    setError(null);
    setNotice(null);
    try {
      await setWeekdays.mutateAsync({ weekdays, confirm });
      setDraft(null);
      setPending(null);
      setNotice("Вихідні збережено.");
    } catch (e) {
      if (isConfirmNeeded(e)) setPending({ kind: "weekdays", weekdays, conflicts: e.conflicts ?? [] });
      else {
        setPending(null);
        setError(humanizeError(e, "Не вдалося зберегти вихідні. Спробуйте ще раз."));
      }
    }
  }

  async function applyClosure(input: ClosureInput) {
    setError(null);
    setNotice(null);
    try {
      await addClosure.mutateAsync(input);
      setPending(null);
      setFrom("");
      setTo("");
      setReason("");
      setNotice("Закриття додано.");
    } catch (e) {
      if (isConfirmNeeded(e)) setPending({ kind: "closure", input: { ...input, confirm: true }, conflicts: e.conflicts ?? [] });
      else {
        setPending(null);
        setError(humanizeError(e, "Не вдалося додати закриття. Спробуйте ще раз."));
      }
    }
  }

  function submitClosure(e: FormEvent) {
    e.preventDefault();
    const input: ClosureInput = { dateFrom: from, dateTo: to || from, reason: reason.trim() || undefined };
    const problem = validateClosure(input);
    setFormError(problem);
    if (problem) return;
    void applyClosure(input);
  }

  async function remove(c: LocationClosure) {
    setError(null);
    setNotice(null);
    try {
      await delClosure.mutateAsync(c.id);
      setNotice("Закриття видалено.");
    } catch (e) {
      setError(humanizeError(e, "Не вдалося видалити закриття."));
    }
  }

  const busy = setWeekdays.isPending || addClosure.isPending;
  const list = [...(closures.data ?? [])].sort((a, b) => a.dateFrom.localeCompare(b.dateFrom));

  return (
    <section aria-labelledby={`${ids}-h`} className="flex flex-col gap-4 border-t border-(--line) pt-4">
      <h3 id={`${ids}-h`} className="text-sm font-semibold">
        Вихідні дні
      </h3>

      <fieldset className="m-0 flex flex-col gap-1 border-0 p-0" disabled={!canEdit}>
        <legend className="mb-1 text-[13px] font-semibold">Щотижневі вихідні</legend>
        <p className="text-[13px] text-(--muted)">
          Позначені дні заклад не працює: слотів і онлайн-запису немає незалежно від графіків майстрів. Нічого не
          позначено — заклад працює 7 днів.
        </p>
        <div className="grid grid-cols-2 gap-x-4 sm:grid-cols-4">
          {WEEKDAYS.map((d) => (
            <CheckRow
              key={d.key}
              label={d.label}
              checked={current.includes(d.key)}
              disabled={!canEdit}
              onChange={(on) => toggle(d.key, on)}
            />
          ))}
        </div>
        {canEdit ? (
          <div className="flex flex-wrap gap-2 pt-1">
            <PrimaryButton disabled={!dirty || busy} onClick={() => void applyWeekdays(current, false)}>
              {setWeekdays.isPending && !pending ? "Зберігаємо…" : "Зберегти вихідні"}
            </PrimaryButton>
            {dirty ? (
              <SecondaryButton
                onClick={() => {
                  setDraft(null);
                  setPending((p) => (p?.kind === "weekdays" ? null : p));
                }}
              >
                Скасувати зміни
              </SecondaryButton>
            ) : null}
          </div>
        ) : null}
      </fieldset>

      {pending?.kind === "weekdays" ? (
        <ClosedDaysConflictDialog
          conflicts={pending.conflicts}
          timezone={location.timezone}
          busy={setWeekdays.isPending}
          onConfirm={() => void applyWeekdays(pending.weekdays, true)}
          onCancel={() => {
            setPending(null);
            setDraft(null);
          }}
        />
      ) : null}

      <div className="flex flex-col gap-2">
        <h4 className="text-[13px] font-semibold">Закриття на дати</h4>
        {closures.isError ? <ErrorBanner>Не вдалося завантажити закриття.</ErrorBanner> : null}
        {closures.isPending ? (
          <p className="text-sm text-(--muted)">Завантаження…</p>
        ) : list.length === 0 ? (
          <p className="text-sm text-(--muted)">Закриттів на найближчий рік немає.</p>
        ) : (
          <ul className="m-0 flex list-none flex-col gap-2 p-0" aria-label={`Закриття закладу «${location.name}»`}>
            {list.map((c) => (
              <li key={c.id} className="flex flex-wrap items-center justify-between gap-2 rounded-xl bg-(--page) px-3 py-2 text-sm">
                <span className="min-w-0 break-words">
                  <span className="font-semibold">{closureRangeLabel(c)}</span>
                  {showReason && c.reason ? <span className="text-(--muted)"> · {c.reason}</span> : null}
                </span>
                {canEdit ? (
                  <SecondaryButton
                    aria-label={`Видалити закриття ${closureRangeLabel(c)}`}
                    disabled={delClosure.isPending}
                    onClick={() => void remove(c)}
                  >
                    Видалити
                  </SecondaryButton>
                ) : null}
              </li>
            ))}
          </ul>
        )}
      </div>

      {canEdit ? (
        <form onSubmit={submitClosure} noValidate className="flex flex-col gap-3" aria-label="Додати закриття на дати">
          <div className="grid gap-3 sm:grid-cols-2">
            <div className="flex flex-col gap-1.5">
              <label htmlFor={`${ids}-from`} className={FIELD_LABEL}>
                Закрито з<span aria-hidden="true"> *</span>
              </label>
              <input
                id={`${ids}-from`}
                type="date"
                required
                value={from}
                onChange={(e) => {
                  setFrom(e.target.value);
                  if (!to || to < e.target.value) setTo(e.target.value);
                  setFormError(null);
                }}
                className={FIELD_INPUT}
              />
            </div>
            <div className="flex flex-col gap-1.5">
              <label htmlFor={`${ids}-to`} className={FIELD_LABEL}>
                Закрито по (включно)<span aria-hidden="true"> *</span>
              </label>
              <input
                id={`${ids}-to`}
                type="date"
                required
                min={from || undefined}
                value={to}
                onChange={(e) => {
                  setTo(e.target.value);
                  setFormError(null);
                }}
                className={FIELD_INPUT}
              />
            </div>
          </div>
          <div className="flex flex-col gap-1.5">
            <label htmlFor={`${ids}-reason`} className={FIELD_LABEL}>
              Причина (необов’язково)
            </label>
            <input
              id={`${ids}-reason`}
              type="text"
              maxLength={MAX_CLOSURE_REASON}
              autoComplete="off"
              value={reason}
              onChange={(e) => setReason(e.target.value)}
              aria-describedby={`${ids}-reason-count`}
              className={FIELD_INPUT}
            />
            <span id={`${ids}-reason-count`} className="text-[13px] text-(--muted)">
              {reason.length}/{MAX_CLOSURE_REASON}. Причину бачать лише власник і адміністратор.
            </span>
          </div>
          {formError ? <ErrorBanner>{formError}</ErrorBanner> : null}
          <div>
            <PrimaryButton type="submit" disabled={busy}>
              {addClosure.isPending && !pending ? "Додаємо…" : "Додати закриття"}
            </PrimaryButton>
          </div>
        </form>
      ) : null}

      {pending?.kind === "closure" ? (
        <ClosedDaysConflictDialog
          conflicts={pending.conflicts}
          timezone={location.timezone}
          busy={addClosure.isPending}
          onConfirm={() => void applyClosure(pending.input)}
          onCancel={() => setPending(null)}
        />
      ) : null}

      {error ? <ErrorBanner>{error}</ErrorBanner> : null}
      {notice ? <SuccessBanner>{notice}</SuccessBanner> : null}
    </section>
  );
}
