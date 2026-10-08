"use client";

import { useSearchParams } from "next/navigation";
import { useId, useState } from "react";
import { useAuth } from "@/features/beauty-auth/components/auth-provider";
import { humanizeError } from "@/features/beauty-auth/errors";
import { useAbsences, useCalendarWeek, useCancelAppointment, useMoveAppointment } from "../hooks/use-beauty-admin";
import { MAX_CANCEL_REASON, cancelledByLabel, cancelledByShort } from "../cancellation";
import { addDaysIso, clock, dateTimeLabel, dayIndex, durationLabel, minutesOfDay, money } from "../format";
import type { Absence, Appointment, CalendarKind } from "../types";
import { ABSENCE_TYPE_LABEL, CheckRow } from "./staff-parts";
import { Badge, Card, Chip, FIELD_INPUT, FIELD_LABEL, PageHeader, PrimaryButton, QueryState, SecondaryButton, TextLink } from "./ui";

const START_HOUR = 9;
const END_HOUR = 20;
/** Висота години в px. Висота блоку = тривалість послуги × PX_PER_HOUR. */
const PX_PER_HOUR = 56;
const MIN_BLOCK_PX = 28;
const GRID_HEIGHT = (END_HOUR + 1 - START_HOUR) * PX_PER_HOUR;

const KIND_VIEW: Record<CalendarKind, { label: string; swatch: string; block: string }> = {
  visit: { label: "Звичайний візит", swatch: "bg-[#EADCF2]", block: "bg-[#EADCF2] text-[#3B1A4D]" },
  promo: { label: "Запис по акції", swatch: "bg-[#FDE9DC]", block: "bg-[#FDE9DC] text-[#7A2308]" },
  new: { label: "Новий клієнт", swatch: "bg-[#DDF3E6]", block: "bg-[#DDF3E6] text-[#145A32]" },
  online: { label: "Онлайн-запис клієнта", swatch: "bg-[#E3E9FB]", block: "bg-[#E3E9FB] text-[#223A8C]" },
  break: { label: "Перерва / закрито", swatch: "bg-[#E9E6EF]", block: "bg-[#E9E6EF] text-[#4A4560]" },
};

function blockGeometry(a: Appointment) {
  const top = ((minutesOfDay(a.startsAt) - START_HOUR * 60) / 60) * PX_PER_HOUR + 1;
  const height = Math.max((a.durationMinutes / 60) * PX_PER_HOUR - 2, MIN_BLOCK_PX);
  return { top, height };
}

function timeRange(a: Appointment) {
  const s = minutesOfDay(a.startsAt);
  return `${clock(s)}–${clock(s + a.durationMinutes)}`;
}

