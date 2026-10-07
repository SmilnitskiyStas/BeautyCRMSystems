"use client";

import Link from "next/link";
import { useState, type FormEvent } from "react";
import { z } from "zod";
import { BeautyApiError, humanizeError, readApiError } from "../errors";
import { AuthCard, FormAlert, SubmitButton, TextField } from "./auth-card";

const MIN_PASSWORD = 12;

const schema = z
  .object({
    fullName: z.string().trim().min(1, "Вкажіть ім'я").max(200, "Не більше 200 символів"),
    password: z
      .string()
      .min(MIN_PASSWORD, `Щонайменше ${MIN_PASSWORD} символів`)
      .max(128, "Не більше 128 символів")
      .regex(/\p{L}/u, "Додайте літери")
      .regex(/\d/, "Додайте цифри"),
    confirm: z.string(),
  })
  .refine((v) => v.password === v.confirm, { path: ["confirm"], message: "Паролі не збігаються" });

type Errors = Partial<Record<"fullName" | "password" | "confirm", string>>;

export function AcceptInviteForm({ token }: { token: string }) {
  const [pending, setPending] = useState(false);
  const [errors, setErrors] = useState<Errors>({});
  const [formError, setFormError] = useState<string | null>(null);
  const [done, setDone] = useState(false);

  async function onSubmit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const fd = new FormData(e.currentTarget);
    const parsed = schema.safeParse({
      fullName: fd.get("fullName"),
      password: fd.get("password"),
      confirm: fd.get("confirm"),
    });
    if (!parsed.success) {
      const next: Errors = {};
      for (const issue of parsed.error.issues) {
        const key = issue.path[0] as keyof Errors;
        next[key] ??= issue.message;
      }
      setErrors(next);
      setFormError(null);
      return;
    }
    setErrors({});
    setFormError(null);
    setPending(true);
    try {
      let res: Response;
      try {
        res = await fetch("/api/session/invite", {
          method: "POST",
          credentials: "same-origin",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ token, fullName: parsed.data.fullName, password: parsed.data.password }),
        });
      } catch {
        throw new BeautyApiError(0, "api_unreachable", "network");
      }
      if (!res.ok) throw await readApiError(res);
      setDone(true);
    } catch (err) {
      setFormError(humanizeError(err, "Не вдалося прийняти запрошення. Спробуйте ще раз."));
      setPending(false);
    }
  }

  if (done) {
    return (
      <AuthCard title="Пароль встановлено" subtitle="Тепер можна увійти в Beauty CRM.">
        <Link
          href="/login"
          className="inline-flex min-h-11 items-center justify-center rounded-[22px] bg-(--accent) px-5 text-sm font-semibold text-white no-underline"
        >
          Перейти до входу
        </Link>
      </AuthCard>
    );
  }

  return (
    <AuthCard title="Прийняття запрошення" subtitle="Вкажіть ім'я й придумайте пароль, щоб завершити реєстрацію.">
      <form onSubmit={onSubmit} noValidate className="flex flex-col gap-4">
        {formError ? <FormAlert>{formError}</FormAlert> : null}
        <TextField id="fullName" name="fullName" label="Ім'я та прізвище" autoComplete="name" required error={errors.fullName} />
        <TextField
          id="password"
          name="password"
          type="password"
          label="Пароль"
          hint={`Щонайменше ${MIN_PASSWORD} символів, літери й цифри.`}
          autoComplete="new-password"
          required
          error={errors.password}
        />
        <TextField
          id="confirm"
          name="confirm"
          type="password"
          label="Повторіть пароль"
          autoComplete="new-password"
          required
          error={errors.confirm}
        />
        <SubmitButton pending={pending} pendingText="Зберігаємо…">
          Встановити пароль
        </SubmitButton>
      </form>
    </AuthCard>
  );
}
