"use client";

import { useId, useRef, useState, type FormEvent } from "react";
import { useAuth } from "@/features/beauty-auth/components/auth-provider";
import { canEditAiSettings, canManageAiKey } from "@/features/beauty-auth/permissions";
import { FALLBACK_ROLE_LABEL, MODES, MODE_VIEW, aiErrorText, errorCode, nf, overallStatusText } from "../ai-settings/messages";
import { promptForSave, promptLength } from "../ai-settings/prompt";
import { useAiModels, useAiSettings, useSaveAiSettings, useSetAiKeySource } from "../hooks/use-beauty-admin";
import type { AiCatalog, AiKeySource, AiMode, AiRoleId, AiSettings, AiWarning } from "../types";
import { AiConfirmDialog } from "./ai-confirm-dialog";
import { AiRoleBlock, type RoleSelection } from "./ai-role-block";
import { AiUsageBlock } from "./ai-usage";
import { Card, FIELD_INPUT, FIELD_LABEL, PrimaryButton, QueryState, SecondaryButton } from "./ui";

const ROLE_ORDER: AiRoleId[] = ["chat", "analytics"];
const PROMPT_ERROR_CODES = ["prompt_addendum_too_long", "prompt_addendum_forbidden", "prompt_addendum_contains_secret"];

interface Draft {
  chat: RoleSelection;
  analytics: RoleSelection;
  mode: AiMode;
  prompt: string;
  tokens: string;
  requests: string;
  maxOut: string;
}

const toDraft = (s: AiSettings): Draft => ({
  chat: { provider: s.chat.provider, model: s.chat.model },
  analytics: { provider: s.analytics.provider, model: s.analytics.model },
  mode: s.mode,
  prompt: s.promptAddendum,
  tokens: s.limits.monthlyTokenLimit === null ? "" : String(s.limits.monthlyTokenLimit),
  requests: s.limits.monthlyRequestLimit === null ? "" : String(s.limits.monthlyRequestLimit),
  maxOut: String(s.limits.maxOutputTokensPerRequest),
});

type LimitField = "tokens" | "requests" | "maxOut";
const RANGES: Record<LimitField, { min: number; max: number; optional: boolean; msg: string }> = {
  tokens: { min: 1, max: 100_000_000_000, optional: true, msg: "Ціле число від 1 до 100 000 000 000 або порожньо (без ліміту)" },
  requests: { min: 1, max: 10_000_000, optional: true, msg: "Ціле число від 1 до 10 000 000 або порожньо (без ліміту)" },
  maxOut: { min: 64, max: 8192, optional: false, msg: "Ціле число від 64 до 8192" },
};

function parseLimit(f: LimitField, raw: string): { value: number | null; error?: string } {
  const r = RANGES[f];
  const t = raw.trim();
  if (t === "") return r.optional ? { value: null } : { value: null, error: r.msg };
  const n = Number(t);
  return Number.isInteger(n) && n >= r.min && n <= r.max ? { value: n } : { value: null, error: r.msg };
}

function KeySourceBlock({ settings, canManage }: { settings: AiSettings; canManage: boolean }) {
  const id = useId();
  const set = useSetAiKeySource();
  const [error, setError] = useState<string | null>(null);
  const label: Record<AiKeySource, string> = {
    platform: "Ключ платформи (з лімітами використання)",
    own: "Власний ключ компанії (оплата провайдеру напряму)",
  };
  if (!canManage)
    return (
      <p className="text-sm">
        <span className="font-semibold">Джерело ключа:</span> {label[settings.keySource]}. Змінювати його може лише власник.
      </p>
    );
  return (
    <fieldset className="flex flex-col gap-2 border-0 p-0" aria-describedby={`${id}-d`}>
      <legend className="mb-1 text-[13px] font-semibold">Джерело ключа (для обох сценаріїв)</legend>
      {(["platform", "own"] as const).map((k) => (
        <label key={k} className="flex min-h-11 cursor-pointer items-center gap-3 text-sm">
          <input
            type="radio"
            name={`${id}-src`}
            value={k}
            checked={settings.keySource === k}
            disabled={set.isPending}
            onChange={() => {
              setError(null);
              set.mutate(k, { onError: (e) => setError(aiErrorText(e, "Не вдалося змінити джерело ключа. Спробуйте ще раз.")) });
            }}
            className="size-5 accent-(--accent)"
          />
          {label[k]}
        </label>
      ))}
      <p id={`${id}-d`} className="text-[13px] text-(--muted)">
        Власні ключі вводяться нижче, у блоці кожного сценарію.
      </p>
      {error ? (
        <p role="alert" className="text-[13px] text-[#8A1F1F]">
          {error}
        </p>
      ) : null}
    </fieldset>
  );
}

