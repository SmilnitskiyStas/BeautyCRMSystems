"use client";

import { useEffect, useId, useState, type FormEvent } from "react";
import { z } from "zod";
import { useAuth } from "@/features/beauty-auth/components/auth-provider";
import { humanizeError } from "@/features/beauty-auth/errors";
import { canEditSettings } from "@/features/beauty-auth/permissions";
import { useCancellationSettings, useUpdateCancellationSettings } from "../hooks/use-beauty-admin";
import type { CancellationSettings } from "../types";
import { AiSettingsSection } from "./ai-settings-section";
import { ReminderSettingsSection } from "./reminder-settings-section";
import { Card, PageHeader, PrimaryButton, QueryState } from "./ui";

const MAX_TERMS = 500;

const percent = (msg: string) => z.number(msg).int(msg).min(0, msg).max(100, msg);

const schema = z.object({
  windowHours: z.number("Вкажіть число").int("Лише ціле число").min(0, "Від 0 до 720").max(720, "Від 0 до 720"),
  refundPercentInWindow: percent("Від 0 до 100"),
  refundPercentOutside: percent("Від 0 до 100"),
  deductFee: z.boolean(),
  // Перевіряється завжди, навіть коли комісію вимкнено (так само робить сервер).
  feePercent: percent("Від 0 до 100"),
  showTermsToClient: z.boolean(),
  customTermsText: z.string().max(MAX_TERMS, `Не більше ${MAX_TERMS} символів`),
});

type FieldKey = "windowHours" | "refundPercentInWindow" | "refundPercentOutside" | "feePercent" | "customTermsText";
type NumKey = Exclude<FieldKey, "customTermsText">;
type Draft = Record<NumKey, string> & { deductFee: boolean; showTermsToClient: boolean; customTermsText: string };
type Errors = Partial<Record<FieldKey, string>>;

const toDraft = (s: CancellationSettings): Draft => ({
  windowHours: String(s.windowHours),
  refundPercentInWindow: String(s.refundPercentInWindow),
  refundPercentOutside: String(s.refundPercentOutside),
  deductFee: s.deductFee,
  feePercent: String(s.feePercent),
  // Старий API без нових полів: показ увімкнено, автотекст.
  showTermsToClient: s.showTermsToClient ?? true,
  customTermsText: s.customTermsText ?? "",
});

const fill = (tpl: string, vars: Record<string, string>) => tpl.replace(/\{\{(\w+)\}\}/g, (_, k: string) => vars[k] ?? "");

/** Автоматичні рядки умов — ті самі, що в мобільному застосунку (mobile/src/i18n/uk.ts, cancellation). */
function autoTermsLines(d: Draft): string[] {
  const h = num(d.windowHours);
  const inP = num(d.refundPercentInWindow);
  const outP = num(d.refundPercentOutside);
  const fee = num(d.feePercent);
  const hs = Number.isFinite(h) ? String(h) : "N";
  const ps = (n: number) => (Number.isFinite(n) ? String(n) : "—");
  const lines: string[] = [];
  lines.push(
    outP === 100 && !d.deductFee
      ? fill("Безкоштовне скасування більш ніж за {{h}} год до візиту.", { h: hs })
      : fill("Якщо скасувати раніше — повернення {{p}}%.", { p: ps(outP) }),
  );
  lines.push(fill("Якщо скасувати за {{h}} год і менше — повернення {{p}}%.", { h: hs, p: ps(inP) }));
  if (d.deductFee) lines.push(fill("Утримується комісія {{p}}%.", { p: ps(fee) }));
  return lines;
}

const num = (v: string) => (v.trim() === "" ? Number.NaN : Number(v));

/** Код помилки сервера -> поле форми. */
const FIELD_BY_CODE: Record<string, FieldKey> = {
  invalid_window_hours: "windowHours",
  invalid_refund_percent: "refundPercentInWindow",
  invalid_fee_percent: "feePercent",
  invalid_terms_text: "customTermsText",
};

