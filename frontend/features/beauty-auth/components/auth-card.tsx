import type { InputHTMLAttributes, ReactNode } from "react";

/** Спільна оболонка екранів входу / запрошення. */
export function AuthCard({ title, subtitle, children }: { title: string; subtitle?: string; children: ReactNode }) {
  return (
    <main className="flex min-h-screen items-center justify-center px-5 py-10">
      <div className="flex w-full max-w-[440px] flex-col gap-6 rounded-[20px] bg-white p-7 sm:p-9">
        <div>
          <div className="beauty-display mb-5 text-lg font-semibold text-(--accent)">Beauty Lab</div>
          <h1 className="beauty-display text-2xl font-semibold">{title}</h1>
          {subtitle ? <p className="mt-2 text-sm text-(--muted)">{subtitle}</p> : null}
        </div>
        {children}
      </div>
    </main>
  );
}

const INPUT =
  "min-h-11 w-full rounded-xl border border-(--line-strong) bg-white px-3.5 text-[15px] text-(--ink) aria-[invalid=true]:border-[#8A1F1F]";

export function TextField({
  id,
  label,
  hint,
  error,
  ...rest
}: {
  id: string;
  label: string;
  hint?: string;
  error?: string | null;
} & Omit<InputHTMLAttributes<HTMLInputElement>, "id">) {
  const hintId = `${id}-hint`;
  const errId = `${id}-err`;
  return (
    <div className="flex flex-col gap-1.5">
      <label htmlFor={id} className="text-[13px] font-semibold">
        {label}
      </label>
      <input
        id={id}
        {...rest}
        aria-invalid={error ? true : undefined}
        aria-describedby={[hint ? hintId : "", error ? errId : ""].filter(Boolean).join(" ") || undefined}
        className={INPUT}
      />
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

export function SubmitButton({ pending, children, pendingText }: { pending: boolean; children: ReactNode; pendingText: string }) {
  return (
    <button
      type="submit"
      disabled={pending}
      className="min-h-11 cursor-pointer rounded-[22px] border-0 bg-(--accent) px-5 text-sm font-semibold text-white disabled:cursor-not-allowed disabled:opacity-60"
    >
      {pending ? pendingText : children}
    </button>
  );
}

export function FormAlert({ children }: { children: ReactNode }) {
  return (
    <p role="alert" className="rounded-xl bg-[#FBE4E4] px-3.5 py-2.5 text-sm text-[#8A1F1F]">
      {children}
    </p>
  );
}
