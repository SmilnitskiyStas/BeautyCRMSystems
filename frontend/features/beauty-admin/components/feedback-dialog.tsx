"use client";

import { useId, useRef, useState, type FormEvent } from "react";
import { humanizeFeedbackError } from "@/features/beauty-auth/errors";
import {
  buildFeedbackInput,
  DEFAULT_SEVERITY,
  FEEDBACK_DATA_NOTICE,
  FEEDBACK_MAX,
  FEEDBACK_MIN,
  FEEDBACK_SEVERITIES,
  FEEDBACK_TYPES,
  messageLength,
  validateFeedbackMessage,
} from "../feedback";
import { useSendFeedback } from "../hooks/use-feedback";
import type { FeedbackSeverity, FeedbackType } from "../types";
import { ModalDialog } from "./modal-dialog";
import { ErrorBanner } from "./staff-parts";
import { FIELD_INPUT, FIELD_LABEL, PrimaryButton, SecondaryButton } from "./ui";

/**
 * Звернення до розробників (§27.9): співпраця / покращити / не працює. Доступне всім ролям персоналу.
 * Текст і вибір зберігаються при помилці (429, мережа), форма очищується лише після `202`.
 */
export function FeedbackDialog({ onClose }: { onClose: () => void }) {
  const send = useSendFeedback();
  const formId = useId();
  const messageId = useId();
  const severityId = useId();
  const counterId = useId();
  const errorId = useId();
  const area = useRef<HTMLTextAreaElement>(null);
  const inFlight = useRef(false);
  const [type, setType] = useState<FeedbackType>("improvement");
  const [severity, setSeverity] = useState<FeedbackSeverity>(DEFAULT_SEVERITY);
  const [message, setMessage] = useState("");
  const [fieldError, setFieldError] = useState<string | undefined>();
  const [serverError, setServerError] = useState<string | null>(null);
  const [done, setDone] = useState(false);

  const length = messageLength(message);
  const over = length > FEEDBACK_MAX;

  async function submit(e: FormEvent) {
    e.preventDefault();
    if (send.isPending || inFlight.current) return;
    setServerError(null);
    setDone(false);
    const err = validateFeedbackMessage(message);
    setFieldError(err);
    if (err) {
      area.current?.focus();
      return;
    }
    inFlight.current = true;
    try {
      await send.mutateAsync(buildFeedbackInput({ type, severity, message }, window.location.pathname));
      setMessage("");
      setSeverity(DEFAULT_SEVERITY);
      setDone(true);
    } catch (e2) {
      setServerError(humanizeFeedbackError(e2));
    } finally {
      inFlight.current = false;
    }
  }

  return (
    <ModalDialog
      title="Звернення до розробників"
      onClose={onClose}
      busy={send.isPending}
      actions={
        <>
          <PrimaryButton type="submit" form={formId} aria-disabled={send.isPending || undefined} className="aria-disabled:cursor-not-allowed aria-disabled:opacity-60">
            {send.isPending ? "Надсилаємо…" : "Надіслати"}
          </PrimaryButton>
          <SecondaryButton onClick={onClose} disabled={send.isPending}>
            Закрити
          </SecondaryButton>
        </>
      }
    >
      <form id={formId} onSubmit={(e) => void submit(e)} noValidate className="flex flex-col gap-4">
        <fieldset className="m-0 flex min-w-0 flex-col gap-1.5 border-0 p-0">
          <legend className={`${FIELD_LABEL} mb-1`}>Про що звернення</legend>
          {FEEDBACK_TYPES.map((t) => (
            <label key={t.id} className="flex min-h-11 cursor-pointer items-start gap-2.5 rounded-xl border border-(--line-strong) px-3 py-2.5 has-[:checked]:border-(--accent) has-[:checked]:bg-(--tint)">
              <input
                type="radio"
                name={`${formId}-type`}
                value={t.id}
                checked={type === t.id}
                onChange={() => setType(t.id)}
                className="mt-0.5 size-5 accent-(--accent)"
              />
              <span className="flex flex-col">
                <span className="text-[15px] font-semibold">{t.label}</span>
                <span className="text-[13px] text-(--muted)">{t.hint}</span>
              </span>
            </label>
          ))}
        </fieldset>

        {type === "bug" ? (
          <div className="flex flex-col gap-1.5">
            <label htmlFor={severityId} className={FIELD_LABEL}>
              Наскільки це критично
            </label>
            <select id={severityId} value={severity} onChange={(e) => setSeverity(e.target.value as FeedbackSeverity)} className={FIELD_INPUT}>
              {FEEDBACK_SEVERITIES.map((s) => (
                <option key={s.id} value={s.id}>
                  {s.label}
                </option>
              ))}
            </select>
          </div>
        ) : null}

        <div className="flex flex-col gap-1.5">
          <label htmlFor={messageId} className={FIELD_LABEL}>
            Опишіть деталі <span aria-hidden="true">*</span>
          </label>
          <textarea
            id={messageId}
            ref={area}
            rows={6}
            required
            value={message}
            onChange={(e) => {
              setMessage(e.target.value);
              if (fieldError) setFieldError(undefined);
              if (done) setDone(false);
            }}
            aria-invalid={fieldError ? true : undefined}
            aria-describedby={fieldError ? `${counterId} ${errorId}` : counterId}
            className={`${FIELD_INPUT} py-2.5 leading-snug`}
          />
          <div className="flex flex-wrap justify-between gap-2 text-[13px]">
            <span id={counterId} data-testid="feedback-counter" className={over ? "font-semibold text-[#8A1F1F]" : "text-(--muted)"}>
              {length} / {FEEDBACK_MAX} (мінімум {FEEDBACK_MIN})
            </span>
          </div>
          {fieldError ? (
            <p id={errorId} className="text-[13px] text-[#8A1F1F]">
              {fieldError}
            </p>
          ) : null}
        </div>

        <p data-testid="feedback-data-notice" data-placeholder="q8" className="rounded-xl border border-dashed border-(--line-strong) px-3 py-2.5 text-[13px] text-(--muted)">
          {FEEDBACK_DATA_NOTICE}
        </p>

        {serverError ? <ErrorBanner>{serverError}</ErrorBanner> : null}
        {/* Постійний контейнер: оголошення результату не губиться. */}
        <div role="status" aria-live="polite" className={done || send.isPending ? "rounded-xl bg-[#DDF3E6] px-3.5 py-2.5 text-sm text-[#145A32]" : "sr-only"}>
          {send.isPending ? "Надсилаємо звернення…" : done ? "Дякуємо! Звернення надіслано розробникам." : ""}
        </div>
      </form>
    </ModalDialog>
  );
}
