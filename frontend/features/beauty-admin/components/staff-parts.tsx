"use client";

import Link from "next/link";
import { useId, useState, type FormEvent } from "react";
import { z } from "zod";
import { humanizeError } from "@/features/beauty-auth/errors";
import { useInviteStaff, useRevokeInvite, useStaffInvites } from "../hooks/use-beauty-admin";
import { dateTimeLabel } from "../format";
import type { AbsenceConflict, AbsenceStatus, AbsenceType, ServiceItem, TimeInterval, Weekday, WorkingHours } from "../types";
import { DEFAULT_INTERVAL, MAX_INTERVALS_PER_DAY, WEEKDAYS, validateDay } from "../working-hours";
import { Badge, FIELD_INPUT, FIELD_LABEL, PrimaryButton, SecondaryButton, type Tone } from "./ui";

export const ABSENCE_TYPE_LABEL: Record<AbsenceType, string> = {
  sick: "Лікарняний",
  vacation: "Відпустка",
  day_off: "Вихідний",
  other: "Інше",
};

export const ABSENCE_STATUS_VIEW: Record<AbsenceStatus, { label: string; tone: Tone }> = {
  requested: { label: "Очікує підтвердження", tone: "wait" },
  approved: { label: "Підтверджено", tone: "ok" },
  rejected: { label: "Відхилено", tone: "neutral" },
  cancelled: { label: "Скасовано", tone: "neutral" },
};

export const ERROR_TEXT = "text-[13px] text-[#8A1F1F]";

export function ErrorBanner({ children }: { children: string }) {
  return (
    <p role="alert" className="rounded-xl bg-[#FBE4E4] px-3.5 py-2.5 text-sm text-[#8A1F1F]">
      {children}
    </p>
  );
}

export function SuccessBanner({ children }: { children: string }) {
  return (
    <p role="status" className="rounded-xl bg-[#DDF3E6] px-3.5 py-2.5 text-sm text-[#145A32]">
      {children}
    </p>
  );
}

/** Підписане текстове поле з підказкою й помилкою. */
export function TextField({
  label,
  value,
  onChange,
  error,
  hint,
  type = "text",
  required,
  disabled,
  autoComplete,
  maxLength,
}: {
  label: string;
  value: string;
  onChange: (v: string) => void;
  error?: string;
  hint?: string;
  type?: "text" | "tel" | "email";
  required?: boolean;
  disabled?: boolean;
  autoComplete?: string;
  maxLength?: number;
}) {
  const id = useId();
  const describedBy = [hint ? `${id}-hint` : "", error ? `${id}-err` : ""].filter(Boolean).join(" ") || undefined;
  return (
    <div className="flex flex-col gap-1.5">
      <label htmlFor={id} className={FIELD_LABEL}>
        {label}
        {required ? <span aria-hidden="true"> *</span> : null}
      </label>
      <input
        id={id}
        type={type}
        value={value}
        required={required}
        disabled={disabled}
        autoComplete={autoComplete}
        maxLength={maxLength}
        onChange={(e) => onChange(e.target.value)}
        aria-invalid={error ? true : undefined}
        aria-describedby={describedBy}
        className={`${FIELD_INPUT} disabled:bg-(--page) disabled:text-(--muted) aria-[invalid=true]:border-[#8A1F1F]`}
      />
      {hint ? (
        <p id={`${id}-hint`} className="text-[13px] text-(--muted)">
          {hint}
        </p>
      ) : null}
      {error ? (
        <p id={`${id}-err`} className={ERROR_TEXT}>
          {error}
        </p>
      ) : null}
    </div>
  );
}