function AiSettingsBody({ settings, catalog, canKey }: { settings: AiSettings; catalog: AiCatalog; canKey: boolean }) {
  const id = useId();
  const save = useSaveAiSettings();
  const [draft, setDraft] = useState<Draft>(() => toDraft(settings));
  const [ackAuto, setAckAuto] = useState(false);
  const [autoDialog, setAutoDialog] = useState(false);
  const [limitErrors, setLimitErrors] = useState<Partial<Record<LimitField, string>>>({});
  const [promptError, setPromptError] = useState<string | null>(null);
  const [formError, setFormError] = useState<string | null>(null);
  const [warnings, setWarnings] = useState<AiWarning[]>([]);
  const [saved, setSaved] = useState(false);
  const promptRef = useRef<HTMLTextAreaElement>(null);
  const alertRef = useRef<HTMLParagraphElement>(null);
  const autoRadio = useRef<HTMLInputElement>(null);
  const setFocusErr = (target: string) => setTimeout(() => document.getElementById(target)?.focus(), 0);

  const roleLabels = {
    chat: catalog.roles.find((r) => r.id === "chat")?.label ?? FALLBACK_ROLE_LABEL.chat,
    analytics: catalog.roles.find((r) => r.id === "analytics")?.label ?? FALLBACK_ROLE_LABEL.analytics,
  };
  const max = settings.promptAddendumMaxLength || 4000;
  const len = promptLength(draft.prompt);
  const over = len > max;
  const promptInvalid = over || !!promptError;
  const promptErrText = over ? `Промпт задовгий: ${nf(len)} з ${nf(max)} символів. Скоротіть текст.` : promptError;

  const patch = (p: Partial<Draft>) => {
    setDraft((d) => ({ ...d, ...p }));
    setSaved(false);
  };

  function onSubmit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    if (over) {
      setFocusErr(`${id}-prompt`);
      return;
    }
    const tokens = parseLimit("tokens", draft.tokens);
    const requests = parseLimit("requests", draft.requests);
    const maxOut = parseLimit("maxOut", draft.maxOut);
    const errs: Partial<Record<LimitField, string>> = {};
    if (tokens.error) errs.tokens = tokens.error;
    if (requests.error) errs.requests = requests.error;
    if (maxOut.error) errs.maxOut = maxOut.error;
    setLimitErrors(errs);
    const firstBad = (["tokens", "requests", "maxOut"] as const).find((f) => errs[f]);
    if (firstBad) {
      setFocusErr(`${id}-${firstBad}`);
      return;
    }
    setFormError(null);
    setPromptError(null);
    setWarnings([]);
    save.mutate(
      {
        chat: draft.chat,
        analytics: draft.analytics,
        mode: draft.mode,
        promptAddendum: promptForSave(draft.prompt, settings.defaultPromptAddendum),
        limits: { monthlyTokenLimit: tokens.value, monthlyRequestLimit: requests.value, maxOutputTokensPerRequest: maxOut.value ?? 1024 },
        ...(ackAuto && draft.mode === "auto" ? { acknowledgeAutoMode: true } : {}),
      },
      {
        onSuccess: ({ settings: next, warnings: w }) => {
          setDraft(toDraft(next));
          setAckAuto(false);
          setWarnings(w);
          setSaved(true);
        },
        onError: (err) => {
          const code = errorCode(err);
          if (PROMPT_ERROR_CODES.includes(code)) {
            setPromptError(aiErrorText(err));
            setFocusErr(`${id}-prompt`);
          } else {
            setFormError(aiErrorText(err, "Не вдалося зберегти налаштування ШІ. Спробуйте ще раз."));
            queueMicrotask(() => alertRef.current?.focus());
          }
        },
      },
    );
  }

  const platform = settings.keySource === "platform";
  const pq = settings.platformQuota;

  return (
    <div className="flex flex-col gap-6">
      <p role="status" data-testid="ai-overall-status" className={`rounded-xl px-3.5 py-2.5 text-sm ${settings.status.chat.state === "ready" ? "bg-[#DDF3E6] text-[#145A32]" : "bg-[#FFF1CC] text-[#5C3900]"}`}>
        {overallStatusText(settings.status.chat)}
      </p>

      <KeySourceBlock settings={settings} canManage={canKey} />

      <form onSubmit={onSubmit} noValidate data-testid="ai-form" className="grid grid-cols-1 items-start gap-6 lg:grid-cols-2 lg:gap-x-10">
        {ROLE_ORDER.map((role) => {
          const otherRole: AiRoleId = role === "chat" ? "analytics" : "chat";
          return (
            <AiRoleBlock
              key={role}
              role={role}
              catalog={catalog}
              settings={settings}
              selection={draft[role]}
              other={{ role: otherRole, provider: draft[otherRole].provider }}
              canManageKey={canKey}
              roleLabels={roleLabels}
              onChange={(sel) => patch({ [role]: sel } as Partial<Draft>)}
            />
          );
        })}

        <fieldset className="flex flex-col gap-2 border-0 p-0">
          <legend className="mb-1 text-[13px] font-semibold">Режим автономності</legend>
          {MODES.map((m) => (
            <label key={m} className="flex min-h-11 cursor-pointer items-start gap-3 py-1 text-sm">
              <input
                ref={m === "auto" ? autoRadio : undefined}
                type="radio"
                name={`${id}-mode`}
                value={m}
                checked={draft.mode === m}
                aria-describedby={`${id}-mode-${m}`}
                onChange={() => {
                  if (m === "auto" && draft.mode !== "auto" && settings.mode !== "auto") setAutoDialog(true);
                  else {
                    patch({ mode: m });
                    if (m !== "auto") setAckAuto(false);
                  }
                }}
                className="mt-0.5 size-5 flex-none accent-(--accent)"
              />
              <span className="flex flex-col">
                <span className="font-semibold">
                  {MODE_VIEW[m].label}
                  {m === "confirm" ? " (за замовчуванням)" : ""}
                </span>
                <span id={`${id}-mode-${m}`} className="text-[13px] text-(--muted)">
                  {MODE_VIEW[m].hint}
                </span>
              </span>
            </label>
          ))}
          {autoDialog ? (
            <AiConfirmDialog
              title="Увімкнути режим «Діє сам»?"
              confirmLabel="Так, увімкнути"
              onCancel={() => {
                setAutoDialog(false);
                queueMicrotask(() => document.querySelector<HTMLInputElement>(`input[name="${id}-mode"]:checked`)?.focus());
              }}
              onConfirm={() => {
                setAutoDialog(false);
                setAckAuto(true);
                patch({ mode: "auto" });
                queueMicrotask(() => autoRadio.current?.focus());
              }}
            >
              <p>ШІ сам створюватиме записи й надсилатиме відповіді клієнтам без вашого підтвердження. Помилки можуть дійти до клієнтів.</p>
              <p>Режим можна змінити будь-коли. Правила безпеки та ізоляції компаній від режиму не залежать.</p>
            </AiConfirmDialog>
          ) : null}
        </fieldset>

        <div className="flex flex-col gap-1.5">
          <h3 className="text-base font-semibold">
            <label htmlFor={`${id}-prompt`}>Промпт даних для ШІ</label>
          </h3>
          <p id={`${id}-prompt-hint`} className="text-[13px] text-(--muted)">
            Стосується сценарію «{roleLabels.chat}». Додається до незмінних правил безпеки та ізоляції компаній; не змінює режим, знижки й доступ до
            даних. Не вставляйте ключі й персональні дані клієнтів. Порожнє поле означає типовий текст.
          </p>
          <textarea
            id={`${id}-prompt`}
            ref={promptRef}
            rows={8}
            value={draft.prompt}
            onChange={(e) => {
              setPromptError(null);
              patch({ prompt: e.target.value });
            }}
            aria-invalid={promptInvalid ? true : undefined}
            aria-describedby={[`${id}-prompt-hint`, `${id}-prompt-count`, promptInvalid ? `${id}-prompt-err` : ""].filter(Boolean).join(" ")}
            className="w-full resize-y rounded-xl border border-(--line-strong) bg-white px-3.5 py-3 text-[15px] leading-snug aria-[invalid=true]:border-[#8A1F1F]"
          />
          <div className="flex flex-wrap items-center justify-between gap-2">
            <span id={`${id}-prompt-count`} aria-live="polite" className={`text-[13px] ${over ? "font-semibold text-[#8A1F1F]" : "text-(--muted)"}`}>
              {nf(len)} / {nf(max)}
            </span>
            <SecondaryButton
              onClick={() => {
                setPromptError(null);
                patch({ prompt: settings.defaultPromptAddendum });
                promptRef.current?.focus();
              }}
            >
              Скинути до типового
            </SecondaryButton>
          </div>
          {promptInvalid ? (
            <p id={`${id}-prompt-err`} role="alert" className="text-[13px] text-[#8A1F1F]">
              {promptErrText}
            </p>
          ) : null}
        </div>

        <fieldset className="flex flex-col gap-3 border-0 p-0 lg:col-span-2">
          <legend className="mb-1 text-[13px] font-semibold">Ліміти (спільні для обох сценаріїв)</legend>
          <div className="grid gap-4 sm:grid-cols-3">
            {(
              [
                ["tokens", "Токенів на місяць", "Порожньо = без ліміту"],
                ["requests", "Запитів на місяць", "Порожньо = без ліміту"],
                ["maxOut", "Макс. токенів відповіді", "Від 64 до 8192"],
              ] as const
            ).map(([f, label, hint]) => (
              <div key={f} className="flex flex-col gap-1.5">
                <label htmlFor={`${id}-${f}`} className={FIELD_LABEL}>
                  {label}
                </label>
                <input
                  id={`${id}-${f}`}
                  type="number"
                  inputMode="numeric"
                  min={RANGES[f].min}
                  step={1}
                  value={draft[f]}
                  onChange={(e) => {
                    patch({ [f]: e.target.value } as Partial<Draft>);
                    setLimitErrors((x) => ({ ...x, [f]: undefined }));
                  }}
                  aria-invalid={limitErrors[f] ? true : undefined}
                  aria-describedby={`${id}-${f}-h${limitErrors[f] ? ` ${id}-${f}-e` : ""}`}
                  className={`${FIELD_INPUT} aria-[invalid=true]:border-[#8A1F1F]`}
                />
                <p id={`${id}-${f}-h`} className="text-[13px] text-(--muted)">
                  {hint}
                </p>
                {limitErrors[f] ? (
                  <p id={`${id}-${f}-e`} className="text-[13px] text-[#8A1F1F]">
                    {limitErrors[f]}
                  </p>
                ) : null}
              </div>
            ))}
          </div>
          {platform ? (
            <p className="rounded-xl bg-(--page) px-3.5 py-2.5 text-sm">
              {pq.enabled
                ? `Ліміт платформи: ${pq.monthlyTokens ? `${nf(pq.monthlyTokens)} токенів` : "токенів без ліміту"}, ${pq.monthlyRequests ? `${nf(pq.monthlyRequests)} запитів` : "запитів без ліміту"} на місяць. Діє: менше з двох.`
                : "Ключ платформи для вашої компанії вимкнено, тож ліміт платформи не діє."}
            </p>
          ) : null}
        </fieldset>

        {formError ? (
          <p ref={alertRef} tabIndex={-1} role="alert" className="lg:col-span-2 rounded-xl bg-[#FBE4E4] px-3.5 py-2.5 text-sm text-[#8A1F1F] outline-none">
            {formError}
          </p>
        ) : null}
        {saved ? (
          <p role="status" className="lg:col-span-2 rounded-xl bg-[#DDF3E6] px-3.5 py-2.5 text-sm text-[#145A32]">
            Налаштування ШІ збережено.
          </p>
        ) : null}
        {warnings.length > 0 ? (
          <div role="status" className="lg:col-span-2 rounded-xl bg-[#FFF1CC] px-3.5 py-2.5 text-sm text-[#5C3900]">
            <p className="font-semibold">Зверніть увагу</p>
            <ul className="m-0 mt-1 list-disc pl-5">
              {warnings.map((w) => (
                <li key={w.code}>{w.message}</li>
              ))}
            </ul>
          </div>
        ) : null}

        <div className="lg:col-span-2">
          <PrimaryButton type="submit" disabled={save.isPending || over}>
            {save.isPending ? "Зберігаємо…" : "Зберегти"}
          </PrimaryButton>
        </div>
      </form>

      <div className="flex flex-col gap-2">
        <h3 className="text-base font-semibold">Використання</h3>
        <AiUsageBlock roleLabels={roleLabels} />
      </div>
    </div>
  );
}

/** Картка «ШІ-асистент» на `/beauty/settings#ai` (§20.9): owner - усе, admin - без блоку ключів, specialist - нічого. */
export function AiSettingsSection() {
  const { user } = useAuth();
  const allowed = canEditAiSettings(user.role);
  const settings = useAiSettings(allowed);
  const models = useAiModels(allowed);
  if (!allowed) return null;
  return (
    <div id="ai" className="scroll-mt-4">
      <Card>
        <h2 className="mb-1 text-lg font-semibold">ШІ-асистент</h2>
        <p className="mb-4 text-sm text-(--muted)">Ключі, моделі, режим автономності, промпт і ліміти використання.</p>
        <QueryState
          isPending={settings.isPending || models.isPending}
          isError={settings.isError || models.isError}
          onRetry={() => {
            void settings.refetch();
            void models.refetch();
          }}
        >
          {settings.data && models.data ? (
            <AiSettingsBody settings={settings.data} catalog={models.data} canKey={canManageAiKey(user.role)} />
          ) : null}
        </QueryState>
      </Card>
    </div>
  );
}
