"use client";

import { useRouter, useSearchParams } from "next/navigation";
import { useState, type FormEvent } from "react";
import { z } from "zod";
import { humanizeError } from "../errors";
import { safeNext } from "../permissions";
import { login } from "../session";
import { AuthCard, FormAlert, SubmitButton, TextField } from "./auth-card";

const schema = z.object({
  tenant: z.string().trim().min(1, "Вкажіть код бізнесу").max(64, "Не більше 64 символів"),
  email: z.email("Вкажіть коректну пошту"),
  password: z.string().min(1, "Введіть пароль").max(128, "Не більше 128 символів"),
});

type Errors = Partial<Record<"tenant" | "email" | "password", string>>;

export function LoginForm() {
  const router = useRouter();
  const params = useSearchParams();
  const [pending, setPending] = useState(false);
  const [errors, setErrors] = useState<Errors>({});
  const [formError, setFormError] = useState<string | null>(null);

  async function onSubmit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const fd = new FormData(e.currentTarget);
    const parsed = schema.safeParse({
      tenant: fd.get("tenant"),
      email: String(fd.get("email") ?? "").trim(),
      password: fd.get("password"),
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
      const user = await login({ ...parsed.data, tenant: parsed.data.tenant.toLowerCase() });
      router.replace(safeNext(params.get("next"), user.role === "specialist" ? "/beauty/calendar" : "/beauty"));
    } catch (err) {
      setFormError(humanizeError(err, "Не вдалося увійти. Спробуйте ще раз."));
      setPending(false);
    }
  }

  return (
    <AuthCard title="Вхід у Beauty CRM" subtitle="Використайте код бізнесу, пошту й пароль із запрошення.">
      <form onSubmit={onSubmit} noValidate className="flex flex-col gap-4">
        {formError ? <FormAlert>{formError}</FormAlert> : null}
        <TextField
          id="tenant"
          name="tenant"
          label="Код бізнесу"
          hint="Латиницею, наприклад beauty-lab"
          autoComplete="organization"
          autoCapitalize="none"
          spellCheck={false}
          required
          error={errors.tenant}
        />
        <TextField id="email" name="email" type="email" label="Пошта" autoComplete="username" required error={errors.email} />
        <TextField
          id="password"
          name="password"
          type="password"
          label="Пароль"
          autoComplete="current-password"
          required
          error={errors.password}
        />
        <SubmitButton pending={pending} pendingText="Входимо…">
          Увійти
        </SubmitButton>
      </form>
    </AuthCard>
  );
}
