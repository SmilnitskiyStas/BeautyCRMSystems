"use client";

import { useId, useState } from "react";
import { useAuth } from "@/features/beauty-auth/components/auth-provider";
import { humanizeError } from "@/features/beauty-auth/errors";
import { canManageReminderPrefs } from "@/features/beauty-auth/permissions";
import { dateTimeLabel } from "../format";
import { METHOD_LABEL, normalizeOffsets, offsetLabel, REMINDER_OFFSETS, reminderLine, SOURCE_LABEL, validateOffsets } from "../reminders";
import { useContactPreferences, useSetContactPreferences } from "../hooks/use-reminders";
import type { ContactPreferences, ReminderMethod } from "../types";
import { Card, PrimaryButton, QueryState } from "./ui";

const OPTION_CLS = "flex min-h-11 cursor-pointer items-center gap-3 text-sm";

function PrefsForm({ clientId, prefs }: { clientId: string; prefs: ContactPreferences }) {
  const save = useSetContactPreferences(clientId);
  const [method, setMethod] = useState<ReminderMethod | null>(prefs.state === "set" ? (prefs.method ?? null) : null);
  const [offsets, setOffsets] = useState<number[]>(prefs.offsetsMinutes);
  const [apply, setApply] = useState(true);
  const [result, setResult] = useState<string | null>(null);
  const groupId = useId();

  const telegramBlocked = !prefs.telegramEnabled ? "Нагадування в Telegram вимкнено в налаштуваннях." : !prefs.telegramAvailable ? "Немає розмови з клієнтом у Telegram: нагадувати там нікуди." : null;
  const callBlocked = !prefs.callEnabled ? "Дзвінки-нагадування вимкнено в налаштуваннях." : !prefs.phoneAvailable ? "У клієнта немає телефону." : null;

  const pick = (m: ReminderMethod) => {
    setMethod(m);
    setResult(null);
    // Для дзвінка лишається одна точка (найпізніша обрана або 24 год), для «Не нагадувати» - жодної.
    setOffsets((cur) => (m === "none" ? [] : m === "call" ? [cur.length ? Math.max(...cur) : 1440] : cur.length ? cur : [120]));
  };
  const toggle = (o: number) => {
    setResult(null);
    setOffsets((cur) => normalizeOffsets(cur.includes(o) ? cur.filter((x) => x !== o) : [...cur, o]));
  };

  const invalid = method === null || validateOffsets(method, offsets) !== null;
  const unchanged = prefs.state === "set" && method === prefs.method && normalizeOffsets(offsets).join() === prefs.offsetsMinutes.join();

  return (
    <form
      className="flex flex-col gap-3"
      onSubmit={(e) => {
        e.preventDefault();
        if (invalid || method === null) return;
        setResult(null);
        save.mutate(
          { method, offsetsMinutes: normalizeOffsets(offsets), applyToUpcoming: apply },
          { onSuccess: (r) => setResult(r.replannedAppointments > 0 ? `Збережено. Перепл. майбутніх записів: ${r.replannedAppointments}.` : "Збережено.") },
        );
      }}
    >
      <fieldset className="flex flex-col gap-0.5 border-0 p-0">
        <legend className="mb-1 text-[13px] font-semibold">Спосіб нагадування</legend>
        <label className={OPTION_CLS}>
          <input type="radio" name={groupId} checked={method === "telegram"} disabled={!!telegramBlocked} onChange={() => pick("telegram")} className="size-5 accent-(--accent)" />
          {METHOD_LABEL.telegram}
        </label>
        {telegramBlocked ? <p className="mb-1 ml-8 text-[13px] text-(--muted)">{telegramBlocked}</p> : null}
        <label className={OPTION_CLS}>
          <input type="radio" name={groupId} checked={method === "call"} disabled={!!callBlocked} onChange={() => pick("call")} className="size-5 accent-(--accent)" />
          {METHOD_LABEL.call}
        </label>
        {callBlocked ? <p className="mb-1 ml-8 text-[13px] text-(--muted)">{callBlocked}</p> : null}
        <label className={OPTION_CLS}>
          <input type="radio" name={groupId} checked={method === "none"} onChange={() => pick("none")} className="size-5 accent-(--accent)" />
          {METHOD_LABEL.none}
        </label>
      </fieldset>

      {method === "telegram" ? (
        <fieldset className="flex flex-col gap-0.5 border-0 p-0">
          <legend className="mb-1 text-[13px] font-semibold">Коли нагадати (можна кілька)</legend>
          {REMINDER_OFFSETS.map((o) => (
            <label key={o} className={OPTION_CLS}>
              <input type="checkbox" checked={offsets.includes(o)} onChange={() => toggle(o)} className="size-5 accent-(--accent)" />
              {offsetLabel(o)} до візиту
            </label>
          ))}
        </fieldset>
      ) : null}
      {method === "call" ? (
        <fieldset className="flex flex-col gap-0.5 border-0 p-0">
          <legend className="mb-1 text-[13px] font-semibold">Коли зателефонувати (одна точка)</legend>
          {REMINDER_OFFSETS.map((o) => (
            <label key={o} className={OPTION_CLS}>
              <input type="radio" name={`${groupId}-call`} checked={offsets[0] === o} onChange={() => { setResult(null); setOffsets([o]); }} className="size-5 accent-(--accent)" />
              {offsetLabel(o)} до візиту
            </label>
          ))}
        </fieldset>
      ) : null}
      {method === "telegram" && offsets.length === 0 ? <p className="text-[13px] text-[#8A1F1F]">Оберіть хоча б одну точку.</p> : null}

      <label className={OPTION_CLS}>
        <input type="checkbox" checked={apply} onChange={(e) => setApply(e.target.checked)} className="size-5 accent-(--accent)" />
        Застосувати до майбутніх записів
      </label>

      {save.isError ? (
        <p role="alert" className="rounded-xl bg-[#FBE4E4] px-3.5 py-2.5 text-sm text-[#8A1F1F]">
          {humanizeError(save.error, "Не вдалося зберегти. Спробуйте ще раз.")}
        </p>
      ) : null}
      {result ? (
        <p role="status" className="rounded-xl bg-[#DDF3E6] px-3.5 py-2.5 text-sm text-[#145A32]">
          {result}
        </p>
      ) : null}
      <div>
        <PrimaryButton type="submit" disabled={invalid || unchanged || save.isPending}>
          {save.isPending ? "Зберігаємо…" : "Зберегти"}
        </PrimaryButton>
      </div>
    </form>
  );
}

