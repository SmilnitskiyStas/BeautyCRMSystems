"use client";

import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { useState, type FormEvent } from "react";
import { z } from "zod";
import { humanizeAuthError } from "../errors";
import { homeFor, safeNext } from "../permissions";
import { login } from "../session";
import type { Business, SessionUser } from "../types";
import { BusinessPicker } from "./business-picker";
import { AuthCard, FormAlert, SubmitButton, TextField } from "./auth-card";

/** Вхід за поштою й паролем (§31.2, без коду бізнесу). */
const schema = z.object({
  email: z.email("Вкажіть коректну пошту"),
  password: z.string().min(1, "Введіть пароль").max(128, "Не більше 128 символів"),
});

type Errors = Partial<Record<"email" | "password", string>>;
type Selection = { businesses: Business[]; ticketExpiresInSeconds: number };

export function LoginForm() {
  const router = useRouter();
  const params = useSearchParams();
  const [pending, setPending] = useState(false);
  const [errors, setErrors] = useState<Errors>({});
  const [formError, setFormError] = useState<string | null>(null);
  const [selection, setSelection] = useState<Selection | null>(null);

  const enter = (user: SessionUser) => router.replace(safeNext(params.get("next"), homeFor(user)));

  async function onSubmit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const fd = new FormData(e.currentTarget);
    const parsed = schema.safeParse({
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
      const result = await login(parsed.data);
      if (result.kind === "selection") {
        setSelection({ businesses: result.businesses, ticketExpiresInSeconds: result.ticketExpiresInSeconds });
        setPending(false);
        return;
      }
      enter(result.user);
    } catch (err) {
      setFormError(humanizeAuthError(err, "Не вдалося увійти. Спробуйте ще раз."));
      setPending(false);
    }
  }

  if (selection) {
    return (
      <BusinessPicker
        businesses={selection.businesses}
        ticketExpiresInSeconds={selection.ticketExpiresInSeconds}
        onSelected={enter}
        onBack={() => setSelection(null)}
      />
    );
  }

  return (
    <AuthCard title="Вхід у Beauty CRM" subtitle="Використайте пошту й пароль від свого облікового запису.">
      <form onSubmit={onSubmit} noValidate className="flex flex-col gap-4">
        <div aria-live="assertive">{formError ? <FormAlert>{formError}</FormAlert> : null}</div>
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
        <Link href="/forgot-password" className="inline-flex min-h-11 items-center justify-center text-sm font-semibold text-(--accent)">
          Забули пароль?
        </Link>
      </form>
    </AuthCard>
  );
}
