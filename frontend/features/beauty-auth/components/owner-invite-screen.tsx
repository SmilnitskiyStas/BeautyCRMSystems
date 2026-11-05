"use client";

import { useRouter } from "next/navigation";
import { useCallback, useEffect, useId, useRef, useState, type FormEvent, type ReactNode } from "react";
import { BeautyApiError, humanizeError, retryWaitText } from "../errors";
import { inspectOwnerInvite, MAX_PASSWORD, MIN_PASSWORD, takeTokenFromHash, type OwnerInviteInfo } from "../owner-invite";
import { acceptOwnerInvite } from "../session";
import { AuthCard, FormAlert, SubmitButton } from "./auth-card";

type Phase =
  | { kind: "loading" }
  | { kind: "ready"; info: OwnerInviteInfo }
  | { kind: "invalid" }
  | { kind: "suspended" }
  | { kind: "limited"; wait: string }
  | { kind: "failed" };

function waitFor(e: BeautyApiError): string {
  return e.retryAfterSeconds ? retryWaitText(e.retryAfterSeconds) : "хвилину";
}

function phaseFromError(e: unknown): Phase {
  if (e instanceof BeautyApiError) {
    if (e.status === 410 || e.status === 404) return { kind: "invalid" };
    if (e.status === 403 && e.code === "tenant_suspended") return { kind: "suspended" };
    if (e.status === 429) return { kind: "limited", wait: waitFor(e) };
  }
  return { kind: "failed" };
}

/** Екран-результат: фокус переходить на заголовок, зміну озвучує role=status. */
function Notice({ title, children, action }: { title: string; children: ReactNode; action?: ReactNode }) {
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    ref.current?.focus();
  }, []);
  return (
    <AuthCard title={title}>
      <div ref={ref} tabIndex={-1} role="status" aria-live="polite" className="flex flex-col gap-4 text-sm text-(--muted) outline-none">
        {children}
        {action}
      </div>
    </AuthCard>
  );
}

const RETRY_BTN = "min-h-11 cursor-pointer rounded-[22px] border border-(--line-strong) bg-white px-5 text-sm font-semibold";

function PasswordField({
  id,
  label,
  hint,
  error,
  value,
  onChange,
  inputRef,
}: {
  id: string;
  label: string;
  hint?: string;
  error?: string;
  value: string;
  onChange: (v: string) => void;
  inputRef?: React.Ref<HTMLInputElement>;
}) {
  const [shown, setShown] = useState(false);
  const hintId = `${id}-hint`;
  const errId = `${id}-err`;
  return (
    <div className="flex flex-col gap-1.5">
      <label htmlFor={id} className="text-[13px] font-semibold">
        {label}
      </label>
      <div className="flex gap-2">
        <input
          id={id}
          ref={inputRef}
          name={id}
          type={shown ? "text" : "password"}
          autoComplete="new-password"
          autoCapitalize="none"
          spellCheck={false}
          required
          value={value}
          onChange={(e) => onChange(e.target.value)}
          aria-invalid={error ? true : undefined}
          aria-describedby={[hint ? hintId : "", error ? errId : ""].filter(Boolean).join(" ") || undefined}
          className="min-h-11 min-w-0 flex-1 rounded-xl border border-(--line-strong) bg-white px-3.5 text-[15px] text-(--ink) aria-[invalid=true]:border-[#8A1F1F]"
        />
        <button
          type="button"
          onClick={() => setShown((s) => !s)}
          aria-pressed={shown}
          aria-controls={id}
          className="min-h-11 shrink-0 cursor-pointer rounded-xl border border-(--line-strong) bg-white px-3 text-[13px] font-semibold"
        >
          {shown ? "Приховати" : "Показати"}
          <span className="sr-only"> пароль у полі «{label}»</span>
        </button>
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

/**
 * /invite для власника бізнесу (ADR-018, §27.8). Токен береться з фрагмента `#t=…`, одразу прибирається з адреси
 * і живе лише в ref: не в state/DOM, не в localStorage, логах чи query.
 */
export function OwnerInviteScreen() {
  const router = useRouter();
  const tokenRef = useRef<string | null | undefined>(undefined);
  const [phase, setPhase] = useState<Phase>({ kind: "loading" });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    // Читаємо фрагмент рівно один раз (повторний запуск ефекту в Strict Mode бачить уже порожню адресу).
    if (tokenRef.current === undefined) tokenRef.current = takeTokenFromHash();
    const token = tokenRef.current;
    if (!token) {
      // Асинхронно, щоб не викликати setState синхронно в ефекті.
      const id = setTimeout(() => setPhase({ kind: "invalid" }), 0);
      return () => clearTimeout(id);
    }
    let alive = true;
    inspectOwnerInvite(token)
      .then((info) => alive && setPhase({ kind: "ready", info }))
      .catch((e: unknown) => alive && setPhase(phaseFromError(e)));
    return () => {
      alive = false;
    };
  }, [attempt]);

  const retry = useCallback(() => {
    setPhase({ kind: "loading" });
    setAttempt((n) => n + 1);
  }, []);

  const onAccepted = useCallback(
    (home: string) => {
      tokenRef.current = null;
      router.replace(home);
    },
    [router],
  );

  switch (phase.kind) {
    case "loading":
      return (
        <AuthCard title="Запрошення в Beauty CRM">
          <p role="status" aria-live="polite" className="text-sm text-(--muted)">
            Перевіряємо посилання…
          </p>
        </AuthCard>
      );
    case "invalid":
      return (
        <Notice title="Посилання недійсне або застаріле">
          <p>Попросіть нове запрошення в тих, хто підключав ваш бізнес до Beauty CRM.</p>
        </Notice>
      );
    case "suspended":
      return (
        <Notice title="Зараз це недоступно">
          <p>Роботу цього бізнесу тимчасово призупинено. Зверніться до постачальника послуги.</p>
        </Notice>
      );
    case "limited":
      return (
        <Notice
          title="Забагато спроб"
          action={
            <button type="button" className={RETRY_BTN} onClick={retry}>
              Спробувати ще раз
            </button>
          }
        >
          <p>Зачекайте {phase.wait} і спробуйте ще раз.</p>
        </Notice>
      );
    case "failed":
      return (
        <Notice
          title="Не вдалося перевірити посилання"
          action={
            <button type="button" className={RETRY_BTN} onClick={retry}>
              Повторити
            </button>
          }
        >
          <p>Сервер недоступний або тимчасово не відповідає. Перевірте з&apos;єднання й повторіть.</p>
        </Notice>
      );
    case "ready":
      return <SetPasswordForm info={phase.info} getToken={() => tokenRef.current ?? null} onAccepted={onAccepted} onTerminal={setPhase} />;
  }
}

