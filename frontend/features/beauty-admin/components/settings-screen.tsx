"use client";

import { useId, useState, type FormEvent } from "react";
import { z } from "zod";
import { useAuth } from "@/features/beauty-auth/components/auth-provider";
import { humanizeError } from "@/features/beauty-auth/errors";
import { canEditSettings } from "@/features/beauty-auth/permissions";
import { useCancellationSettings, useUpdateCancellationSettings } from "../hooks/use-beauty-admin";
import type { CancellationSettings } from "../types";
import { Card, PageHeader, PrimaryButton, QueryState } from "./ui";

const percent = (msg: string) => z.number(msg).int(msg).min(0, msg).max(100, msg);

const schema = z.object({
  windowHours: z.number("Вкажіть число").int("Лише ціле число").min(0, "Від 0 до 720").max(720, "Від 0 до 720"),
  refundPercentInWindow: percent("Від 0 до 100"),
  refundPercentOutside: percent("Від 0 до 100"),
  deductFee: z.boolean(),
  // Перевіряється завжди, навіть коли комісію вимкнено (так само робить сервер).
  feePercent: percent("Від 0 до 100"),
});

type FieldKey = "windowHours" | "refundPercentInWindow" | "refundPercentOutside" | "feePercent";
type Draft = Record<FieldKey, string> & { deductFee: boolean };
type Errors = Partial<Record<FieldKey, string>>;

const toDraft = (s: CancellationSettings): Draft => ({
  windowHours: String(s.windowHours),
  refundPercentInWindow: String(s.refundPercentInWindow),
  refundPercentOutside: String(s.refundPercentOutside),
  deductFee: s.deductFee,
  feePercent: String(s.feePercent),
});

const num = (v: string) => (v.trim() === "" ? Number.NaN : Number(v));

/** Код помилки сервера -> поле форми. */
const FIELD_BY_CODE: Record<string, FieldKey> = {
  invalid_window_hours: "windowHours",
  invalid_refund_percent: "refundPercentInWindow",
  invalid_fee_percent: "feePercent",
};

function NumberField({
  label,
  hint,
  suffix,
  value,
  error,
  readOnly,
  onChange,
}: {
  label: string;
  hint?: string;
  suffix: string;
  value: string;
  error?: string;
  readOnly: boolean;
  onChange: (v: string) => void;
}) {
  const id = useId();
  const hintId = `${id}-hint`;
  const errId = `${id}-err`;
  return (
    <div className="flex flex-col gap-1.5">
      <label htmlFor={id} className="text-[13px] font-semibold">
        {label}
      </label>
      <div className="flex items-center gap-2">
        <input
          id={id}
          type="number"
          inputMode="numeric"
          min={0}
          step={1}
          value={value}
          disabled={readOnly}
          onChange={(e) => onChange(e.target.value)}
          aria-invalid={error ? true : undefined}
          aria-describedby={[hint ? hintId : "", error ? errId : ""].filter(Boolean).join(" ") || undefined}
          className="min-h-11 w-32 rounded-xl border border-(--line-strong) bg-white px-3.5 text-[15px] text-(--ink) disabled:bg-(--page) disabled:text-(--muted) aria-[invalid=true]:border-[#8A1F1F]"
        />
        <span className="text-sm text-(--muted)">{suffix}</span>
      </div>
      {hint ? (
        <p id={hintId} className="text-[13px] text-(--muted)">
          {hint}
        </p>
      ) : null}
      {error ? (
        <p id={errId} className="text-[13px] text-[#8A1F1F]">
          {error}
        </p>
      ) : null}
    </div>
  );
}