function AppointmentPanel({
  appt,
  dayLabel,
  specialistId,
  onCancelled,
  onMoved,
}: {
  appt: Appointment;
  dayLabel: string;
  specialistId: string;
  onCancelled: (refund: number) => void;
  onMoved: () => void;
}) {
  const [confirming, setConfirming] = useState(false);
  const [moving, setMoving] = useState(false);
  const [newStart, setNewStart] = useState(appt.startsAt);
  const cancel = useCancelAppointment(specialistId);
  const move = useMoveAppointment(specialistId);
  const moveInputId = useId();
  const reasonId = useId();
  const [reason, setReason] = useState("");
  const closed = appt.status === "completed" || appt.status === "cancelled" || appt.status === "no_show";
  const terms = appt.cancellation;

  return (
    <>
      <div className="text-base font-semibold">{appt.clientName}</div>
      <div className="text-sm">{appt.serviceName}</div>
      <div className="text-sm text-(--muted)">
        {dayLabel}, {timeRange(appt)} ({durationLabel(appt.durationMinutes)})
      </div>
      <div className="text-sm font-semibold">{money(appt.priceFinal)}</div>
      {appt.status === "cancelled" ? (
        <div className="flex flex-col gap-0.5 rounded-xl bg-(--page) p-3 text-sm" data-testid="cancel-info">
          <span className="font-semibold">{cancelledByLabel(appt.cancelledBy)}</span>
          {appt.cancelledAt ? <span className="text-(--muted)">{dateTimeLabel(appt.cancelledAt)}</span> : null}
          {appt.cancelReason ? <span>Причина: {appt.cancelReason}</span> : null}
        </div>
      ) : null}
      {appt.clientId ? <TextLink href={`/beauty/clients/${appt.clientId}`}>Профіль клієнта</TextLink> : null}
      {appt.kind === "promo" ? <Badge tone="promo" className="self-start">Акція: {appt.promotionName}</Badge> : null}
      {appt.kind === "online" ? <Badge tone="now" className="self-start">Створено клієнтом онлайн</Badge> : null}
      {cancel.isError ? (
        <p role="alert" className="text-sm text-[#8A1F1F]">
          {humanizeError(cancel.error, "Не вдалося скасувати запис. Спробуйте ще раз.")}
        </p>
      ) : null}
      {moving ? (
        <form
          className="flex flex-col gap-2 rounded-xl bg-(--page) p-3"
          onSubmit={(e) => {
            e.preventDefault();
            if (!newStart || newStart === appt.startsAt) return;
            move.mutate({ id: appt.id, startsAt: newStart }, { onSuccess: onMoved });
          }}
        >
          <label htmlFor={moveInputId} className={FIELD_LABEL}>
            Новий час візиту
          </label>
          <input
            id={moveInputId}
            type="datetime-local"
            step={900}
            required
            value={newStart}
            onChange={(e) => setNewStart(e.target.value)}
            className={FIELD_INPUT}
          />
          {move.isError ? (
            <p role="alert" className="text-sm text-[#8A1F1F]">
              {humanizeError(move.error, "Не вдалося перенести запис. Спробуйте ще раз.")}
            </p>
          ) : null}
          <div className="flex flex-wrap gap-2">
            <PrimaryButton type="submit" disabled={move.isPending || !newStart || newStart === appt.startsAt}>
              {move.isPending ? "Переносимо…" : "Зберегти час"}
            </PrimaryButton>
            <SecondaryButton onClick={() => setMoving(false)}>Не переносити</SecondaryButton>
          </div>
        </form>
      ) : null}
      <div className="mt-1.5 flex flex-wrap gap-2">
        {!moving && !confirming && !closed ? (
          <SecondaryButton
            onClick={() => {
              setNewStart(appt.startsAt);
              move.reset();
              setMoving(true);
            }}
          >
            Перенести запис
          </SecondaryButton>
        ) : null}
        {confirming ? (
          <>
            <div className="flex w-full flex-col gap-1.5">
              <label htmlFor={reasonId} className={FIELD_LABEL}>
                Причина (необов’язково)
              </label>
              <textarea
                id={reasonId}
                rows={2}
                maxLength={MAX_CANCEL_REASON}
                value={reason}
                onChange={(e) => setReason(e.target.value)}
                aria-describedby={`${reasonId}-count`}
                className={`${FIELD_INPUT} py-2`}
              />
              <span id={`${reasonId}-count`} className="text-[13px] text-(--muted)">
                {reason.length}/{MAX_CANCEL_REASON}
              </span>
            </div>
            <PrimaryButton
              disabled={cancel.isPending}
              onClick={() => cancel.mutate({ id: appt.id, reason: reason.trim() || undefined }, { onSuccess: (r) => onCancelled(r.refundAmount) })}
            >
              {cancel.isPending ? "Скасовуємо…" : "Так, скасувати"}
            </PrimaryButton>
            <SecondaryButton onClick={() => setConfirming(false)}>Залишити</SecondaryButton>
          </>
        ) : !moving && !closed ? (
          <SecondaryButton onClick={() => setConfirming(true)}>Скасувати запис</SecondaryButton>
        ) : null}
      </div>
      {confirming ? (
        <p className="text-[13px] text-(--muted)">
          {terms
            ? `Якщо до візиту ${terms.windowHours} год або менше, повертається ${terms.refundPercentInWindow}% суми, раніше — ${terms.refundPercentOutside}%${terms.deductFee && terms.feePercent > 0 ? `, з утриманням комісії ${terms.feePercent}%` : ""}.`
            : "Суму повернення розрахує система за політикою скасування."}
        </p>
      ) : null}
    </>
  );
}