/** Картка «Нагадування й зв'язок» у профілі клієнта (§22.11): спосіб, точки, джерело й автор останньої зміни. */
export function ContactPreferencesCard({ clientId }: { clientId: string }) {
  const { user } = useAuth();
  const allowed = canManageReminderPrefs(user.role);
  const q = useContactPreferences(clientId, allowed);
  if (!allowed) return null;
  const p = q.data;
  return (
    <Card as="div" className="flex flex-col gap-3">
      <h2 className="text-lg font-semibold">Нагадування й зв’язок</h2>
      <QueryState isPending={q.isPending} isError={q.isError} onRetry={() => q.refetch()}>
        {p ? (
          <>
            <div data-testid="prefs-summary" className="rounded-xl bg-(--page) px-3.5 py-2.5 text-sm">
              {p.state === "unset" || !p.method ? (
                <strong>Не обрано</strong>
              ) : (
                <>
                  <strong>{reminderLine(p.method, p.offsetsMinutes)}</strong>
                  <div className="mt-1 text-[13px] text-(--muted)">
                    Джерело: {p.source ? SOURCE_LABEL[p.source] : "—"}
                    {p.updatedBy ? `, ${p.updatedBy.name}` : ""}
                    {p.updatedAt ? ` · ${dateTimeLabel(p.updatedAt)}` : ""}
                  </div>
                </>
              )}
            </div>
            <PrefsForm key={`${p.telegramEnabled}-${p.callEnabled}`} clientId={clientId} prefs={p} />
          </>
        ) : null}
      </QueryState>
    </Card>
  );
}
