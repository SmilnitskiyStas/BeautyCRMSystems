"use client";

import { useEffect, useRef, useState } from "react";
import { BeautyApiError, humanizeAuthError } from "../errors";
import { selectBusiness } from "../session";
import { ROLE_LABEL, type Business, type SessionUser } from "../types";
import { AuthCard, FormAlert } from "./auth-card";

/** Останній обраний бізнес (лише `tenantId`, §31.11) - стоїть першим і підсвічений. */
export const LAST_TENANT_KEY = "beauty-last-tenant";

export function readLastTenant(): string | null {
  try {
    return window.localStorage.getItem(LAST_TENANT_KEY);
  } catch {
    return null;
  }
}

function rememberTenant(id: string) {
  try {
    window.localStorage.setItem(LAST_TENANT_KEY, id);
  } catch {
    /* сховище недоступне - не критично */
  }
}

export function orderBusinesses(list: Business[], last: string | null): Business[] {
  const i = last ? list.findIndex((b) => b.tenantId === last) : -1;
  return i > 0 ? [list[i], ...list.slice(0, i), ...list.slice(i + 1)] : list;
}

/**
 * Екран вибору бізнесу (§31.3, §31.11). Квиток лежить у httpOnly-cookie (BFF), тут його немає.
 * Через `ticketExpiresInSeconds` (або `401 login_ticket_invalid`) показуємо «Час вибору минув» і повертаємо до форми.
 */
export function BusinessPicker({
  businesses,
  ticketExpiresInSeconds,
  onSelected,
  onBack,
}: {
  businesses: Business[];
  ticketExpiresInSeconds: number;
  onSelected: (user: SessionUser) => void;
  onBack: () => void;
}) {
  const [last] = useState(() => readLastTenant());
  const ordered = orderBusinesses(businesses, last);
  const [pendingId, setPendingId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [expired, setExpired] = useState(false);
  const firstRef = useRef<HTMLButtonElement>(null);
  const backRef = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    const id = setTimeout(() => setExpired(true), ticketExpiresInSeconds * 1000);
    return () => clearTimeout(id);
  }, [ticketExpiresInSeconds]);

  useEffect(() => {
    if (expired) backRef.current?.focus();
    else firstRef.current?.focus();
  }, [expired]);

  async function choose(b: Business) {
    if (pendingId) return;
    setPendingId(b.tenantId);
    setError(null);
    try {
      const user = await selectBusiness(b.tenantId);
      rememberTenant(b.tenantId);
      onSelected(user);
    } catch (e) {
      setPendingId(null);
      if (e instanceof BeautyApiError && e.code === "login_ticket_invalid") return setExpired(true);
      setError(humanizeAuthError(e, "Не вдалося відкрити бізнес. Спробуйте ще раз."));
    }
  }

  if (expired) {
    return (
      <AuthCard title="Час вибору минув">
        <div className="flex flex-col gap-4">
          <p role="alert" className="text-sm text-(--muted)">
            {humanizeAuthError(new BeautyApiError(401, "login_ticket_invalid", ""))}
          </p>
          <button
            ref={backRef}
            type="button"
            onClick={onBack}
            className="min-h-11 cursor-pointer rounded-[22px] border-0 bg-(--accent) px-5 text-sm font-semibold text-white"
          >
            Увійти ще раз
          </button>
        </div>
      </AuthCard>
    );
  }

  return (
    <AuthCard title="Оберіть бізнес" subtitle="Ваш обліковий запис має доступ до кількох бізнесів.">
      <div className="flex flex-col gap-3">
        <div aria-live="assertive">{error ? <FormAlert>{error}</FormAlert> : null}</div>
        <ul className="m-0 flex list-none flex-col gap-2 p-0" aria-label="Бізнеси">
          {ordered.map((b, i) => {
            const isLast = b.tenantId === last;
            return (
              <li key={b.tenantId}>
                <button
                  ref={i === 0 ? firstRef : undefined}
                  type="button"
                  disabled={!!pendingId}
                  onClick={() => void choose(b)}
                  aria-current={isLast ? "true" : undefined}
                  className={`flex min-h-14 w-full cursor-pointer flex-col items-start rounded-xl border px-4 py-2.5 text-left disabled:cursor-not-allowed disabled:opacity-60 ${
                    isLast ? "border-(--accent) bg-(--tint)" : "border-(--line-strong) bg-white"
                  }`}
                >
                  <span className="text-[15px] font-semibold">{pendingId === b.tenantId ? `${b.name}: відкриваємо…` : b.name}</span>
                  <span className="text-[13px] text-(--muted)">
                    {ROLE_LABEL[b.role] ?? b.role}
                    {isLast ? ", востаннє обраний" : ""}
                  </span>
                </button>
              </li>
            );
          })}
        </ul>
        <button type="button" onClick={onBack} className="min-h-11 cursor-pointer rounded-xl border-0 bg-transparent text-sm font-semibold text-(--accent)">
          Назад до входу
        </button>
      </div>
    </AuthCard>
  );
}
