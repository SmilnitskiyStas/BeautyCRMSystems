"use client";

import { useId, useState } from "react";
import { useAuth } from "@/features/beauty-auth/components/auth-provider";
import { BeautyApiError, humanizeError } from "@/features/beauty-auth/errors";
import { canUseCalls } from "@/features/beauty-auth/permissions";
import { dateTimeLabel } from "../format";
import { offsetLabel } from "../reminders";
import { useLocations } from "../hooks/use-beauty-admin";
import { useCallTasks, useUpdateCallTask } from "../hooks/use-reminders";
import type { CallTask, CallTaskQuery, CallTaskStatus } from "../types";
import { Badge, Card, Chip, EmptyState, FIELD_INPUT, FIELD_LABEL, PageHeader, PrimaryButton, QueryState, RowLink, SecondaryButton, TextLink, type Tone } from "./ui";

const STATUS_VIEW: Record<CallTaskStatus, { label: string; tone: Tone }> = {
  open: { label: "Очікує дзвінка", tone: "wait" },
  done: { label: "Зроблено", tone: "ok" },
  skipped: { label: "Пропущено", tone: "neutral" },
  cancelled: { label: "Скасовано системою", tone: "neutral" },
};
const REASON_LABEL = { reminder: "Нагадування про візит", delivery_failed: "Не доставлено в Telegram" } as const;
const DATE = /^\d{4}-\d{2}-\d{2}$/;

/** Хто закрив задачу після 409 `task_already_closed` (поточний стан приходить у `task`). */
function closedBy(e: unknown): string | null {
  if (!(e instanceof BeautyApiError) || e.code !== "task_already_closed") return null;
  const t = e.task as Partial<CallTask> | undefined;
  const who = t?.completedBy?.name;
  return `Цю задачу вже закрито${who ? `: ${who}` : ""}${t?.status === "skipped" ? " (пропущено)" : t?.status === "done" ? " (зроблено)" : ""}. Список оновлено.`;
}

function TaskRow({ task }: { task: CallTask }) {
  const update = useUpdateCallTask();
  const [note, setNote] = useState("");
  const [message, setMessage] = useState<string | null>(null);
  const noteId = useId();
  const status = STATUS_VIEW[task.status];
  const tz = task.location.timezone;

  function act(next: "done" | "skipped" | "open") {
    setMessage(null);
    update.mutate(
      { id: task.id, patch: { status: next, ...(next !== "open" && note.trim() ? { note: note.trim() } : {}) } },
      {
        onSuccess: () => setNote(""),
        onError: (e) => setMessage(closedBy(e) ?? humanizeError(e, "Не вдалося оновити задачу. Спробуйте ще раз.")),
      },
    );
  }

  return (
    <li data-testid="call-task" data-status={task.status} className="flex flex-col gap-3 rounded-2xl bg-white p-4">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="flex min-w-0 flex-col gap-1">
          <div className="flex flex-wrap items-center gap-2">
            <span className="beauty-display text-lg font-semibold">{task.localDueTime}</span>
            <Badge tone={task.reason === "delivery_failed" ? "promo" : "now"}>{REASON_LABEL[task.reason]}</Badge>
            <Badge tone={status.tone}>{status.label}</Badge>
          </div>
          <RowLink href={`/beauty/clients/${task.client.id}`}>
            <span className="text-base font-semibold break-words">{task.client.name}</span>
          </RowLink>
          {task.client.phone ? (
            <a href={`tel:${task.client.phone.replace(/[^+\d]/g, "")}`} className="inline-flex min-h-11 items-center text-[15px] font-semibold text-(--accent) underline">
              {task.client.phone}
            </a>
          ) : null}
        </div>
        <div className="flex min-w-0 flex-col gap-0.5 text-sm sm:text-right">
          <span className="font-semibold">{dateTimeLabel(task.appointment.localStartsAt)}</span>
          <span>{task.appointment.serviceName}</span>
          <span className="text-(--muted)">
            {task.appointment.specialistName} · {task.location.name}
          </span>
          {task.offsetMinutes ? <span className="text-xs text-(--muted)">Дзвінок {offsetLabel(task.offsetMinutes)} до візиту</span> : null}
        </div>
      </div>

      {task.status !== "open" && task.completedBy ? (
        <p className="text-[13px] text-(--muted)">
          {task.status === "done" ? "Зробив(ла)" : "Пропустив(ла)"}: {task.completedBy.name}
          {task.completedAt ? `, ${dateTimeLabel(task.completedAt, tz)}` : ""}
        </p>
      ) : null}
      {task.note ? <p className="rounded-xl bg-(--page) px-3 py-2 text-sm">Нотатка: {task.note}</p> : null}

      {task.status === "open" ? (
        <div className="flex flex-col gap-2">
          <label htmlFor={noteId} className={FIELD_LABEL}>
            Нотатка (необов’язково)
          </label>
          <input id={noteId} type="text" maxLength={300} value={note} onChange={(e) => setNote(e.target.value)} className={FIELD_INPUT} />
          <div className="flex flex-wrap gap-2">
            <PrimaryButton disabled={update.isPending} onClick={() => act("done")}>
              Зателефонував
            </PrimaryButton>
            <SecondaryButton disabled={update.isPending} onClick={() => act("skipped")}>
              Пропустити
            </SecondaryButton>
          </div>
        </div>
      ) : task.status === "cancelled" ? null : (
        <div>
          <SecondaryButton disabled={update.isPending} onClick={() => act("open")}>
            Повернути
          </SecondaryButton>
        </div>
      )}
      {message ? (
        <p role="alert" className="rounded-xl bg-[#FBE4E4] px-3.5 py-2.5 text-sm text-[#8A1F1F]">
          {message}
        </p>
      ) : null}
    </li>
  );
}