const SERVER_FIELD_MESSAGE: Partial<Record<string, string>> = {
  invalid_terms_text: `Власний текст умов має бути не довшим за ${MAX_TERMS} символів.`,
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
  const showId = useId();
  const termsId = useId();
  const termsHintId = `${termsId}-hint`;
  const termsErrId = `${termsId}-err`;
  const termsLen = draft.customTermsText.length;

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
      showTermsToClient: draft.showTermsToClient,
      customTermsText: draft.customTermsText,
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
    const { customTermsText, ...rest } = parsed.data;
    const trimmed = customTermsText.trim();
    update.mutate({ ...rest, customTermsText: trimmed === "" ? null : trimmed }, {
      onSuccess: (s) => {
        setDraft(toDraft(s));
        setSaved(true);
      },
      onError: (err) => {
        const code = (err as { code?: string }).code ?? "";
        const field = FIELD_BY_CODE[code];
        if (field) setErrors({ [field]: SERVER_FIELD_MESSAGE[code] ?? humanizeError(err) });
        else setServerError(humanizeError(err, "Не вдалося зберегти налаштування. Спробуйте ще раз."));
      },
    });
  }

  const feeOn = draft.deductFee;
  const w = draft.windowHours || "N";

  return (
    <form onSubmit={onSubmit} noValidate data-testid="cancellation-form" className="grid grid-cols-1 items-start gap-6 lg:grid-cols-2 lg:gap-x-10">
      {!canEdit ? (
        <p role="note" className="lg:col-span-2 rounded-xl bg-(--tint) px-3.5 py-2.5 text-sm">
          Лише власник може змінювати ці налаштування. Ви бачите поточні значення.
        </p>
      ) : null}

      <div className="flex flex-col gap-5">
        <NumberField
          label="Вікно скасування, годин до візиту"
          suffix="год"
          hint="Від 0 до 720. Скасування за цей час до початку (включно з межею) вважається пізнім."
          value={draft.windowHours}
          error={errors.windowHours}
          readOnly={!canEdit}
          onChange={(v) => set("windowHours", v)}
        />
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

      <p className="rounded-xl bg-(--page) px-3.5 py-2.5 text-sm lg:col-span-2">
        Приклад: оплата 1000 ₴, скасування за {w} год або менше — повернемо{" "}
        <strong>{summary(draft, true)}</strong>; раніше — <strong>{summary(draft, false)}</strong>.
      </p>

      <fieldset className="flex flex-col gap-3 border-0 p-0">
        <legend className="mb-1 text-[13px] font-semibold">Що бачить клієнт</legend>
        <label htmlFor={showId} className="flex min-h-11 cursor-pointer items-center gap-3 text-sm">
          <input
            id={showId}
            type="checkbox"
            role="switch"
            checked={draft.showTermsToClient}
            disabled={!canEdit}
            aria-describedby={`${showId}-hint`}
            onChange={(e) => set("showTermsToClient", e.target.checked)}
            className="size-5 accent-(--accent)"
          />
          Показувати умови скасування клієнту у застосунку
        </label>
        <p id={`${showId}-hint`} className="text-[13px] text-(--muted)">
          Правила повернення коштів діють незалежно від того, чи показано текст клієнту.
        </p>

        <div className="flex flex-col gap-1.5">
          <label htmlFor={termsId} className="text-[13px] font-semibold">
            Власний текст умов (необов&apos;язково)
          </label>
          <textarea
            id={termsId}
            rows={4}
            value={draft.customTermsText}
            disabled={!canEdit || !draft.showTermsToClient}
            placeholder="Наприклад: Скасування безкоштовне за добу до візиту."
            onChange={(e) => set("customTermsText", e.target.value)}
            aria-invalid={errors.customTermsText ? true : undefined}
            aria-describedby={[termsHintId, errors.customTermsText ? termsErrId : ""].filter(Boolean).join(" ")}
            className="w-full max-w-xl rounded-xl border border-(--line-strong) bg-white px-3.5 py-2.5 text-[15px] text-(--ink) disabled:bg-(--page) disabled:text-(--muted) aria-[invalid=true]:border-[#8A1F1F]"
          />
          <p className={`text-[13px] ${termsLen > MAX_TERMS ? "text-[#8A1F1F]" : "text-(--muted)"}`} aria-live="polite">
            {termsLen} / {MAX_TERMS}
          </p>
          <p id={termsHintId} className="text-[13px] text-(--muted)">
            Якщо заповнено, клієнт побачить цей текст замість автоматичного. Залиште порожнім, щоб показувати автоматичний текст.
          </p>
          {errors.customTermsText ? (
            <p id={termsErrId} className="text-[13px] text-[#8A1F1F]">
              {errors.customTermsText}
            </p>
          ) : null}
        </div>

        <div role="region" aria-label="Так побачить клієнт" className="rounded-xl bg-(--page) px-3.5 py-2.5 text-sm">
          <p className="mb-1 text-[13px] font-semibold">Так побачить клієнт</p>
          {!draft.showTermsToClient ? (
            <p className="text-(--muted)">Блок умов приховано</p>
          ) : draft.customTermsText.trim() !== "" ? (
            <p className="whitespace-pre-line">{draft.customTermsText.trim()}</p>
          ) : (
            <ul className="flex flex-col gap-0.5">
              {autoTermsLines(draft).map((l) => (
                <li key={l}>{l}</li>
              ))}
            </ul>
          )}
        </div>
      </fieldset>

      {serverError ? (
        <p role="alert" className="lg:col-span-2 rounded-xl bg-[#FBE4E4] px-3.5 py-2.5 text-sm text-[#8A1F1F]">
          {serverError}
        </p>
      ) : null}
      {saved ? (
        <p role="status" className="lg:col-span-2 rounded-xl bg-[#DDF3E6] px-3.5 py-2.5 text-sm text-[#145A32]">
          Налаштування збережено.
        </p>
      ) : null}

      {canEdit ? (
        <div className="lg:col-span-2">
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
  const canEdit = canEditSettings(user);

  // Посилання /beauty/settings#ai (банер в «AI-асистент»): секція з'являється після завантаження, тож прокручуємо вручну.
  useEffect(() => {
    if (window.location.hash !== "#ai") return;
    const t = setTimeout(() => document.getElementById("ai")?.scrollIntoView?.({ block: "start" }), 400);
    return () => clearTimeout(t);
  }, []);

  return (
    <>
      <PageHeader title="Налаштування" subtitle="Повернення коштів, нагадування клієнтам та ШІ-асистент" />
      <div data-testid="settings-layout" className="grid grid-cols-1 items-start gap-6">
        <QueryState isPending={q.isPending} isError={q.isError} onRetry={() => q.refetch()}>
          {q.data ? (
            <Card>
              <h2 className="mb-4 text-lg font-semibold">Повернення коштів</h2>
              <CancellationForm initial={q.data} canEdit={canEdit} />
            </Card>
          ) : null}
        </QueryState>
        <ReminderSettingsSection />
        <AiSettingsSection />
      </div>
    </>
  );
}