export function CheckRow({
  label,
  checked,
  onChange,
  disabled,
  hint,
}: {
  label: string;
  checked: boolean;
  onChange: (next: boolean) => void;
  disabled?: boolean;
  hint?: string;
}) {
  const id = useId();
  return (
    <label htmlFor={id} className="flex min-h-11 cursor-pointer items-center gap-3 text-sm has-disabled:cursor-not-allowed">
      <input
        id={id}
        type="checkbox"
        checked={checked}
        disabled={disabled}
        onChange={(e) => onChange(e.target.checked)}
        className="size-5 flex-none accent-(--accent)"
      />
      <span>
        {label}
        {hint ? <span className="block text-[13px] text-(--muted)">{hint}</span> : null}
      </span>
    </label>
  );
}

/** Чекбокси послуг, згруповані за категоріями (fieldset + legend). */
export function ServicesChecklist({
  services,
  selected,
  onChange,
  disabled,
}: {
  services: ServiceItem[];
  selected: string[];
  onChange: (ids: string[]) => void;
  disabled?: boolean;
}) {
  const groups = new Map<string, ServiceItem[]>();
  for (const s of services) {
    const key = s.category ?? "Без категорії";
    groups.set(key, [...(groups.get(key) ?? []), s]);
  }
  const toggle = (id: string, on: boolean) => onChange(on ? [...selected, id] : selected.filter((x) => x !== id));

  if (services.length === 0) return <p className="text-sm text-(--muted)">У каталозі ще немає послуг.</p>;
  return (
    <div className="flex flex-col gap-4">
      {[...groups.entries()].map(([category, list]) => (
        <fieldset key={category} className="m-0 flex flex-col gap-0.5 border-0 p-0">
          <legend className="mb-1 text-[13px] font-semibold text-(--muted)">{category}</legend>
          {list.map((s) => (
            <CheckRow key={s.id} label={s.name} checked={selected.includes(s.id)} disabled={disabled} onChange={(on) => toggle(s.id, on)} />
          ))}
        </fieldset>
      ))}
    </div>
  );
}

function IntervalRow({
  interval,
  disabled,
  describedBy,
  canRemove,
  onChange,
  onRemove,
}: {
  interval: TimeInterval;
  disabled?: boolean;
  describedBy?: string;
  canRemove: boolean;
  onChange: (next: TimeInterval) => void;
  onRemove: () => void;
}) {
  const fromId = useId();
  const toId = useId();
  return (
    <div className="flex flex-wrap items-end gap-3">
      <div className="flex flex-col gap-1">
        <label htmlFor={fromId} className={FIELD_LABEL}>
          Початок
        </label>
        <input
          id={fromId}
          type="time"
          step={900}
          required
          disabled={disabled}
          value={interval.from}
          aria-describedby={describedBy}
          onChange={(e) => onChange({ ...interval, from: e.target.value })}
          className={`${FIELD_INPUT} w-32`}
        />
      </div>
      <div className="flex flex-col gap-1">
        <label htmlFor={toId} className={FIELD_LABEL}>
          Кінець
        </label>
        <input
          id={toId}
          type="time"
          step={900}
          required
          disabled={disabled}
          value={interval.to}
          aria-describedby={describedBy}
          onChange={(e) => onChange({ ...interval, to: e.target.value })}
          className={`${FIELD_INPUT} w-32`}
        />
      </div>
      {canRemove && !disabled ? <SecondaryButton onClick={onRemove}>Видалити інтервал</SecondaryButton> : null}
    </div>
  );
}

