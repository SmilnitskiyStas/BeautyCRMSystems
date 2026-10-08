"use client";

import { useId, useState, type FormEvent } from "react";
import { humanizeError } from "@/features/beauty-auth/errors";
import {
  useAbsences,
  useApproveAbsence,
  useCancelAbsence,
  useCreateAbsence,
  useRejectAbsence,
  useStaff,
} from "../hooks/use-beauty-admin";
import { addDaysIso, dateRangeLabel, todayIso } from "../format";
import type { Absence, AbsenceConflict, AbsenceType, StaffMember } from "../types";
import { ABSENCE_TYPE_LABEL, ConflictsList, ERROR_TEXT, ErrorBanner, StatusBadge, SuccessBanner } from "./staff-parts";
import { Badge, EmptyState, FIELD_INPUT, FIELD_LABEL, PrimaryButton, QueryState, SecondaryButton } from "./ui";

const NOTE_MAX = 500;
const TYPES = Object.keys(ABSENCE_TYPE_LABEL) as AbsenceType[];

interface Outcome {
  status: Absence["status"] | null;
  conflicts: AbsenceConflict[];
}

function AbsenceForm({
  member,
  isManager,
  onDone,
  onCancel,
}: {
  member: StaffMember;
  isManager: boolean;
  onDone: (o: Outcome) => void;
  onCancel: () => void;
}) {
  const create = useCreateAbsence(member.id);
  const [type, setType] = useState<AbsenceType>("vacation");
  const [dateFrom, setDateFrom] = useState(() => todayIso());
  const [dateTo, setDateTo] = useState(() => todayIso());
  const [note, setNote] = useState("");
  const [errors, setErrors] = useState<{ dateFrom?: string; dateTo?: string; note?: string }>({});
  const ids = { type: useId(), from: useId(), to: useId(), note: useId() };

  function submit(e: FormEvent) {
    e.preventDefault();
    const next: typeof errors = {};
    if (!dateFrom) next.dateFrom = "Вкажіть першу дату";
    if (!dateTo) next.dateTo = "Вкажіть останню дату";
    else if (dateFrom && dateTo < dateFrom) next.dateTo = "Остання дата не може бути раніше за першу";
    if (note.length > NOTE_MAX) next.note = `Не більше ${NOTE_MAX} символів`;
    setErrors(next);
    if (Object.keys(next).length > 0) return;
    create.mutate(
      { type, dateFrom, dateTo, note: note.trim() || undefined },
      {
        onSuccess: (r) => {
          onDone({ status: r.absence?.status ?? (isManager ? "approved" : "requested"), conflicts: r.conflicts });
          setNote("");
        },
      },
    );
  }

  return (
    <form onSubmit={submit} noValidate className="flex flex-col gap-4 rounded-xl bg-(--page) p-4" aria-label="Нова відсутність">
      <h3 className="text-base font-semibold">{isManager ? "Додати відсутність" : "Повідомити про відсутність"}</h3>
      <div className="flex flex-col gap-1.5">
        <label htmlFor={ids.type} className={FIELD_LABEL}>
          Тип
        </label>
        <select id={ids.type} value={type} onChange={(e) => setType(e.target.value as AbsenceType)} className={FIELD_INPUT}>
          {TYPES.map((t) => (
            <option key={t} value={t}>
              {ABSENCE_TYPE_LABEL[t]}
            </option>
          ))}
        </select>
      </div>
      <div className="grid gap-4 sm:grid-cols-2">
        <div className="flex flex-col gap-1.5">
          <label htmlFor={ids.from} className={FIELD_LABEL}>
            Перший день
          </label>
          <input
            id={ids.from}
            type="date"
            required
            value={dateFrom}
            onChange={(e) => {
              setDateFrom(e.target.value);
              if (dateTo && e.target.value > dateTo) setDateTo(e.target.value);
            }}
            aria-invalid={errors.dateFrom ? true : undefined}
            aria-describedby={errors.dateFrom ? `${ids.from}-err` : undefined}
            className={FIELD_INPUT}
          />
          {errors.dateFrom ? <p id={`${ids.from}-err`} className={ERROR_TEXT}>{errors.dateFrom}</p> : null}
        </div>
        <div className="flex flex-col gap-1.5">
          <label htmlFor={ids.to} className={FIELD_LABEL}>
            Останній день (включно)
          </label>
          <input
            id={ids.to}
            type="date"
            required
            min={dateFrom || undefined}
            value={dateTo}
            onChange={(e) => setDateTo(e.target.value)}
            aria-invalid={errors.dateTo ? true : undefined}
            aria-describedby={errors.dateTo ? `${ids.to}-err` : undefined}
            className={FIELD_INPUT}
          />
          {errors.dateTo ? <p id={`${ids.to}-err`} className={ERROR_TEXT}>{errors.dateTo}</p> : null}
        </div>
      </div>
      <div className="flex flex-col gap-1.5">
        <label htmlFor={ids.note} className={FIELD_LABEL}>
          Примітка (необов’язково)
        </label>
        <textarea
          id={ids.note}
          rows={3}
          maxLength={NOTE_MAX}
          value={note}
          onChange={(e) => setNote(e.target.value)}
          aria-describedby={`${ids.note}-hint`}
          className="w-full rounded-xl border border-(--line-strong) bg-white px-3.5 py-2.5 text-[15px] text-(--ink)"
        />
        <p id={`${ids.note}-hint`} className="text-[13px] text-(--muted)">
          Примітку бачать лише керівники й автор. Не вказуйте медичні подробиці без потреби. {note.length}/{NOTE_MAX}
        </p>
        {errors.note ? <p className={ERROR_TEXT}>{errors.note}</p> : null}
      </div>
      {create.isError ? <ErrorBanner>{humanizeError(create.error, "Не вдалося зберегти відсутність. Спробуйте ще раз.")}</ErrorBanner> : null}
      <div className="flex flex-wrap gap-2">
        <PrimaryButton type="submit" disabled={create.isPending}>
          {create.isPending ? "Зберігаємо…" : isManager ? "Зберегти відсутність" : "Надіслати запит"}
        </PrimaryButton>
        <SecondaryButton onClick={onCancel}>Закрити форму</SecondaryButton>
      </div>
    </form>
  );
}

