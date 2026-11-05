"use client";

import { useSearchParams } from "next/navigation";
import { useEffect, useId, useRef, useState } from "react";
import { useAuth } from "@/features/beauty-auth/components/auth-provider";
import { BeautyApiError, humanizeError } from "@/features/beauty-auth/errors";
import { useAbsences, useCalendarWeek, useCancelAppointment, useLocations, useMoveAppointment, useSetAppointmentStatus } from "../hooks/use-beauty-admin";
import { canSeeClosureReason, closedDayLabel } from "../closed-days";
import { CLIENT_TG_CANCEL_EVENT, MAX_CANCEL_REASON, cancelledByLabel, cancelledByShort, isClientTelegramCancel } from "../cancellation";
import { ConfirmedBadge } from "./client-confirmed-badge";
import { CouponBadge } from "./coupon-badge";
import { addDaysIso, clock, dateTimeLabel, dayIndex, durationLabel, minutesOfDay, money } from "../format";
import { bookableAt } from "../../beauty-shared/availability";
import { instantToLocal } from "../../beauty-shared/zone-time";
import type { Absence, Appointment, CalendarKind, DayAvailability, MinuteInterval, PatchableStatus } from "../types";
import { ReminderPoints } from "./reminder-points";
import { ABSENCE_TYPE_LABEL, CheckRow } from "./staff-parts";
import { Badge, Card, Chip, FIELD_INPUT, FIELD_LABEL, PageHeader, PrimaryButton, QueryState, SecondaryButton, STATUS_VIEW, TextLink } from "./ui";

/** Діапазон сітки за замовчуванням (години доби [start, end)); реальний приходить із `gridStartHour/gridEndHour` (F1). */
const DEFAULT_START_HOUR = 9;
const DEFAULT_END_HOUR = 21;
/** Висота години в px. Висота блоку = тривалість послуги × PX_PER_HOUR. */
const PX_PER_HOUR = 56;
const MIN_BLOCK_PX = 28;
/** Вихідний закладу: діагональний штрих + тло. */
const CLOSED_STRIPES = "repeating-linear-gradient(135deg, transparent 0 8px, rgba(63,58,85,0.14) 8px 10px)";
/** Відсутність майстра: діагональний штрих. */
const ABSENCE_STRIPES = "repeating-linear-gradient(135deg, transparent 0 8px, rgba(74,69,96,0.12) 8px 10px)";
/** Майстер не працює (поза графіком): крапковий візерунок на світлому тлі. */
const OFF_DOTS = "radial-gradient(rgba(74,69,96,0.32) 1px, transparent 1.2px)";
/** Поза годинами закладу: горизонтальні лінії на темнішому тлі. */
const HOURS_LINES = "repeating-linear-gradient(0deg, transparent 0 5px, rgba(63,58,85,0.2) 5px 6px)";

const KIND_VIEW: Record<CalendarKind, { label: string; swatch: string; block: string }> = {
  visit: { label: "Звичайний візит", swatch: "bg-[#EADCF2]", block: "bg-[#EADCF2] text-[#3B1A4D]" },
  promo: { label: "Запис по акції", swatch: "bg-[#FDE9DC]", block: "bg-[#FDE9DC] text-[#7A2308]" },
  new: { label: "Новий клієнт", swatch: "bg-[#DDF3E6]", block: "bg-[#DDF3E6] text-[#145A32]" },
  online: { label: "Онлайн-запис клієнта", swatch: "bg-[#E3E9FB]", block: "bg-[#E3E9FB] text-[#223A8C]" },
  break: { label: "Перерва / закрито", swatch: "bg-[#E9E6EF]", block: "bg-[#E9E6EF] text-[#4A4560]" },
};

function blockGeometry(a: Appointment, startHour: number) {
  const top = ((minutesOfDay(a.startsAt) - startHour * 60) / 60) * PX_PER_HOUR + 1;
  const height = Math.max((a.durationMinutes / 60) * PX_PER_HOUR - 2, MIN_BLOCK_PX);
  return { top, height };
}

function timeRange(a: Appointment) {
  const s = minutesOfDay(a.startsAt);
  return `${clock(s)}–${clock(s + a.durationMinutes)}`;
}

