"use client";

import { useId, useState, type FormEvent } from "react";
import { useAuth } from "@/features/beauty-auth/components/auth-provider";
import { humanizeError } from "@/features/beauty-auth/errors";
import { canEditReminderSettings, canManageReminderPrefs } from "@/features/beauty-auth/permissions";
import { validateQuietHours } from "../reminders";
import { useReminderSettings, useSaveReminderSettings } from "../hooks/use-reminders";
import type { ReminderSettings } from "../types";
import { Card, FIELD_INPUT, FIELD_LABEL, PrimaryButton, QueryState } from "./ui";

function ReminderForm({ initial, canEdit }: { initial: ReminderSettings; canEdit: boolean }) {
  const save = useSaveReminderSettings();
  const [draft, setDraft] = useState<ReminderSettings>(initial);
  const [saved, setSaved] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const tgId = useId();
  const callId = useId();
  const startId = useId();
  const endId = useId();
  const set = (patch: Partial<ReminderSettings>) => {
    setDraft((d) => ({ ...d, ...patch }));
    setSaved(false);
  };

  function onSubmit(e: FormEvent) {
    e.preventDefault();
    if (!canEdit) return;
    if (!validateQuietHours(draft.quietHoursStart, draft.quietHoursEnd)) {
      setError("Години тиші: початок і кінець мають бути різними, а тиша — не довшою за 12 годин.");
      return;
    }
    setError(null);
    save.mutate(draft, {
      onSuccess: (s) => {
        setDraft(s);
        setSaved(true);
      },
      onError: (err) => setError(humanizeError(err, "Не вдалося зберегти налаштування. Спробуйте ще раз.")),
    });
  }

  return (
    <form onSubmit={onSubmit} noValidate data-testid="reminder-settings-form" className="grid grid-cols-1 items-start gap-6 lg:grid-cols-2 lg:gap-x-10">
      {!canEdit ? (
        <p role="note" className="rounded-xl bg-(--tint) px-3.5 py-2.5 text-sm lg:col-span-2">
          Лише власник може змінювати ці налаштування. Ви бачите поточні значення.
        </p>
      ) : null}
      <fieldset className="flex flex-col gap-1 border-0 p-0">
        <legend className="mb-1 text-[13px] font-semibold">Способи нагадування</legend>
        <label htmlFor={tgId} className="flex min-h-11 cursor-pointer items-center gap-3 text-sm">
          <input id={tgId} type="checkbox" checked={draft.telegramEnabled} disabled={!canEdit} onChange={(e) => set({ telegramEnabled: e.target.checked })} className="size-5 accent-(--accent)" />
          Нагадування в Telegram
        </label>
        <label htmlFor={callId} className="flex min-h-11 cursor-pointer items-center gap-3 text-sm">
          <input id={callId} type="checkbox" checked={draft.callEnabled} disabled={!canEdit} onChange={(e) => set({ callEnabled: e.target.checked })} className="size-5 accent-(--accent)" />
          Дзвінки адміністратора (розділ «Дзвінки»)
        </label>
        <p className="text-[13px] text-(--muted)">Зміна діє на нові записи й перевірку тиші; наявні точки не перераховуються.</p>
      </fieldset>
      <fieldset className="flex flex-col gap-2 border-0 p-0">
        <legend className="mb-1 text-[13px] font-semibold">Нічні години (повідомлення не надсилаються)</legend>
        <div className="flex flex-wrap items-end gap-3">
          <div className="flex flex-col gap-1.5">
            <label htmlFor={startId} className={FIELD_LABEL}>
              З
            </label>
            <input id={startId} type="time" value={draft.quietHoursStart} disabled={!canEdit} onChange={(e) => set({ quietHoursStart: e.target.value })} className={`${FIELD_INPUT} w-auto`} />
          </div>
          <div className="flex flex-col gap-1.5">
            <label htmlFor={endId} className={FIELD_LABEL}>
              До
            </label>
            <input id={endId} type="time" value={draft.quietHoursEnd} disabled={!canEdit} onChange={(e) => set({ quietHoursEnd: e.target.value })} className={`${FIELD_INPUT} w-auto`} />
          </div>
        </div>
        <p className="text-[13px] text-(--muted)">За часовою зоною закладу. Повідомлення, що потрапляють на ці години, переносяться на ранок, якщо до візиту ще є година.</p>
      </fieldset>
      {error ? (
        <p role="alert" className="rounded-xl bg-[#FBE4E4] px-3.5 py-2.5 text-sm text-[#8A1F1F] lg:col-span-2">
          {error}
        </p>
      ) : null}
      {saved ? (
        <p role="status" className="rounded-xl bg-[#DDF3E6] px-3.5 py-2.5 text-sm text-[#145A32] lg:col-span-2">
          Налаштування збережено.
        </p>
      ) : null}
      {canEdit ? (
        <div className="lg:col-span-2">
          <PrimaryButton type="submit" disabled={save.isPending}>
            {save.isPending ? "Зберігаємо…" : "Зберегти"}
          </PrimaryButton>
        </div>
      ) : null}
    </form>
  );
}

/** Секція «Нагадування» в Налаштуваннях (§22.6.4): owner редагує, admin читає; specialist її не бачить. */
export function ReminderSettingsSection() {
  const { user } = useAuth();
  const allowed = canManageReminderPrefs(user.role);
  const q = useReminderSettings(allowed);
  if (!allowed) return null;
  return (
    <Card>
      <h2 className="mb-4 text-lg font-semibold">Нагадування клієнтам</h2>
      <QueryState isPending={q.isPending} isError={q.isError} onRetry={() => q.refetch()}>
        {q.data ? <ReminderForm initial={q.data} canEdit={canEditReminderSettings(user.role)} /> : null}
      </QueryState>
    </Card>
  );
}
