"use client";

import { useState } from "react";
import { useAuth } from "@/features/beauty-auth/components/auth-provider";
import { humanizeError } from "@/features/beauty-auth/errors";
import { canSeeFullPhone } from "@/features/beauty-auth/permissions";
import { dateTimeLabel } from "../format";
import { useResolvePhonePending } from "../hooks/use-finance";
import { PHONE_SOURCE_LABEL, telHref } from "../phone";
import type { ClientPhoneInfo, PhonePendingAction } from "../types";
import { AiConfirmDialog } from "./ai-confirm-dialog";
import { Badge, Card, SecondaryButton, TextLink, PrimaryButton } from "./ui";

/**
 * Телефон у картці клієнта (§25.8): наявність, джерело, згода на дзвінок, інший номер із чату (pending) з діями
 * «Замінити» / «Залишити поточний» (обидві за підтвердженням) і позначка «можливий дубль». Повний номер бачать лише owner/admin.
 */
export function ClientPhoneCard({ clientId, info }: { clientId: string; info: ClientPhoneInfo }) {
  const { user } = useAuth();
  const resolve = useResolvePhonePending(clientId);
  const [confirming, setConfirming] = useState<PhonePendingAction | null>(null);
  const [result, setResult] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  if (!canSeeFullPhone(user.role)) return null;

  const pending = info.phonePending;
  function run(action: PhonePendingAction) {
    setError(null);
    resolve.mutate(action, {
      onSuccess: () => {
        setConfirming(null);
        setResult(action === "accept" ? "Номер замінено на новий." : "Залишено поточний номер, новий видалено.");
      },
      onError: (e) => {
        setConfirming(null);
        setError(humanizeError(e, "Не вдалося зберегти рішення. Спробуйте ще раз."));
      },
    });
  }

  return (
    <Card as="div" className="flex flex-col gap-3">
      <h2 className="text-lg font-semibold">Телефон</h2>
      <div data-testid="phone-status" className="flex flex-col gap-1.5 text-sm">
        {info.phone ? (
          <>
            <div className="flex flex-wrap items-center gap-2">
              <a href={telHref(info.phone)} className="inline-flex min-h-11 items-center text-[15px] font-semibold text-(--accent) underline">
                {info.phone}
              </a>
              {info.phoneSource ? <Badge tone="neutral">{PHONE_SOURCE_LABEL[info.phoneSource]}</Badge> : null}
            </div>
            <span className="text-[13px] text-(--muted)">
              {info.callConsentAt ? `Згода на дзвінок: ${dateTimeLabel(info.callConsentAt)}` : "Згоди на дзвінок немає"}
            </span>
          </>
        ) : (
          <>
            <strong>Телефону немає</strong>
            <span className="text-[13px] text-(--muted)">Дзвінок-нагадування неможливий без номера. Клієнт може надати його в чаті.</span>
          </>
        )}
      </div>

      {info.possibleDuplicate ? (
        <p data-testid="possible-duplicate" className="m-0 rounded-xl bg-[#FFF1CC] px-3.5 py-2.5 text-sm text-[#5C3900]">
          Можливий дубль: <TextLink href={`/beauty/clients/${info.possibleDuplicate.id}`}>{info.possibleDuplicate.name} →</TextLink>
        </p>
      ) : null}

      {pending ? (
        <div role="group" aria-label="Новий номер від клієнта" data-testid="phone-pending" className="flex flex-col gap-2 rounded-xl border border-dashed border-(--accent) p-3.5 text-sm">
          <p className="m-0">
            Клієнт надав інший номер у чаті: <a href={telHref(pending.phone)} className="font-semibold text-(--accent) underline">{pending.phone}</a>
            {pending.at ? ` (${dateTimeLabel(pending.at)})` : ""}
          </p>
          <p className="m-0 text-[13px] text-(--muted)">Поточний номер не змінено, доки ви не вирішите. Дзвінок іде на поточний номер.</p>
          {confirming ? (
            <AiConfirmDialog
              title={confirming === "accept" ? "Замінити номер?" : "Залишити поточний номер?"}
              confirmLabel={confirming === "accept" ? "Замінити" : "Залишити поточний"}
              busy={resolve.isPending}
              onCancel={() => setConfirming(null)}
              onConfirm={() => run(confirming)}
            >
              {confirming === "accept" ? (
                <p>
                  Номер {info.phone ?? "—"} буде замінено на {pending.phone}. Далі адміністратор телефонуватиме на новий номер.
                </p>
              ) : (
                <p>Новий номер {pending.phone} буде видалено, клієнт лишиться з поточним номером.</p>
              )}
            </AiConfirmDialog>
          ) : (
            <div className="flex flex-wrap gap-2">
              <PrimaryButton onClick={() => setConfirming("accept")}>Замінити</PrimaryButton>
              <SecondaryButton onClick={() => setConfirming("reject")}>Залишити поточний</SecondaryButton>
            </div>
          )}
        </div>
      ) : null}

      {result ? (
        <p role="status" className="m-0 rounded-xl bg-[#DDF3E6] px-3.5 py-2.5 text-sm text-[#145A32]">
          {result}
        </p>
      ) : null}
      {error ? (
        <p role="alert" className="m-0 rounded-xl bg-[#FBE4E4] px-3.5 py-2.5 text-sm text-[#8A1F1F]">
          {error}
        </p>
      ) : null}
    </Card>
  );
}