/** Редактор тижневого графіка у форматі `working_hours` (§9). Відсутній день = вихідний. */
export function WorkingHoursEditor({
  value,
  onChange,
  disabled,
}: {
  value: WorkingHours;
  onChange: (next: WorkingHours) => void;
  disabled?: boolean;
}) {
  const uid = useId();
  const setDay = (day: Weekday, list: TimeInterval[]) => onChange({ ...value, [day]: list });

  return (
    <div className="flex flex-col gap-3">
      <p className="text-[13px] text-(--muted)">
        Вихідні дні закладу перекривають цей графік: у закритий день слотів немає, навіть якщо тут день робочий.
      </p>
      {WEEKDAYS.map((d) => {
        const list = value[d.key] ?? [];
        const works = list.length > 0;
        const error = validateDay(list);
        const errId = `${uid}-${d.key}-err`;
        return (
          <fieldset key={d.key} className="m-0 flex flex-col gap-2 rounded-xl border border-(--line) p-3">
            <legend className="px-1 text-sm font-semibold">{d.label}</legend>
            <CheckRow
              label={works ? "Працює" : "Вихідний"}
              checked={works}
              disabled={disabled}
              onChange={(on) => setDay(d.key, on ? [{ ...DEFAULT_INTERVAL }] : [])}
            />
            {works ? (
              <div className="flex flex-col gap-3">
                {list.map((iv, idx) => (
                  <IntervalRow
                    key={idx}
                    interval={iv}
                    disabled={disabled}
                    describedBy={error ? errId : undefined}
                    canRemove={list.length > 1}
                    onChange={(next) => setDay(d.key, list.map((x, i) => (i === idx ? next : x)))}
                    onRemove={() => setDay(d.key, list.filter((_, i) => i !== idx))}
                  />
                ))}
                {!disabled && list.length < MAX_INTERVALS_PER_DAY ? (
                  <div>
                    <SecondaryButton
                      onClick={() => {
                        const last = list[list.length - 1];
                        const from = last && last.to < "20:00" ? last.to : "14:00";
                        setDay(d.key, [...list, { from, to: from < "20:00" ? "20:00" : "23:00" }]);
                      }}
                    >
                      Додати інтервал
                    </SecondaryButton>
                  </div>
                ) : null}
              </div>
            ) : null}
            {error ? (
              <p id={errId} role="alert" className={ERROR_TEXT}>
                {error}
              </p>
            ) : null}
          </fieldset>
        );
      })}
    </div>
  );
}

/** Одноразове посилання на запрошення з копіюванням. Токен не логується й не кешується. */
export function InviteBox({ token, onClose }: { token: string; onClose: () => void }) {
  const [origin] = useState(() => (typeof window === "undefined" ? "" : window.location.origin));
  const [copied, setCopied] = useState<"ok" | "fail" | null>(null);
  const id = useId();
  const link = `${origin}/invite/${token}`;

  async function copy() {
    try {
      await navigator.clipboard.writeText(link);
      setCopied("ok");
    } catch {
      setCopied("fail");
    }
  }

  return (
    <div className="flex flex-col gap-2.5 rounded-xl border-2 border-(--accent) bg-(--tint) p-4">
      <h3 className="text-base font-semibold">Посилання-запрошення</h3>
      <p className="text-sm">
        Надішліть це посилання працівнику самостійно: пошта з системи не надсилається. Посилання показується{" "}
        <strong>лише один раз</strong> — після закриття його не можна буде переглянути.
      </p>
      <label htmlFor={id} className={FIELD_LABEL}>
        Посилання
      </label>
      <input id={id} readOnly value={link} onFocus={(e) => e.currentTarget.select()} className={`${FIELD_INPUT} font-mono text-[13px]`} />
      <div className="flex flex-wrap gap-2">
        <PrimaryButton onClick={copy}>Копіювати посилання</PrimaryButton>
        <SecondaryButton onClick={onClose}>Закрити</SecondaryButton>
      </div>
      {copied === "ok" ? <p role="status" className="text-sm font-semibold text-[#145A32]">Посилання скопійовано.</p> : null}
      {copied === "fail" ? (
        <p role="status" className="text-sm text-[#8A1F1F]">
          Не вдалося скопіювати автоматично. Виділіть посилання в полі вище й скопіюйте вручну.
        </p>
      ) : null}
    </div>
  );
}

const emailSchema = z.string().trim().min(1, "Вкажіть пошту").email("Некоректна адреса пошти");