const rangeLabel = (i: MinuteInterval) => `${clock(i.from)}–${clock(i.to)}`;

/**
 * Затінення недоступного часу в колонці дня (F1): «Заклад не працює» / «Не працює» на весь день або сегменти
 * «Заклад зачинено» (поза годинами закладу) і «Не працює» (поза графіком майстра). Кліків не перехоплює.
 */
function AvailabilityShade({ day, startHour, endHour }: { day: DayAvailability; startHour: number; endHour: number }) {
  if (day.state === "location_hours_off") {
    return (
      <div
        data-testid="hours-day-off"
        className="pointer-events-none absolute inset-0 overflow-hidden bg-[#D9D3E6] px-1.5 py-1 text-[11px] leading-tight font-semibold text-[#3F3A55]"
        style={{ backgroundImage: HOURS_LINES }}
      >
        <span className="block">Заклад не працює</span>
        <span className="block font-normal">поза годинами закладу</span>
      </div>
    );
  }
  if (day.state === "master_off") {
    return (
      <div
        data-testid="master-off"
        className="pointer-events-none absolute inset-0 overflow-hidden bg-[#F3F0F8] px-1.5 py-1 text-[11px] leading-tight font-semibold text-[#4A4560]"
        style={{ backgroundImage: OFF_DOTS, backgroundSize: "7px 7px" }}
      >
        <span className="block">Не працює</span>
        <span className="block font-normal">вихідний за графіком</span>
      </div>
    );
  }
  if (day.state !== "open") return null;
  const lo = startHour * 60;
  const hi = endHour * 60;
  const seg = (list: MinuteInterval[]) =>
    list
      .map((i) => ({ from: Math.max(i.from, lo), to: Math.min(i.to, hi) }))
      .filter((i) => i.to > i.from)
      .map((i) => ({ ...i, top: ((i.from - lo) / 60) * PX_PER_HOUR, height: ((i.to - i.from) / 60) * PX_PER_HOUR }));
  return (
    <>
      {seg(day.hoursOff).map((i) => (
        <div
          key={`h${i.from}`}
          data-testid="shade-hours"
          className="pointer-events-none absolute right-0 left-0 overflow-hidden bg-[#D9D3E6] px-1.5 py-0.5 text-[11px] leading-tight font-semibold text-[#3F3A55]"
          style={{ top: i.top, height: i.height, backgroundImage: HOURS_LINES }}
        >
          {i.height >= 30 ? "Заклад зачинено" : null}
        </div>
      ))}
      {seg(day.masterOff).map((i) => (
        <div
          key={`m${i.from}`}
          data-testid="shade-master"
          className="pointer-events-none absolute right-0 left-0 overflow-hidden bg-[#F3F0F8] px-1.5 py-0.5 text-[11px] leading-tight font-semibold text-[#4A4560]"
          style={{ top: i.top, height: i.height, backgroundImage: OFF_DOTS, backgroundSize: "7px 7px" }}
        >
          {i.height >= 30 ? "Не працює" : null}
        </div>
      ))}
    </>
  );
}

/** Тексти результатів зміни статусу запису (§9, §24.4). */
const STATUS_NOTICE: Record<PatchableStatus, string> = {
  confirmed: "Запис підтверджено.",
  completed: "Візит завершено. Ціну й собівартість зафіксовано у звіті.",
  no_show: "Запис позначено як «Не прийшов».",
};
const ASK_TITLE: Record<"completed" | "no_show", string> = {
  completed: "Завершити візит?",
  no_show: "Позначити «Не прийшов»?",
};

/** Помилка зміни статусу: закритий запис (409 appointment_closed) - дружній текст; решта - за кодом без серверних текстів. */
export function statusErrorText(e: unknown): string {
  if (e instanceof BeautyApiError && e.code === "appointment_closed") {
    return "Запис уже закрито (завершено, скасовано або позначено як «Не прийшов»), тому змінити його не можна. Дані оновлено.";
  }
  return humanizeError(e, "Не вдалося змінити статус запису. Спробуйте ще раз.");
}

