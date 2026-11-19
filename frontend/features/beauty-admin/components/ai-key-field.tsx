"use client";

import { useEffect, useId, useRef, useState, type KeyboardEvent } from "react";
import { BeautyApiError } from "@/features/beauty-auth/errors";
import { useAiKeyActions } from "../hooks/use-beauty-admin";
import { aiErrorText, dmy, testReasonText } from "../ai-settings/messages";
import type { AiKeyInfo, AiKeySource, AiRoleId } from "../types";
import { AiConfirmDialog } from "./ai-confirm-dialog";
import { FIELD_INPUT, FIELD_LABEL, PrimaryButton, SecondaryButton } from "./ui";

type Pending = { kind: "replace" | "delete" | "save"; roles: AiRoleId[] };

const isConflict = (e: unknown): e is BeautyApiError => e instanceof BeautyApiError && e.status === 409 && e.code === "key_affects_other_roles";
const toRoles = (v: string[] | undefined, all: AiRoleId[]): AiRoleId[] => all.filter((r) => v?.includes(r));

/**
 * Ключ API провайдера одного сценарію (§20.9, owner). Секрет живе лише в стані поля й в аргументі запиту:
 * не в URL/query, storage, React Query cache, логах чи повідомленнях помилок. Після відправки поле очищається
 * (крім 409, коли запит чекає підтвердження залежних сценаріїв).
 */