export function CalendarScreen() {
  const { user } = useAuth();
  const isSpecialist = user.role === "specialist";
  // Порожній id = «перший майстер»; specialist бачить лише власний календар.
  const fromLink = useSearchParams().get("specialist") ?? "";
  const [chosenId, setChosenId] = useState<string>(isSpecialist ? (user.specialistId ?? "") : fromLink);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [showCancelled, setShowCancelled] = useState(false);
  const week = useCalendarWeek(chosenId, showCancelled);
  const data = week.data;
  const masterId = data?.specialistId ?? chosenId;

  const master = data?.masters.find((m) => m.id === masterId);
  // Відсутність майстра на тиждень: смуга на весь день; текст примітки є лише у відповіді для керівника й автора.
  const weekStart = data?.weekStart ?? "";
  const absences = useAbsences(
    { from: weekStart, to: weekStart ? addDaysIso(weekStart, 6) : "", specialistId: masterId },
    !!weekStart && !!masterId,
  );
  const dayAbsences = (d: number): Absence[] => {
    if (!weekStart) return [];
    const day = addDaysIso(weekStart, d);
    return (absences.data ?? []).filter(
      (a) => a.specialistId === masterId && (a.status === "approved" || a.status === "requested") && a.dateFrom <= day && a.dateTo >= day,
    );
  };
  const real = data?.appointments.filter((a) => a.kind !== "break" && a.status !== "cancelled") ?? [];
  const bookedMinutes = real.reduce((s, a) => s + a.durationMinutes, 0);
  const selected = data?.appointments.find((a) => a.id === selectedId && a.kind !== "break") ?? null;

  const hours: number[] = [];
  for (let h = START_HOUR; h <= END_HOUR; h++) hours.push(h);

  return (
    <>
      <PageHeader
        title="Календар майстра"
        subtitle={
          <>
            {data?.weekLabel ?? "Тиждень"} · {master ? `${master.name}, ${master.locationName}` : "…"}
            {isSpecialist ? null : (
              <>
                {" · "}
                <TextLink href={`/beauty/staff/${masterId}`}>Профіль майстра</TextLink>
              </>
            )}
          </>
        }
        actions={
          isSpecialist ? null : (
          <div className="flex flex-wrap items-center gap-2" role="group" aria-label="Майстер">
            {data?.masters.map((m) => (
              <Chip
                key={m.id}
                pressed={m.id === masterId}
                onClick={() => {
                  setChosenId(m.id);
                  setSelectedId(null);
                }}
              >
                {m.name} · {m.locationName}
                {m.isActive ? "" : " · неактивний"}
              </Chip>
            ))}
          </div>
          )
        }
      />

      <ul className="flex list-none flex-wrap gap-4 p-0 text-[13px] text-(--muted)" aria-label="Легенда">
        {(Object.keys(KIND_VIEW) as CalendarKind[]).map((k) => (
          <li key={k} className="flex items-center gap-1.5">
            <span className={`size-3.5 rounded ${KIND_VIEW[k].swatch}`} aria-hidden="true" />
            {KIND_VIEW[k].label}
          </li>
        ))}
      </ul>

      <div className="flex flex-wrap items-center gap-x-6 gap-y-1">
        <CheckRow label="Показати скасовані" checked={showCancelled} onChange={setShowCancelled} />
      </div>

      {master && !master.isActive ? (
        <p role="status" className="rounded-xl bg-[#FFF1CC] px-3.5 py-2.5 text-sm text-[#5C3900]">
          <strong>{master.name} — неактивний.</strong>{" "}
          {master.toMoveCount > 0 ? `Є записи, їх потрібно перенести (${master.toMoveCount}).` : "Нових записів немає."}
        </p>
      ) : null}

      <QueryState isPending={week.isPending} isError={week.isError} onRetry={() => week.refetch()}>
        {data ? (
          <div className="flex flex-wrap items-start gap-6">
            <Card className="flex-[999_1_640px] overflow-x-auto p-4">
              <div className="min-w-[900px]">
                <div className="grid grid-cols-[56px_repeat(7,minmax(0,1fr))]">
                  <div />
                  {data.days.map((d, i) => (
                    <div key={d} className={`px-1.5 pt-2 pb-3 text-center text-sm font-semibold ${i === 0 ? "text-(--accent)" : ""}`}>
                      {d}
                    </div>
                  ))}
                </div>
                <div className="grid grid-cols-[56px_repeat(7,minmax(0,1fr))]">
                  <div className="relative" style={{ height: GRID_HEIGHT }} aria-hidden="true">
                    {hours.map((h) => (
                      <div key={h} className="absolute left-0 -translate-y-2 text-xs text-(--muted)" style={{ top: (h - START_HOUR) * PX_PER_HOUR }}>
                        {h}:00
                      </div>
                    ))}
                  </div>
                  {data.days.map((day, d) => (
                    <div
                      key={day}
                      className="relative border-l border-(--line)"
                      style={{
                        height: GRID_HEIGHT,
                        backgroundImage: `repeating-linear-gradient(to bottom, transparent 0, transparent ${PX_PER_HOUR - 1}px, #ECE8F2 ${PX_PER_HOUR - 1}px, #ECE8F2 ${PX_PER_HOUR}px)`,
                      }}
                      role="group"
                      aria-label={dayAbsences(d).length ? `${day}. ${dayAbsences(d).map((a) => `${a.status === "requested" ? "Запит: " : ""}${ABSENCE_TYPE_LABEL[a.type]}`).join(", ")}${dayAbsences(d).some((a) => a.status === "approved") ? ", слоти недоступні" : ""}` : day}
                    >
                      {dayAbsences(d).map((a) => {
                        const pending = a.status === "requested";
                        return (
                          <div
                            key={a.id}
                            data-absence={a.status}
                            className={`absolute inset-0 overflow-hidden px-1.5 py-1 text-[11px] leading-tight font-semibold text-[#4A4560] ${
                              pending ? "border-2 border-dashed border-[#6F6985] bg-[#F1EEF6]/80" : "bg-[#E9E6EF]"
                            }`}
                            style={{
                              backgroundImage: pending
                                ? undefined
                                : "repeating-linear-gradient(135deg, transparent 0 8px, rgba(74,69,96,0.12) 8px 10px)",
                            }}
                          >
                            <span className="block">
                              {pending ? "Запит: " : ""}
                              {ABSENCE_TYPE_LABEL[a.type]}
                            </span>
                            <span className="block font-normal">{pending ? "очікує підтвердження" : "слоти недоступні"}</span>
                            {a.note ? <span className="mt-0.5 block font-normal">{a.note}</span> : null}
                          </div>
                        );
                      })}
                      {data.appointments
                        .filter((a) => dayIndex(a.startsAt, data.weekStart) === d)
                        .map((a) => {
                          const { top, height } = blockGeometry(a);
                          const gone = a.status === "cancelled";
                          const base = `absolute right-1 left-1 box-border overflow-hidden rounded-lg px-2 py-0.5 text-left text-[11px] leading-tight ${gone ? "bg-[#F4F2F8] text-[#4A4560]" : KIND_VIEW[a.kind].block}`;
                          if (a.kind === "break") {
                            return (
                              <div key={a.id} className={`${base} border-2 border-transparent`} style={{ top, height }}>
                                <span className="block font-semibold">{a.clientName}</span>
                              </div>
                            );
                          }
                          const on = a.id === selectedId;
                          return (
                            <button
                              key={a.id}
                              type="button"
                              aria-pressed={on}
                              aria-label={`${a.clientName}, ${a.serviceName}, ${timeRange(a)}${gone ? `, скасовано. ${cancelledByLabel(a.cancelledBy)}` : ""}`}
                              onClick={() => {
                                setSelectedId(a.id);
                                setNotice(null);
                              }}
                              data-cancelled={gone ? "true" : undefined}
                              className={`${base} cursor-pointer border-2 ${gone ? "border-dashed" : ""} ${on ? "border-(--accent)" : gone ? "border-[#6F6985]" : "border-transparent"}`}
                              style={{ top, height }}
                            >
                              <span className={`block font-semibold ${gone ? "line-through" : ""}`}>{a.clientName}</span>
                              <span className="block">
                                {timeRange(a)} · {durationLabel(a.durationMinutes)}
                              </span>
                              <span className="block truncate">{gone ? cancelledByShort(a.cancelledBy) : a.serviceName}</span>
                            </button>
                          );
                        })}
                    </div>
                  ))}
                </div>
              </div>
            </Card>

            <aside className="flex min-w-0 flex-[1_1_300px] flex-col gap-4">
              <Card as="div" className="flex flex-col gap-2.5">
                <h2 className="text-lg font-semibold">Запис</h2>
                {notice ? (
                  <p role="status" className="rounded-xl bg-[#DDF3E6] px-3 py-2 text-sm text-[#145A32]">
                    {notice}
                  </p>
                ) : null}
                {selected ? (
                  <AppointmentPanel
                    key={selected.id}
                    appt={selected}
                    dayLabel={data.days[dayIndex(selected.startsAt, data.weekStart)] ?? ""}
                    specialistId={masterId}
                    onMoved={() => {
                      setSelectedId(null);
                      setNotice("Запис перенесено.");
                    }}
                    onCancelled={(refund) => {
                      setSelectedId(null);
                      setNotice(`Запис скасовано. Сума повернення: ${money(refund)}.`);
                    }}
                  />
                ) : (
                  <div className="text-sm text-(--muted)">Оберіть запис у календарі, щоб побачити деталі.</div>
                )}
              </Card>
              <Card as="div" className="flex flex-col gap-2">
                <h2 className="text-lg font-semibold">Тиждень майстра</h2>
                <div className="flex justify-between text-sm"><span className="text-(--muted)">Записів</span><span className="font-semibold">{real.length}</span></div>
                <div className="flex justify-between text-sm"><span className="text-(--muted)">З них по акції</span><span className="font-semibold">{real.filter((a) => a.kind === "promo").length}</span></div>
                <div className="flex justify-between text-sm"><span className="text-(--muted)">Завантаження</span><span className="font-semibold">{Math.round((bookedMinutes / 60 / (6 * 11)) * 100)}%</span></div>
              </Card>
            </aside>
          </div>
        ) : null}
      </QueryState>
    </>
  );
}