function SetPasswordForm({
  info,
  getToken,
  onAccepted,
  onTerminal,
}: {
  info: OwnerInviteInfo;
  getToken: () => string | null;
  onAccepted: (home: string) => void;
  onTerminal: (p: Phase) => void;
}) {
  const uid = useId();
  const [password, setPassword] = useState("");
  const [confirm, setConfirm] = useState("");
  const [errors, setErrors] = useState<{ password?: string; confirm?: string }>({});
  const [formError, setFormError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);
  const pwRef = useRef<HTMLInputElement>(null);
  const confirmRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    pwRef.current?.focus();
  }, []);

  async function onSubmit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const next: typeof errors = {};
    if (password.length < MIN_PASSWORD) next.password = `Щонайменше ${MIN_PASSWORD} символів.`;
    else if (password.length > MAX_PASSWORD) next.password = `Не більше ${MAX_PASSWORD} символів.`;
    if (confirm !== password) next.confirm = "Паролі не збігаються.";
    setErrors(next);
    setFormError(null);
    if (next.password) return void pwRef.current?.focus();
    if (next.confirm) return void confirmRef.current?.focus();
    const token = getToken();
    if (!token) return onTerminal({ kind: "invalid" });

    setPending(true);
    try {
      const user = await acceptOwnerInvite({ token, password });
      onAccepted(user.role === "specialist" ? "/beauty/calendar" : "/beauty");
    } catch (err) {
      setPending(false);
      if (err instanceof BeautyApiError) {
        if (err.status === 410 || err.status === 404 || (err.status === 403 && err.code === "tenant_suspended")) {
          return onTerminal(phaseFromError(err));
        }
        if (err.status === 422) {
          setErrors({ password: `Пароль не підходить: потрібно від ${MIN_PASSWORD} до ${MAX_PASSWORD} символів.` });
          pwRef.current?.focus();
          return;
        }
        if (err.status === 429) {
          setFormError(`Забагато спроб. Зачекайте ${waitFor(err)} і спробуйте ще раз.`);
          return;
        }
      }
      setFormError(humanizeError(err, "Не вдалося встановити пароль. Спробуйте ще раз."));
    }
  }

  const longEnough = password.length >= MIN_PASSWORD;
  return (
    <AuthCard title="Створіть пароль" subtitle="Це запрошення власника бізнесу в Beauty CRM.">
      <dl className="grid gap-1 rounded-xl bg-(--bg,#f6f3f0) px-3.5 py-3 text-sm">
        <div className="flex flex-wrap gap-x-2">
          <dt className="text-(--muted)">Бізнес:</dt>
          <dd className="m-0 font-semibold">{info.tenantName}</dd>
        </div>
        <div className="flex flex-wrap gap-x-2">
          <dt className="text-(--muted)">Пошта для входу:</dt>
          <dd className="m-0 font-semibold">{info.email}</dd>
        </div>
      </dl>
      <form onSubmit={onSubmit} noValidate className="flex flex-col gap-4" aria-describedby={formError ? `${uid}-form-err` : undefined}>
        <div id={`${uid}-form-err`} aria-live="assertive">
          {formError ? <FormAlert>{formError}</FormAlert> : null}
        </div>
        <PasswordField
          id={`${uid}-password`}
          label="Пароль"
          hint={`Від ${MIN_PASSWORD} до ${MAX_PASSWORD} символів. Краще довга фраза, яку легко запам'ятати.`}
          error={errors.password}
          value={password}
          onChange={setPassword}
          inputRef={pwRef}
        />
        <p aria-live="polite" className={`-mt-2 text-[13px] ${longEnough ? "text-[#1F6B3A]" : "text-(--muted)"}`}>
          {longEnough ? "Довжина достатня." : `Ще ${MIN_PASSWORD - password.length} символів до мінімуму.`}
        </p>
        <PasswordField
          id={`${uid}-confirm`}
          label="Повторіть пароль"
          error={errors.confirm}
          value={confirm}
          onChange={setConfirm}
          inputRef={confirmRef}
        />
        <SubmitButton pending={pending} pendingText="Зберігаємо…">
          Встановити пароль і увійти
        </SubmitButton>
      </form>
    </AuthCard>
  );
}