function AppointmentPanel({
  appt,
  dayLabel,
  onCancelled,
  onMoved,
  onStatusChanged,
  canChangeStatus,
  moveProblem,
  outOfHours,
}: {
  appt: Appointment;
  dayLabel: string;
  onCancelled: (refund: number) => void;
  onMoved: (newStart: string) => void;
  /** Результат зміни статусу (підтвердження/завершення/«не прийшов») для повідомлення над панеллю. */
  onStatusChanged: (message: string) => void;
  /** owner/admin - будь-який запис, specialist - лише власний (інакше backend віддасть 404). */
  canChangeStatus: boolean;
  /** Чому новий час недоступний (F1: у недоступні клітинки записи не переносяться) або `null`. */
  moveProblem?: (startsAt: string, durationMinutes: number) => string | null;
  /** Поточний запис припадає на недоступний час (залишається видимим із попередженням). */
  outOfHours?: boolean;
}) {
  const [confirming, setConfirming] = useState(false);
  const [moving, setMoving] = useState(false);
  const [newStart, setNewStart] = useState(appt.startsAt);
  const cancel = useCancelAppointment();
  const move = useMoveAppointment();
  const statusMutation = useSetAppointmentStatus();
  const [ask, setAsk] = useState<"completed" | "no_show" | null>(null);
  const askRef = useRef<HTMLDivElement>(null);
  const askTitleId = useId();
  const moveInputId = useId();
  const reasonId = useId();
  const zone = useLocations().data?.find((l) => l.id === appt.locationId)?.timezone;
  const [reason, setReason] = useState("");
  const cancelledByTelegram = isClientTelegramCancel(appt);
  const closed = appt.status === "completed" || appt.status === "cancelled" || appt.status === "no_show";
  const terms = appt.cancellation;
  // Візит у майбутньому (за часом закладу) завершити можна (backend не забороняє), але з попередженням.
  const notStarted = appt.startsAt > instantToLocal(new Date().toISOString(), zone);
  const canAct = canChangeStatus && !closed && appt.kind !== "break";
  const showStatusActions = canAct && !moving && !confirming && ask === null;
  useEffect(() => {
    if (ask) askRef.current?.focus();
  }, [ask]);
  const change = (status: PatchableStatus) => {
    statusMutation.reset();
    statusMutation.mutate(
      { id: appt.id, status },
      {
        onSuccess: () => {
          setAsk(null);
          onStatusChanged(STATUS_NOTICE[status]);
        },
        onError: () => setAsk(null),
      },
    );
  };
  const problem = moving && newStart && newStart !== appt.startsAt ? (moveProblem?.(newStart, appt.durationMinutes) ?? null) : null;

  return (
    <>
      <div className="text-base font-semibold">{appt.clientName}</div>
      <div className="text-sm">{appt.serviceName}</div>
      <div className="text-sm text-(--muted)">
        {dayLabel}, {timeRange(appt)} ({durationLabel(appt.durationMinutes)})
      </div>
      <div className="text-sm font-semibold">{money(appt.priceFinal)}</div>
      <Badge tone={STATUS_VIEW[appt.status].tone} className="self-start">
        <span data-testid="appt-status">{STATUS_VIEW[appt.status].label}</span>
      </Badge>
      {appt.status === "cancelled" ? (
        <div className="flex flex-col gap-0.5 rounded-xl bg-(--page) p-3 text-sm" data-testid="cancel-info">
          <span className="font-semibold">{cancelledByTelegram ? CLIENT_TG_CANCEL_EVENT : cancelledByLabel(appt.cancelledBy)}</span>
          {appt.cancelledAt ? <span className="text-(--muted)">{dateTimeLabel(appt.cancelledAt)}</span> : null}
          {appt.cancelReason && !cancelledByTelegram ? <span>Причина: {appt.cancelReason}</span> : null}
        </div>
      ) : null}
      {appt.clientConfirmedAt && appt.status !== "cancelled" ? <ConfirmedBadge at={appt.clientConfirmedAt} timezone={zone} /> : null}
      {outOfHours ? (
        <p role="status" data-testid="out-of-hours-note" className="rounded-xl bg-[#FFF1CC] px-3 py-2 text-[13px] text-[#5C3900]">
          Запис припадає на час, коли майстер або заклад недоступні (поза графіком, годинами закладу, вихідним чи відсутністю). Його можна перенести.
        </p>
      ) : null}
      {appt.reminders && appt.status !== "cancelled" ? <ReminderPoints reminders={appt.reminders} timezone={zone} /> : null}
      {appt.clientId ? <TextLink href={`/beauty/clients/${appt.clientId}`}>Профіль клієнта</TextLink> : null}
      {appt.kind === "promo" ? <Badge tone="promo" className="self-start">Акція: {appt.promotionName}</Badge> : null}
      <CouponBadge appt={appt} />
      {appt.kind === "online" ? <Badge tone="now" className="self-start">Створено клієнтом онлайн</Badge> : null}
      {cancel.isError ? (
        <p role="alert" className="text-sm text-[#8A1F1F]">
          {humanizeError(cancel.error, "Не вдалося скасувати запис. Спробуйте ще раз.")}
        </p>
      ) : null}
      {statusMutation.isError ? (
        <p role="alert" data-testid="status-error" className="text-sm text-[#8A1F1F]">
          {statusErrorText(statusMutation.error)}
        </p>
      ) : null}
      {moving ? (
        <form
          className="flex flex-col gap-2 rounded-xl bg-(--page) p-3"
          onSubmit={(e) => {
            e.preventDefault();
            if (!newStart || newStart === appt.startsAt || problem) return;
            move.mutate({ id: appt.id, startsAt: newStart }, { onSuccess: () => onMoved(newStart) });
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
          {problem ? (
            <p role="alert" data-testid="move-problem" className="text-sm text-[#8A1F1F]">
              {problem}
            </p>
          ) : null}
          {move.isError ? (
            <p role="alert" className="text-sm text-[#8A1F1F]">
              {humanizeError(move.error, "Не вдалося перенести запис. Спробуйте ще раз.")}
            </p>
          ) : null}
          <div className="flex flex-wrap gap-2">
            <PrimaryButton type="submit" disabled={move.isPending || !newStart || newStart === appt.startsAt || !!problem}>
              {move.isPending ? "Переносимо…" : "Зберегти час"}
            </PrimaryButton>
            <SecondaryButton onClick={() => setMoving(false)}>Не переносити</SecondaryButton>
          </div>
        </form>
      ) : null}
      {ask ? (
        <div
          ref={askRef}
          tabIndex={-1}
          role="group"
          aria-labelledby={askTitleId}
          data-testid="status-confirm"
          className="flex flex-col gap-2 rounded-xl bg-(--page) p-3 outline-none focus-visible:ring-2 focus-visible:ring-(--accent)"
        >
          <p id={askTitleId} className="text-sm font-semibold">
            {ASK_TITLE[ask]}
          </p>
          {ask === "completed" ? (
            <>
              <p className="text-sm">
                Цим фіксується ціна візиту ({money(appt.priceFinal)}) і собівартість послуги на сьогодні: вони потраплять у звіт і більше не
                зміняться. Після завершення запис не можна перенести чи скасувати.
              </p>
              {notStarted ? (
                <p role="status" data-testid="complete-future-warning" className="rounded-xl bg-[#FFF1CC] px-3 py-2 text-[13px] text-[#5C3900]">
                  Візит призначено на {dateTimeLabel(appt.startsAt)} і він ще не настав. Завершуйте його наперед, лише якщо послугу вже надано.
                </p>
              ) : null}
            </>
          ) : (
            <p className="text-sm">Клієнт не з’явився на візит. Запис буде закрито, і змінити, перенести чи скасувати його вже не можна.</p>
          )}
          <div className="flex flex-wrap gap-2">
            <PrimaryButton disabled={statusMutation.isPending} onClick={() => change(ask)}>
              {statusMutation.isPending ? "Зберігаємо…" : ask === "completed" ? "Так, завершити" : "Так, не прийшов"}
            </PrimaryButton>
            <SecondaryButton disabled={statusMutation.isPending} onClick={() => setAsk(null)}>
              Назад
            </SecondaryButton>
          </div>
        </div>
      ) : null}
      <div className="mt-1.5 flex flex-wrap gap-2">
        {showStatusActions && appt.status === "pending" ? (
          <PrimaryButton disabled={statusMutation.isPending} onClick={() => change("confirmed")}>
            {statusMutation.isPending ? "Підтверджуємо…" : "Підтвердити запис"}
          </PrimaryButton>
        ) : null}
        {showStatusActions ? (
          <>
            <PrimaryButton
              onClick={() => {
                statusMutation.reset();
                setAsk("completed");
              }}
            >
              Завершити візит
            </PrimaryButton>
            <SecondaryButton
              onClick={() => {
                statusMutation.reset();
                setAsk("no_show");
              }}
            >
              Не прийшов
            </SecondaryButton>
          </>
        ) : null}
        {!moving && !confirming && ask === null && !closed ? (
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
        ) : !moving && ask === null && !closed ? (
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

function availabilityAria(day: string, a: DayAvailability): string {
  if (a.state === "location_hours_off") return `${day}. Заклад не працює, слоти недоступні`;
  if (a.state === "master_off") return `${day}. Майстер не працює, слоти недоступні`;
  if (a.state === "open" && a.free.length > 0) return `${day}. Доступно: ${a.free.map(rangeLabel).join(", ")}`;
  return day;
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
  // Заклад, обраний у календарі (майстер може працювати в кількох): порожній = перший активний заклад майстра.
  const [chosenLoc, setChosenLoc] = useState("");
  const week = useCalendarWeek(chosenId, showCancelled, chosenLoc);
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
  // Закритий день закладу (§17): причину закриття бачать лише керівники.
  const showReason = canSeeClosureReason(user.role);
  const closedDay = (d: number) => data?.closedDays?.[d] ?? null;
  const avail = data?.availability;
  // Затверджена відсутність блокує день (запит `requested` ні, §13).
  const absentDay = (d: number) => dayAbsences(d).some((a) => a.status === "approved");
  const dayBlocked = (d: number) => !!closedDay(d) || absentDay(d);
  const real = data?.appointments.filter((a) => a.kind !== "break" && a.status !== "cancelled") ?? [];
  const bookedMinutes = real.reduce((s, a) => s + a.durationMinutes, 0);
  const selected = data?.appointments.find((a) => a.id === selectedId && a.kind !== "break") ?? null;

  // Запис у недоступній клітинці лишається видимим із попередженням (F1).
  const outOfHours = (a: Appointment) => {
    if (a.kind === "break" || a.status === "cancelled" || !data) return false;
    const d = dayIndex(a.startsAt, data.weekStart);
    if (d < 0 || d > 6) return false;
    if (dayBlocked(d)) return true;
    return avail ? !bookableAt(avail[d], minutesOfDay(a.startsAt), a.durationMinutes) : false;
  };
  const moveProblem = (startsAt: string, durationMinutes: number): string | null => {
    if (!data || !avail) return null;
    const d = dayIndex(startsAt, data.weekStart);
    if (d < 0 || d > 6) return null; // інший тиждень: перевіряє backend
    if (closedDay(d)) return "У цей день заклад не працює.";
    if (absentDay(d)) return "У цей день майстер відсутній.";
    if (!bookableAt(avail[d], minutesOfDay(startsAt), durationMinutes)) return "Цей час поза графіком майстра або годинами закладу.";
    return null;
  };
  // Місткість тижня - з доступних хвилин (графік ∩ години ∩ вихідні ∩ відсутності), не з фіксованих 6×11 годин.
  const capacityMinutes = avail
    ? avail.reduce((sum, day, d) => (dayBlocked(d) ? sum : sum + day.free.reduce((x, f) => x + (f.to - f.from), 0)), 0)
    : 6 * 11 * 60;
  const loadLabel = capacityMinutes > 0 ? `${Math.min(100, Math.round((bookedMinutes / capacityMinutes) * 100))}%` : "—";

  // Діапазон сітки: за графіком/годинами закладу, розширений так, щоб жоден запис не вийшов за межі колонки.
  let startHour = data?.gridStartHour ?? DEFAULT_START_HOUR;
  let endHour = data?.gridEndHour ?? DEFAULT_END_HOUR;
  for (const a of data?.appointments ?? []) {
    startHour = Math.min(startHour, Math.floor(minutesOfDay(a.startsAt) / 60));
    endHour = Math.max(endHour, Math.min(24, Math.ceil((minutesOfDay(a.startsAt) + a.durationMinutes) / 60)));
  }
  const gridHeight = (endHour - startHour) * PX_PER_HOUR;
  const hours: number[] = [];
  for (let h = startHour; h < endHour; h++) hours.push(h);
  const chosenLocationName = data?.locations?.find((l) => l.id === data.locationId)?.name ?? master?.locationName;

  return (
    <>
      <PageHeader
        title="Календар майстра"
        subtitle={
          <>
            {data?.weekLabel ?? "Тиждень"} · {master ? `${master.name}, ${chosenLocationName}` : "…"}
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
                  setChosenLoc("");
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
        <li className="flex items-center gap-1.5">
          <span className="size-3.5 rounded bg-[#E4E0EC]" style={{ backgroundImage: CLOSED_STRIPES }} aria-hidden="true" />
          Вихідний закладу
        </li>
        <li className="flex items-center gap-1.5">
          <span className="size-3.5 rounded bg-[#E9E6EF]" style={{ backgroundImage: ABSENCE_STRIPES }} aria-hidden="true" />
          Відсутність майстра
        </li>
        <li className="flex items-center gap-1.5">
          <span className="size-3.5 rounded border border-[#CFC9DD] bg-[#F3F0F8]" style={{ backgroundImage: OFF_DOTS, backgroundSize: "5px 5px" }} aria-hidden="true" />
          Не працює (поза графіком майстра)
        </li>
        <li className="flex items-center gap-1.5">
          <span className="size-3.5 rounded bg-[#D9D3E6]" style={{ backgroundImage: HOURS_LINES }} aria-hidden="true" />
          Заклад зачинено (поза годинами закладу)
        </li>
        <li className="flex items-center gap-1.5">
          <span className="size-3.5 rounded border-2 border-[#C77700] bg-white" aria-hidden="true" />
          Запис поза доступним часом
        </li>
      </ul>

      {data?.locations && data.locations.length > 1 ? (
        <div className="flex flex-wrap items-center gap-2" role="group" aria-label="Заклад">
          <span className="text-sm text-(--muted)">Заклад:</span>
          {data.locations.map((l) => (
            <Chip
              key={l.id}
              variant="soft"
              pressed={l.id === data.locationId}
              onClick={() => {
                setChosenLoc(l.id);
                setSelectedId(null);
              }}
            >
              {l.name}
            </Chip>
          ))}
          {data.otherLocationsCount ? (
            <span className="text-[13px] text-(--muted)">Записів в іншому закладі цього тижня: {data.otherLocationsCount}</span>
          ) : null}
        </div>
      ) : null}

      {data?.availabilityWarning ? (
        <p role="status" data-testid="availability-warning" className="rounded-xl bg-[#FFF1CC] px-3.5 py-2.5 text-sm text-[#5C3900]">
          Не вдалося завантажити вихідні та закриття закладу. Календар може показувати закриті дні як робочі.
        </p>
      ) : null}

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
                  <div className="relative" style={{ height: gridHeight }} aria-hidden="true">
                    {hours.map((h) => (
                      <div key={h} className="absolute left-0 -translate-y-2 text-xs text-(--muted)" style={{ top: (h - startHour) * PX_PER_HOUR }}>
                        {h}:00
                      </div>
                    ))}
                  </div>
                  {data.days.map((day, d) => (
                    <div
                      key={day}
                      className="relative border-l border-(--line)"
                      style={{
                        height: gridHeight,
                        backgroundImage: `repeating-linear-gradient(to bottom, transparent 0, transparent ${PX_PER_HOUR - 1}px, #ECE8F2 ${PX_PER_HOUR - 1}px, #ECE8F2 ${PX_PER_HOUR}px)`,
                      }}
                      role="group"
                      data-closed={closedDay(d)?.source}
                      aria-label={
                        closedDay(d)
                          ? `${day}. ${closedDayLabel(closedDay(d)!, showReason)}, слоти недоступні`
                          : dayAbsences(d).length
                            ? `${day}. ${dayAbsences(d).map((a) => `${a.status === "requested" ? "Запит: " : ""}${ABSENCE_TYPE_LABEL[a.type]}`).join(", ")}${dayAbsences(d).some((a) => a.status === "approved") ? ", слоти недоступні" : ""}`
                            : avail
                              ? availabilityAria(day, avail[d])
                              : day
                      }
                    >
                      {closedDay(d) ? (
                        <div
                          data-testid="closed-day"
                          className="pointer-events-none absolute inset-0 overflow-hidden bg-[#E4E0EC] px-1.5 py-1 text-[11px] leading-tight font-semibold text-[#3F3A55]"
                          style={{ backgroundImage: CLOSED_STRIPES }}
                        >
                          <span className="block">Вихідний</span>
                          {showReason && closedDay(d)?.source === "closure" && closedDay(d)?.reason ? (
                            <span className="block font-normal">{closedDay(d)?.reason}</span>
                          ) : null}
                        </div>
                      ) : null}
                      {(closedDay(d) ? [] : dayAbsences(d)).map((a) => {
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
                      {avail && !dayBlocked(d) ? <AvailabilityShade day={avail[d]} startHour={startHour} endHour={endHour} /> : null}
                      {data.appointments
                        .filter((a) => dayIndex(a.startsAt, data.weekStart) === d)
                        .map((a) => {
                          const { top, height } = blockGeometry(a, startHour);
                          const warn = outOfHours(a);
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
                              aria-label={`${a.clientName}, ${a.serviceName}, ${timeRange(a)}${gone ? `, скасовано. ${cancelledByLabel(a.cancelledBy)}` : ""}${a.clientConfirmedAt && !gone ? ", клієнт підтвердив візит" : ""}${warn ? ", поза доступним часом" : ""}`}
                              onClick={() => {
                                setSelectedId(a.id);
                                setNotice(null);
                              }}
                              data-cancelled={gone ? "true" : undefined}
                              data-out-of-hours={warn ? "true" : undefined}
                              className={`${base} cursor-pointer border-2 ${gone ? "border-dashed" : ""} ${on ? "border-(--accent)" : gone ? "border-[#6F6985]" : warn ? "border-[#C77700]" : "border-transparent"}`}
                              style={{ top, height }}
                            >
                              {warn ? (
                                <span aria-hidden="true" title="Поза доступним часом" className="absolute top-0 right-1 text-xs font-bold text-[#C77700]">
                                  !
                                </span>
                              ) : null}
                              <span className={`block font-semibold ${gone ? "line-through" : ""}`}>{a.clientName}</span>
                              <span className="block">
                                {timeRange(a)} · {durationLabel(a.durationMinutes)}
                              </span>
                              <span className="block truncate">{gone ? cancelledByShort(a.cancelledBy) : a.serviceName}</span>
                              {a.clientConfirmedAt && !gone ? (
                                <span data-testid="block-confirmed" title="Клієнт підтвердив візит" className="absolute right-1 bottom-0.5 text-xs font-bold">
                                  <span aria-hidden="true">✓</span>
                                  <span className="sr-only">клієнт підтвердив візит</span>
                                </span>
                              ) : null}
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
                    moveProblem={moveProblem}
                    outOfHours={outOfHours(selected)}
                    canChangeStatus={!isSpecialist || selected.specialistId === user.specialistId}
                    onStatusChanged={(message) => setNotice(message)}
                    onMoved={(newStart) => {
                      setSelectedId(null);
                      setNotice(`Запис перенесено на ${dateTimeLabel(newStart)}.`);
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
                <div className="flex justify-between text-sm"><span className="text-(--muted)">Завантаження</span><span className="font-semibold">{loadLabel}</span></div>
              </Card>
            </aside>
          </div>
        ) : null}
      </QueryState>
    </>
  );
}