function CallsView() {
  const [day, setDay] = useState("today");
  const [status, setStatus] = useState<CallTaskQuery["status"]>("open");
  const [locationId, setLocationId] = useState("");
  const locations = useLocations();
  const q = useCallTasks({ day, status, ...(locationId ? { locationId } : {}), take: 100 });
  const counts = q.data?.counts;

  return (
    <>
      <PageHeader title="Дзвінки" subtitle="Кому з клієнтів зателефонувати перед візитом. Список оновлюється сам." />
      <div className="flex flex-wrap items-end gap-4">
        <div className="flex flex-wrap gap-2" role="group" aria-label="День">
          <Chip pressed={day === "today"} onClick={() => setDay("today")}>
            Сьогодні
          </Chip>
          <Chip pressed={day === "tomorrow"} onClick={() => setDay("tomorrow")}>
            Завтра
          </Chip>
        </div>
        <div className="flex flex-col gap-1.5">
          <label htmlFor="calls-date" className={FIELD_LABEL}>
            Дата
          </label>
          <input
            id="calls-date"
            type="date"
            value={DATE.test(day) ? day : ""}
            onChange={(e) => e.target.value && setDay(e.target.value)}
            className={`${FIELD_INPUT} w-auto`}
          />
        </div>
        <div className="flex flex-col gap-1.5">
          <label htmlFor="calls-location" className={FIELD_LABEL}>
            Заклад
          </label>
          <select id="calls-location" value={locationId} onChange={(e) => setLocationId(e.target.value)} className={`${FIELD_INPUT} w-auto`}>
            <option value="">Усі заклади</option>
            {locations.data?.map((l) => (
              <option key={l.id} value={l.id}>
                {l.name}
              </option>
            ))}
          </select>
        </div>
        <div className="flex flex-col gap-1.5">
          <label htmlFor="calls-status" className={FIELD_LABEL}>
            Статус
          </label>
          <select id="calls-status" value={status} onChange={(e) => setStatus(e.target.value as CallTaskQuery["status"])} className={`${FIELD_INPUT} w-auto`}>
            <option value="open">Очікують дзвінка</option>
            <option value="done">Зроблено</option>
            <option value="skipped">Пропущено</option>
            <option value="all">Усі</option>
          </select>
        </div>
      </div>
      {counts ? (
        <p role="status" className="text-sm text-(--muted)">
          За цей день: очікують {counts.open} · зроблено {counts.done} · пропущено {counts.skipped}
        </p>
      ) : null}
      <QueryState isPending={q.isPending} isError={q.isError} onRetry={() => q.refetch()}>
        {q.data && q.data.items.length > 0 ? (
          <ul aria-label="Задачі дзвінків" className="m-0 flex list-none flex-col gap-3 p-0">
            {q.data.items.map((t) => (
              <TaskRow key={t.id} task={t} />
            ))}
          </ul>
        ) : (
          <Card>
            <EmptyState>
              {status === "open" ? "На цей день немає дзвінків, що очікують. " : "За цими фільтрами задач немає. "}
              Клієнти, яким потрібен дзвінок-нагадування, налаштовуються в профілі клієнта.
            </EmptyState>
            <TextLink href="/beauty/clients">До клієнтів</TextLink>
          </Card>
        )}
      </QueryState>
    </>
  );
}

export function CallsScreen() {
  const { user } = useAuth();
  if (!canUseCalls(user.role)) {
    return (
      <>
        <PageHeader title="Дзвінки" />
        <Card>
          <p role="note" className="text-sm">
            Задачі дзвінків доступні лише власнику й адміністратору.
          </p>
        </Card>
      </>
    );
  }
  return <CallsView />;
}
