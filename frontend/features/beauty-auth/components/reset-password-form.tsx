"use client";

import Link from "next/link";
import { useEffect, useRef, useState, type FormEvent } from "react";
import { BeautyApiError, humanizeError } from "../errors";
import { MAX_PASSWORD, MIN_PASSWORD, takeTokenFromHash } from "../owner-invite";
import { resetPassword } from "../session";
import { AuthCard, FormAlert, SubmitButton, TextField } from "./auth-card";

type Phase = "loading" | "ready" | "invalid" | "done";

/**
 * `/reset-password#t=…` (§31.7.2, §31.11). Токен читається з фрагмента один раз, одразу прибирається з адреси
 * (`history.replaceState`) і живе лише в ref. Після успіху автоматичного входу немає.
 */
export function ResetPasswordForm() {
  const tokenRef = useRef<string | null | undefined>(undefined);
  const [phase, setPhase] = useState<Phase>("loading");
  const [errors, setErrors] = useState<{ password?: string; confirm?: string }>({});
  const [formError, setFormError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);

  useEffect(() => {
    if (tokenRef.current === undefined) tokenRef.current = takeTokenFromHash();
    const id = setTimeout(() => setPhase(tokenRef.current ? "ready" : "invalid"), 0);
    return () => clearTimeout(id);
  }, []);

  async function onSubmit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const fd = new FormData(e.currentTarget);
    const password = String(fd.get("password") ?? "");
    const confirm = String(fd.get("confirm") ?? "");
    const next: typeof errors = {};
    if (password.length < MIN_PASSWORD) next.password = `Щонайменше ${MIN_PASSWORD} символів`;
    else if (password.length > MAX_PASSWORD) next.password = `Не більше ${MAX_PASSWORD} символів`;
    if (confirm !== password) next.confirm = "Паролі не збігаються";
    setErrors(next);
    setFormError(null);
    if (next.password || next.confirm) return;
    const token = tokenRef.current;
    if (!token) return setPhase("invalid");

    setPending(true);
    try {
      await resetPassword({ token, password });
      tokenRef.current = null;
      setPhase("done");
    } catch (err) {
      setPending(false);
      if (err instanceof BeautyApiError && err.code === "reset_invalid") {
        tokenRef.current = null;
        return setPhase("invalid");
      }
      if (err instanceof BeautyApiError && err.code === "weak_password") return setErrors({ password: humanizeError(err) });
      setFormError(humanizeError(err, "Не вдалося змінити пароль. Спробуйте ще раз."));
    }
  }

  const linkCls = "inline-flex min-h-11 items-center justify-center rounded-[22px] bg-(--accent) px-5 text-sm font-semibold text-white";

  if (phase === "loading") {
    return (
      <AuthCard title="Новий пароль">
        <p role="status" className="text-sm text-(--muted)">
          Перевіряємо посилання…
        </p>
      </AuthCard>
    );
  }
  if (phase === "invalid") {
    return (
      <AuthCard title="Посилання недійсне">
        <div className="flex flex-col gap-4">
          <p role="alert" className="text-sm text-(--muted)">
            {humanizeError(new BeautyApiError(410, "reset_invalid", ""))}
          </p>
          <Link href="/forgot-password" className={linkCls}>
            Запросити нове посилання
          </Link>
        </div>
      </AuthCard>
    );
  }
  if (phase === "done") {
    return (
      <AuthCard title="Пароль змінено">
        <div className="flex flex-col gap-4">
          <p role="status" className="text-sm text-(--muted)">
            Тепер увійдіть із новим паролем.
          </p>
          <Link href="/login" className={linkCls}>
            Перейти до входу
          </Link>
        </div>
      </AuthCard>
    );
  }
  return (
    <AuthCard title="Новий пароль" subtitle={`Від ${MIN_PASSWORD} до ${MAX_PASSWORD} символів.`}>
      <form onSubmit={onSubmit} noValidate className="flex flex-col gap-4">
        <div aria-live="assertive">{formError ? <FormAlert>{formError}</FormAlert> : null}</div>
        <TextField id="password" name="password" type="password" label="Пароль" autoComplete="new-password" required error={errors.password} />
        <TextField id="confirm" name="confirm" type="password" label="Повторіть пароль" autoComplete="new-password" required error={errors.confirm} />
        <SubmitButton pending={pending} pendingText="Зберігаємо…">
          Змінити пароль
        </SubmitButton>
      </form>
    </AuthCard>
  );
}