function CancellationForm({ initial, canEdit }: { initial: CancellationSettings; canEdit: boolean }) {
  const update = useUpdateCancellationSettings();
  const [draft, setDraft] = useState<Draft>(() => toDraft(initial));
  const [errors, setErrors] = useState<Errors>({});
  const [saved, setSaved] = useState(false);
  const [serverError, setServerError] = useState<string | null>(null);
  const feeId = useId();

  const set = <K extends keyof Draft>(key: K, value: Draft[K]) => {
    setDraft((d) => ({ ...d, [key]: value }));
    setSaved(false);
  };

  function onSubmit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    if (!canEdit) return;
    const parsed = schema.safeParse({
      windowHours: num(draft.windowHours),
      refundPercentInWindow: num(draft.refundPercentInWindow),
      refundPercentOutside: num(draft.refundPercentOutside),
      deductFee: draft.deductFee,
      feePercent: num(draft.feePercent),
    });
    if (!parsed.success) {
      const next: Errors = {};
      for (const issue of parsed.error.issues) {
        const key = issue.path[0] as FieldKey;
        next[key] ??= issue.message;
      }
      setErrors(next);
      setServerError(null);
      return;
    }
    setErrors({});
    setServerError(null);
    update.mutate(parsed.data, {
      onSuccess: (s) => {
        setDraft(toDraft(s));
        setSaved(true);
      },
      onError: (err) => {
        const code = (err as { code?: string }).code ?? "";
        const field = FIELD_BY_CODE[code];
        if (field) setErrors({ [field]: humanizeError(err) });
        else setServerError(humanizeError(err, "Не вдалося зберегти налаштування. Спробуйте ще раз."));
      },
    });
  }

  const feeOn = draft.deductFee;
  const w = draft.windowHours || "N";

  return (
    <form onSubmit={onSubmit} noValidate className="flex flex-col gap-6">
      {!canEdit ? (
        <p role="note" className="rounded-xl bg-(--tint) px-3.5 py-2.5 text-sm">
          Лише власник може змінювати ці налаштування. Ви бачите поточні значення.
        </p>
      ) : null}

      <div className="grid gap-5 sm:grid-cols-2">
        <NumberField
          label="Вікно скасування, годин до візиту"
          suffix="год"
          hint="Від 0 до 720. Скасування за цей час до початку (включно з межею) вважається пізнім."
          value={draft.windowHours}
          error={errors.windowHours}
          readOnly={!canEdit}
          onChange={(v) => set("windowHours", v)}
        />
        <div />
        <NumberField
          label={`Повернення, якщо скасовано за ${w} год або менше`}
          suffix="%"
          hint="Від 0 до 100."
          value={draft.refundPercentInWindow}
          error={errors.refundPercentInWindow}
          readOnly={!canEdit}
          onChange={(v) => set("refundPercentInWindow", v)}
        />
        <NumberField
          label={`Повернення, якщо скасовано раніше ніж за ${w} год`}
          suffix="%"
          hint="Від 0 до 100."
          value={draft.refundPercentOutside}
          error={errors.refundPercentOutside}
          readOnly={!canEdit}
          onChange={(v) => set("refundPercentOutside", v)}
        />
      </div>

      <fieldset className="flex flex-col gap-3 border-0 p-0">
        <legend className="mb-1 text-[13px] font-semibold">Комісія</legend>
        <label htmlFor={feeId} className="flex min-h-11 cursor-pointer items-center gap-3 text-sm">
          <input
            id={feeId}
            type="checkbox"
            checked={draft.deductFee}
            disabled={!canEdit}
            onChange={(e) => set("deductFee", e.target.checked)}
            className="size-5 accent-(--accent)"
          />
          Утримувати комісію з суми повернення
        </label>
        <NumberField
          label="Комісія"
          suffix="%"
          hint={feeOn ? "Від 0 до 100. Віднімається від суми повернення." : "Не застосовується, поки комісію вимкнено, але значення має бути від 0 до 100."}
          value={draft.feePercent}
          error={errors.feePercent}
          readOnly={!canEdit}
          onChange={(v) => set("feePercent", v)}
        />
      </fieldset>

      <p className="rounded-xl bg-(--page) px-3.5 py-2.5 text-sm">
        Приклад: оплата 1000 ₴, скасування за {w} год або менше — повернемо{" "}
        <strong>{summary(draft, true)}</strong>; раніше — <strong>{summary(draft, false)}</strong>.
      </p>

      {serverError ? (
        <p role="alert" className="rounded-xl bg-[#FBE4E4] px-3.5 py-2.5 text-sm text-[#8A1F1F]">
          {serverError}
        </p>
      ) : null}
      {saved ? (
        <p role="status" className="rounded-xl bg-[#DDF3E6] px-3.5 py-2.5 text-sm text-[#145A32]">
          Налаштування збережено.
        </p>
      ) : null}

      {canEdit ? (
        <div>
          <PrimaryButton type="submit" disabled={update.isPending}>
            {update.isPending ? "Зберігаємо…" : "Зберегти"}
          </PrimaryButton>
        </div>
      ) : null}
    </form>
  );
}

/** Орієнтовна сума повернення з 1000 ₴ (множення до ділення, округлення вниз — як на сервері). */
function summary(d: Draft, inWindow: boolean): string {
  const pct = num(inWindow ? d.refundPercentInWindow : d.refundPercentOutside);
  const fee = d.deductFee ? num(d.feePercent) : 0;
  if (![pct, fee].every((n) => Number.isFinite(n) && n >= 0 && n <= 100)) return "—";
  const kop = Math.floor((1000 * 100 * pct * (100 - fee)) / 10_000);
  return `${(kop / 100).toLocaleString("uk-UA", { minimumFractionDigits: 0, maximumFractionDigits: 2 })} ₴`;
}

export function SettingsScreen() {
  const { user } = useAuth();
  const q = useCancellationSettings();
  const canEdit = canEditSettings(user.role);

  return (
    <>
      <PageHeader title="Налаштування" subtitle="Повернення коштів при скасуванні запису" />
      <QueryState isPending={q.isPending} isError={q.isError} onRetry={() => q.refetch()}>
        {q.data ? (
          <Card className="max-w-3xl">
            <h2 className="mb-4 text-lg font-semibold">Повернення коштів</h2>
            <CancellationForm initial={q.data} canEdit={canEdit} />
          </Card>
        ) : null}
      </QueryState>
    </>
  );
}