export function AiKeyField({
  role,
  roleLabel,
  roleLabels,
  providerId,
  providerLabel,
  info,
  keySource,
  otherRole,
  otherDraftSameProvider,
}: {
  role: AiRoleId;
  roleLabel: string;
  roleLabels: Record<AiRoleId, string>;
  providerId: string;
  providerLabel: string;
  info: AiKeyInfo | undefined;
  keySource: AiKeySource;
  otherRole: AiRoleId;
  /** Інший сценарій у чернетці стоїть на тому самому провайдері. */
  otherDraftSameProvider: boolean;
}) {
  const id = useId();
  const actions = useAiKeyActions();
  const [value, setValue] = useState("");
  const [editing, setEditing] = useState(false);
  const [ack, setAck] = useState(false);
  const [pending, setPending] = useState<Pending | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const input = useRef<HTMLInputElement>(null);
  const replaceBtn = useRef<HTMLButtonElement>(null);
  const deleteBtn = useRef<HTMLButtonElement>(null);
  const saveBtn = useRef<HTMLButtonElement>(null);
  const [focusInput, setFocusInput] = useState(0);

  const hasKey = !!info?.hasKey;
  const sharedWith = (info?.usedByRoles ?? []).filter((r) => r !== role);
  const other = roleLabels[otherRole];
  const showInput = !hasKey || editing;
  const inputId = `${id}-key`;
  const descIds = [`${id}-hint`, error ? `${id}-err` : ""].filter(Boolean).join(" ");

  useEffect(() => {
    if (focusInput > 0) input.current?.focus();
  }, [focusInput, showInput]);

  const names = (roles: AiRoleId[]) => roles.map((r) => `«${roleLabels[r]}»`).join(", ");

  async function submit(acknowledge: boolean) {
    const key = value.trim();
    if (!key) {
      setError("Введіть ключ API.");
      setFocusInput((n) => n + 1);
      return;
    }
    setError(null);
    setNotice(null);
    try {
      await actions.save({ provider: providerId, apiKey: key, forRole: role, acknowledgeAffectedRoles: acknowledge || undefined });
      setValue("");
      setEditing(false);
      setAck(false);
      setPending(null);
      setNotice("Ключ збережено.");
    } catch (e) {
      if (isConflict(e)) {
        setPending({ kind: "save", roles: toRoles(e.affectedRoles, ["chat", "analytics"]) });
        return;
      }
      setValue("");
      setPending(null);
      setError(aiErrorText(e, "Не вдалося зберегти ключ. Спробуйте ще раз."));
      setFocusInput((n) => n + 1);
    }
  }

  async function remove(acknowledge: boolean) {
    setError(null);
    setNotice(null);
    try {
      await actions.remove({ provider: providerId, forRole: role, acknowledgeAffectedRoles: acknowledge || undefined });
      setPending(null);
      setEditing(false);
      setAck(false);
      setNotice("Ключ видалено.");
      setFocusInput((n) => n + 1);
    } catch (e) {
      if (isConflict(e)) {
        setPending({ kind: "delete", roles: toRoles(e.affectedRoles, ["chat", "analytics"]) });
        return;
      }
      setPending(null);
      setError(aiErrorText(e, "Не вдалося видалити ключ. Спробуйте ще раз."));
    }
  }

  async function check() {
    setError(null);
    setNotice(null);
    const candidate = value.trim();
    try {
      const r = await actions.test(providerId, candidate || undefined);
      if (r.ok) setNotice("Ключ прийнято провайдером.");
      else setError(testReasonText(r.reason));
    } catch (e) {
      setError(aiErrorText(e, "Не вдалося перевірити ключ. Спробуйте ще раз."));
    }
  }

  const cancelDialog = () => {
    const kind = pending?.kind;
    setPending(null);
    // Фокус повертається на кнопку-тригер.
    queueMicrotask(() => (kind === "delete" ? deleteBtn : kind === "save" ? saveBtn : replaceBtn).current?.focus());
  };

  const onEnter = (e: KeyboardEvent<HTMLInputElement>) => {
    // Enter у полі ключа не повинен відправляти форму налаштувань: він зберігає лише ключ.
    if (e.key === "Enter") {
      e.preventDefault();
      if (!actions.busy) void submit(ack);
    }
  };

  const onReplace = () => {
    if (sharedWith.length > 0) setPending({ kind: "replace", roles: sharedWith });
    else {
      setEditing(true);
      setFocusInput((n) => n + 1);
    }
  };

  if (keySource === "platform") {
    return (
      <div className="flex flex-col gap-1.5 rounded-xl bg-(--page) p-3.5">
        <label htmlFor={inputId} className={FIELD_LABEL}>
          Ключ API {providerLabel}
        </label>
        <input id={inputId} type="password" autoComplete="off" spellCheck={false} autoCapitalize="off" disabled aria-describedby={`${id}-hint`} value="" readOnly className={`${FIELD_INPUT} disabled:bg-white disabled:text-(--muted)`} />
        <p id={`${id}-hint`} className="text-[13px] text-(--muted)">
          Використовується ключ платформи.
          {hasKey && info?.last4 ? ` Ваш власний ключ ${providerLabel} …${info.last4} збережено, але зараз не використовується.` : ""}
        </p>
      </div>
    );
  }

  const dialogText = (p: Pending) => {
    const who = names(p.roles);
    if (p.kind === "delete")
      return p.roles.length > 0
        ? `Цей ключ також використовує ${who}. Після видалення обидва сценарії перестануть працювати, доки ви не додасте ключ.`
        : `Після видалення сценарій «${roleLabel}» перестане працювати, доки ви не додасте ключ.`;
    return `Цей ключ також використовує ${who}. Після заміни зміна торкнеться обох сценаріїв.`;
  };

  return (
    <div className="flex flex-col gap-2.5 rounded-xl bg-(--page) p-3.5" data-key-provider={providerId}>
      {showInput ? (
        <>
          <label htmlFor={inputId} className={FIELD_LABEL}>
            Ключ API {providerLabel}
          </label>
          <input
            id={inputId}
            ref={input}
            type="password"
            autoComplete="new-password"
            autoCapitalize="off"
            spellCheck={false}
            data-1p-ignore
            data-lpignore="true"
            value={value}
            disabled={actions.busy}
            onChange={(e) => {
              setValue(e.target.value);
              setError(null);
            }}
            onKeyDown={onEnter}
            aria-invalid={error ? true : undefined}
            aria-describedby={descIds}
            className={`${FIELD_INPUT} aria-[invalid=true]:border-[#8A1F1F]`}
          />
          <p id={`${id}-hint`} className="text-[13px] text-(--muted)">
            Ключ зберігається зашифрованим і більше не показується; після збереження видно лише останні 4 символи.
            {!hasKey && otherDraftSameProvider ? ` Цей ключ буде використано й для «${other}».` : ""}
            {ack && editing ? ` Заміна торкнеться й «${other}».` : ""}
          </p>
          <div className="flex flex-wrap gap-2">
            <SecondaryButton disabled={actions.busy || (!value.trim() && !hasKey)} onClick={() => void check()}>
              Перевірити
            </SecondaryButton>
            <PrimaryButton ref={saveBtn} disabled={actions.busy || !value.trim()} onClick={() => void submit(ack)}>
              {actions.busy ? "Зачекайте…" : "Зберегти ключ"}
            </PrimaryButton>
            {hasKey && editing ? (
              <SecondaryButton
                disabled={actions.busy}
                onClick={() => {
                  setValue("");
                  setEditing(false);
                  setAck(false);
                  setError(null);
                  queueMicrotask(() => replaceBtn.current?.focus());
                }}
              >
                Скасувати заміну
              </SecondaryButton>
            ) : null}
          </div>
        </>
      ) : (
        <>
          <p id={`${id}-hint`} className="text-sm">
            {sharedWith.length > 0
              ? `Використовується той самий ключ ${providerLabel} …${info?.last4 ?? ""} (спільний із ${names(sharedWith)})`
              : `Ключ збережено: …${info?.last4 ?? ""}${info?.updatedAt ? ` · змінено ${dmy(info.updatedAt)}` : ""}`}
          </p>
          <div className="flex flex-wrap gap-2">
            <SecondaryButton ref={replaceBtn} disabled={actions.busy} onClick={onReplace}>
              Замінити
            </SecondaryButton>
            <SecondaryButton ref={deleteBtn} disabled={actions.busy} onClick={() => setPending({ kind: "delete", roles: sharedWith })}>
              Видалити
            </SecondaryButton>
            <SecondaryButton disabled={actions.busy} onClick={() => void check()}>
              Перевірити
            </SecondaryButton>
          </div>
        </>
      )}

      {pending ? (
        <AiConfirmDialog
          title={pending.kind === "delete" ? "Видалити ключ?" : "Ключ використовується ще одним сценарієм"}
          confirmLabel={pending.kind === "delete" ? "Так, видалити ключ" : pending.kind === "save" ? "Так, замінити ключ" : "Так, замінити"}
          danger={pending.kind === "delete"}
          busy={actions.busy}
          onCancel={cancelDialog}
          onConfirm={() => {
            if (pending.kind === "delete") void remove(pending.roles.length > 0);
            else if (pending.kind === "save") void submit(true);
            else {
              setPending(null);
              setAck(true);
              setEditing(true);
              setFocusInput((n) => n + 1);
            }
          }}
        >
          <p>{dialogText(pending)}</p>
        </AiConfirmDialog>
      ) : null}

      {error ? (
        <p id={`${id}-err`} role="alert" className="text-[13px] text-[#8A1F1F]">
          {error}
        </p>
      ) : null}
      {notice ? (
        <p role="status" className="text-[13px] text-[#145A32]">
          {notice}
        </p>
      ) : null}
    </div>
  );
}