function AbsenceRow({ absence, isManager, specialistId }: { absence: Absence; isManager: boolean; specialistId: string }) {
  const approve = useApproveAbsence();
  const reject = useRejectAbsence();
  const cancel = useCancelAbsence();
  const [conflicts, setConflicts] = useState<AbsenceConflict[] | null>(null);
  const pending = approve.isPending || reject.isPending || cancel.isPending;
  const err = approve.error ?? reject.error ?? cancel.error;
  const canCancel = isManager ? absence.status === "requested" || absence.status === "approved" : absence.status === "requested";
  const label = ABSENCE_TYPE_LABEL[absence.type];

  return (
    <li className="flex flex-col gap-2 border-t border-(--line) py-3 first:border-t-0">
      <div className="flex flex-wrap items-center gap-2">
        <span className="text-[15px] font-semibold">{label}</span>
        <span className="text-sm text-(--muted)">{dateRangeLabel(absence.dateFrom, absence.dateTo)}</span>
        <StatusBadge status={absence.status} />
      </div>
      {absence.note ? <p className="text-sm">Примітка: {absence.note}</p> : null}
      <div className="flex flex-wrap gap-2">
        {isManager && absence.status === "requested" ? (
          <>
            <PrimaryButton
              disabled={pending}
              aria-label={`Підтвердити: ${label}, ${dateRangeLabel(absence.dateFrom, absence.dateTo)}`}
              onClick={() => approve.mutate(absence.id, { onSuccess: (r) => setConflicts(r.conflicts) })}
            >
              Підтвердити
            </PrimaryButton>
            <SecondaryButton
              disabled={pending}
              aria-label={`Відхилити: ${label}, ${dateRangeLabel(absence.dateFrom, absence.dateTo)}`}
              onClick={() => reject.mutate(absence.id)}
            >
              Відхилити
            </SecondaryButton>
          </>
        ) : null}
        {canCancel ? (
          <SecondaryButton
            disabled={pending}
            aria-label={`${isManager ? "Скасувати відсутність" : "Скасувати запит"}: ${label}, ${dateRangeLabel(absence.dateFrom, absence.dateTo)}`}
            onClick={() => cancel.mutate(absence.id)}
          >
            {isManager ? "Скасувати" : "Скасувати запит"}
          </SecondaryButton>
        ) : null}
      </div>
      {err ? <ErrorBanner>{humanizeError(err, "Не вдалося виконати дію. Спробуйте ще раз.")}</ErrorBanner> : null}
      {conflicts ? (
        <div role="status">
          <ConflictsList conflicts={conflicts} specialistId={specialistId} />
        </div>
      ) : null}
    </li>
  );
}

