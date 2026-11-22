"use client";

import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useRouter } from "next/navigation";
import { useId, useState } from "react";
import { BeautyApiError, humanizeError } from "../errors";
import { homeFor } from "../permissions";
import { fetchMemberships, switchBusiness } from "../session";
import { ROLE_LABEL } from "../types";
import { useAuth } from "./auth-provider";

/**
 * Перемикач бізнесу в шапці (§31.4-§31.5, Q6): показується лише при ≥ 2 активних членствах.
 * Після перемикання кеш запитів скидається, права читаються з `me` нового членства (ADR-023).
 */
export function BusinessSwitcher() {
  const { user } = useAuth();
  const qc = useQueryClient();
  const router = useRouter();
  const id = useId();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const { data, refetch } = useQuery({
    queryKey: ["memberships", user.id],
    queryFn: fetchMemberships,
    staleTime: 60_000,
    retry: false,
  });
  if (!data || data.length < 2) return null;
  const current = data.find((m) => m.current)?.tenantId ?? "";

  async function onChange(tenantId: string) {
    if (busy || tenantId === current) return;
    setBusy(true);
    setError(null);
    try {
      // Кеш скидається лише після успішного switch, але до збереження токена й reloadMe (R-1).
      const next = await switchBusiness(tenantId, {
        beforeActivate: async () => {
          await qc.cancelQueries();
          qc.clear();
        },
      });
      router.replace(homeFor(next));
    } catch (e) {
      setError(humanizeError(e, "Не вдалося перемкнути бізнес. Спробуйте ще раз."));
      if (e instanceof BeautyApiError && e.code === "membership_not_found") void refetch();
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="flex flex-col gap-1.5">
      <label htmlFor={id} className="text-[13px] font-semibold text-[#D9D4E4]">
        Бізнес
      </label>
      <select
        id={id}
        value={current}
        disabled={busy}
        onChange={(e) => void onChange(e.target.value)}
        className="min-h-11 w-full cursor-pointer rounded-xl border border-white/40 bg-(--ink) px-3 text-[15px] text-white disabled:cursor-not-allowed disabled:opacity-60"
      >
        {data.map((m) => (
          <option key={m.tenantId} value={m.tenantId}>
            {m.name} ({ROLE_LABEL[m.role] ?? m.role})
          </option>
        ))}
      </select>
      <div aria-live="polite">{busy ? <span className="text-[13px] text-[#D9D4E4]">Перемикаємо…</span> : null}</div>
      {error ? (
        <p role="alert" className="text-[13px] text-[#FBE4E4]">
          {error}
        </p>
      ) : null}
    </div>
  );
}