/** «Запросити на вхід» для вже створеного профілю. */
export function InvitePanel({ staffId }: { staffId: string }) {
  const invite = useInviteStaff();
  const [email, setEmail] = useState("");
  const [error, setError] = useState<string | undefined>();
  const [token, setToken] = useState<string | null>(null);
  const pending = useStaffInvites(staffId);
  const revoke = useRevokeInvite(staffId);

  function submit(e: FormEvent) {
    e.preventDefault();
    const parsed = emailSchema.safeParse(email);
    if (!parsed.success) {
      setError(parsed.error.issues[0]?.message);
      return;
    }
    setError(undefined);
    invite.mutate(
      { id: staffId, email: parsed.data },
      {
        onSuccess: (r) => {
          setToken(r.token);
          setEmail("");
        },
      },
    );
  }

  return (
    <div className="flex flex-col gap-3">
      <h2 className="text-lg font-semibold">Доступ до системи</h2>
      <p className="text-sm text-(--muted)">Запрошення необов’язкове. Працівник отримає посилання, за яким створить пароль.</p>
      {pending.data && pending.data.length > 0 ? (
        <ul className="m-0 flex list-none flex-col gap-2 p-0" aria-label="Незакриті запрошення">
          {pending.data.map((i) => (
            <li key={i.id} className="flex flex-wrap items-center justify-between gap-2 text-sm">
              <span className="break-all">
                {i.email} · діє до {dateTimeLabel(i.expiresAt)}
              </span>
              <SecondaryButton disabled={revoke.isPending} onClick={() => revoke.mutate(i.id)}>
                Відкликати
              </SecondaryButton>
            </li>
          ))}
        </ul>
      ) : null}
      {revoke.isError ? <ErrorBanner>{humanizeError(revoke.error, "Не вдалося відкликати запрошення.")}</ErrorBanner> : null}
      {token ? (
        <InviteBox
          token={token}
          onClose={() => {
            setToken(null);
            invite.reset();
          }}
        />
      ) : (
        <form onSubmit={submit} noValidate className="flex flex-col gap-3">
          <TextField label="Пошта працівника" type="email" autoComplete="off" value={email} onChange={setEmail} error={error} />
          {invite.isError ? <ErrorBanner>{humanizeError(invite.error, "Не вдалося створити запрошення. Спробуйте ще раз.")}</ErrorBanner> : null}
          <div>
            <PrimaryButton type="submit" disabled={invite.isPending}>
              {invite.isPending ? "Створюємо…" : "Запросити на вхід"}
            </PrimaryButton>
          </div>
        </form>
      )}
    </div>
  );
}

export function StatusBadge({ status }: { status: AbsenceStatus }) {
  const v = ABSENCE_STATUS_VIEW[status];
  return <Badge tone={v.tone}>{v.label}</Badge>;
}

/** Записи, які перетинаються з відсутністю: керівник сам переносить/скасовує їх у календарі. */
export function ConflictsList({ conflicts, specialistId }: { conflicts: AbsenceConflict[]; specialistId: string }) {
  if (conflicts.length === 0) {
    return <p className="text-sm text-(--muted)">Конфліктних записів немає.</p>;
  }
  return (
    <div className="flex flex-col gap-2 rounded-xl bg-[#FFF1CC] p-3.5 text-[#5C3900]">
      <p className="text-sm font-semibold">
        Є записи на ці дні ({conflicts.length}). Вони не скасовуються автоматично — перенесіть або скасуйте їх у календарі.
      </p>
      <ul className="m-0 flex list-none flex-col gap-1 p-0 text-sm">
        {conflicts.map((c) => (
          <li key={c.appointmentId}>
            {dateTimeLabel(c.startsAt, c.timezone)} · {c.serviceName}
          </li>
        ))}
      </ul>
      <Link
        href={`/beauty/calendar?specialist=${encodeURIComponent(specialistId)}`}
        className="inline-flex min-h-11 items-center text-sm font-semibold text-[#4A2260] underline"
      >
        Відкрити календар майстра
      </Link>
    </div>
  );
}