/** Відсутність колег: лише тип і дати, без приміток (поле навіть не читаємо). */
function Colleagues({ selfId }: { selfId: string }) {
  const today = todayIso();
  const q = useAbsences({ from: today, to: addDaysIso(today, 90) });
  const staff = useStaff();
  const names = new Map((staff.data ?? []).map((s) => [s.id, s.name]));
  const rows = (q.data ?? []).filter((a) => a.specialistId !== selfId && a.status === "approved");
  return (
    <section className="flex flex-col gap-2" aria-labelledby="colleagues-h">
      <h3 id="colleagues-h" className="text-base font-semibold">
        Відсутність колег
      </h3>
      <QueryState isPending={q.isPending} isError={q.isError} onRetry={() => q.refetch()}>
        {rows.length === 0 ? (
          <EmptyState>Найближчими 90 днями відсутності колег немає.</EmptyState>
        ) : (
          <ul className="m-0 flex list-none flex-col p-0">
            {rows.map((a) => (
              <li key={a.id} className="flex flex-wrap items-center gap-2 border-t border-(--line) py-2.5 first:border-t-0">
                <span className="text-sm font-semibold">{names.get(a.specialistId) ?? "Колега"}</span>
                <Badge tone="neutral">{ABSENCE_TYPE_LABEL[a.type]}</Badge>
                <span className="text-sm text-(--muted)">{dateRangeLabel(a.dateFrom, a.dateTo)}</span>
              </li>
            ))}
          </ul>
        )}
      </QueryState>
    </section>
  );
}

export function AbsencesTab({
  member,
  isManager,
  isSelf,
  formOpen,
  onFormOpenChange,
}: {
  member: StaffMember;
  isManager: boolean;
  isSelf: boolean;
  formOpen: boolean;
  onFormOpenChange: (open: boolean) => void;
}) {
  const today = todayIso();
  const q = useAbsences({ from: addDaysIso(today, -30), to: addDaysIso(today, 330), specialistId: member.id });
  const [outcome, setOutcome] = useState<Outcome | null>(null);
  const canCreate = isManager || isSelf;
  const list = [...(q.data ?? [])].sort((a, b) => b.dateFrom.localeCompare(a.dateFrom));

  return (
    <div className="flex flex-col gap-5">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h2 className="text-lg font-semibold">Відсутність</h2>
        {canCreate && !formOpen ? (
          <PrimaryButton
            aria-expanded={false}
            onClick={() => {
              setOutcome(null);
              onFormOpenChange(true);
            }}
          >
            {isManager ? "Додати відсутність" : "Повідомити про відсутність"}
          </PrimaryButton>
        ) : null}
      </div>

      {canCreate && formOpen ? (
        <AbsenceForm
          member={member}
          isManager={isManager}
          onCancel={() => onFormOpenChange(false)}
          onDone={(o) => {
            setOutcome(o);
            onFormOpenChange(false);
          }}
        />
      ) : null}

      {outcome ? (
        <div className="flex flex-col gap-3">
          <SuccessBanner>
            {outcome.status === "requested"
              ? "Запит надіслано. Керівник підтвердить або відхилить його."
              : "Відсутність збережено. Нові слоти цього працівника на ці дні недоступні."}
          </SuccessBanner>
          {isManager ? <ConflictsList conflicts={outcome.conflicts} specialistId={member.id} /> : null}
        </div>
      ) : null}

      <QueryState isPending={q.isPending} isError={q.isError} onRetry={() => q.refetch()}>
        {list.length === 0 ? (
          <EmptyState>Відсутностей ще немає.</EmptyState>
        ) : (
          <ul className="m-0 flex list-none flex-col p-0" aria-label="Список відсутностей">
            {list.map((a) => (
              <AbsenceRow key={a.id} absence={a} isManager={isManager} specialistId={member.id} />
            ))}
          </ul>
        )}
      </QueryState>

      {isSelf && !isManager ? <Colleagues selfId={member.id} /> : null}
    </div>
  );
}
