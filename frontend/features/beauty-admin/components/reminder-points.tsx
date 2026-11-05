import { dateTimeLabel } from "../format";
import { offsetLabel, POINT_STATUS_LABEL, reminderLine, SKIP_REASON_LABEL } from "../reminders";
import type { AppointmentReminders } from "../types";

/** Точки нагадувань запису (лише читання, §22.11): спосіб, коли надсилається й статус; пропуск пояснено причиною. */
export function ReminderPoints({ reminders, timezone }: { reminders: AppointmentReminders; timezone?: string }) {
  const points = reminders.points ?? [];
  return (
    <section aria-label="Нагадування" data-testid="reminder-points" className="flex flex-col gap-1.5 rounded-xl bg-(--page) p-3 text-sm">
      <h3 className="text-sm font-semibold">Нагадування</h3>
      <p>{reminderLine(reminders.method, reminders.offsetsMinutes)}</p>
      {points.length > 0 ? (
        <ul className="m-0 flex list-none flex-col gap-1 p-0 text-[13px]">
          {points.map((p) => (
            <li key={`${p.kind}-${p.offsetMinutes}`}>
              <span className="font-semibold">
                {p.kind === "call" ? "Дзвінок" : "Повідомлення"} {offsetLabel(p.offsetMinutes)}
              </span>
              {`: ${dateTimeLabel(p.dueAt, timezone)}`}
              <span className="text-(--muted)">
                {` · ${POINT_STATUS_LABEL[p.status] ?? p.status}`}
                {p.skipReason ? ` (${SKIP_REASON_LABEL[p.skipReason] ?? "причину не визначено"})` : ""}
              </span>
            </li>
          ))}
        </ul>
      ) : reminders.method !== "none" ? (
        <p className="text-[13px] text-(--muted)">Точок немає: час нагадування вже минув.</p>
      ) : null}
    </section>
  );
}
