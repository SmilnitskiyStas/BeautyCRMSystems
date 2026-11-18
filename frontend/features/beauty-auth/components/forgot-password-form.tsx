"use client";

import Link from "next/link";
import { useState, type FormEvent } from "react";
import { z } from "zod";
import { BeautyApiError, humanizeAuthError, retryWaitText } from "../errors";
import { requestPasswordReset } from "../session";
import { AuthCard, FormAlert, SubmitButton, TextField } from "./auth-card";

const emailSchema = z.email("Вкажіть коректну пошту").max(320, "Не більше 320 символів");

/** `/forgot-password` (§31.7.1): відповідь однакова для будь-якої пошти, існування акаунта не розкриваємо. */
export function ForgotPasswordForm() {
  const [pending, setPending] = useState(false);
  const [emailError, setEmailError] = useState<string | undefined>();
  const [formError, setFormError] = useState<string | null>(null);
  const [done, setDone] = useState(false);

  async function onSubmit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const parsed = emailSchema.safeParse(String(new FormData(e.currentTarget).get("email") ?? "").trim());
    if (!parsed.success) {
      setEmailError(parsed.error.issues[0]?.message);
      setFormError(null);
      return;
    }
    setEmailError(undefined);
    setFormError(null);
    setPending(true);
    try {
      await requestPasswordReset(parsed.data);
      setDone(true);
    } catch (err) {
      setPending(false);
      if (err instanceof BeautyApiError && err.status === 429 && err.retryAfterSeconds) {
        return setFormError(`Забагато спроб. Спробуйте приблизно через ${retryWaitText(err.retryAfterSeconds)}`);
      }
      if (err instanceof BeautyApiError && err.code === "invalid_email") return setEmailError(humanizeAuthError(err));
      setFormError(humanizeAuthError(err, "Не вдалося надіслати запит. Спробуйте ще раз."));
    }
  }

  if (done) {
    return (
      <AuthCard title="Перевірте пошту">
        <div className="flex flex-col gap-4">
          <p role="status" className="text-sm text-(--muted)">
            Якщо ця пошта зареєстрована, ми надіслали лист із посиланням
          </p>
          <Link href="/login" className="inline-flex min-h-11 items-center justify-center text-sm font-semibold text-(--accent)">
            Повернутися до входу
          </Link>
        </div>
      </AuthCard>
    );
  }

  return (
    <AuthCard title="Скидання пароля" subtitle="Вкажіть пошту облікового запису: ми надішлемо посилання для нового пароля.">
      <form onSubmit={onSubmit} noValidate className="flex flex-col gap-4">
        <div aria-live="assertive">{formError ? <FormAlert>{formError}</FormAlert> : null}</div>
        <TextField id="email" name="email" type="email" label="Пошта" autoComplete="username" required error={emailError} />
        <SubmitButton pending={pending} pendingText="Надсилаємо…">
          Надіслати посилання
        </SubmitButton>
        <Link href="/login" className="inline-flex min-h-11 items-center justify-center text-sm font-semibold text-(--accent)">
          Назад до входу
        </Link>
      </form>
    </AuthCard>
  );
}
