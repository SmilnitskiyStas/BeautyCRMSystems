"use client";

import { useId, useState } from "react";
import { useAuth } from "@/features/beauty-auth/components/auth-provider";
import { BeautyApiError } from "@/features/beauty-auth/errors";
import { canManageConsent } from "@/features/beauty-auth/permissions";
import { dateTimeLabel } from "../format";
import { useClientCoupons, useMarketingConsent, useSetMarketingConsent } from "../hooks/use-marketing";
import {
  BASIS_LABEL,
  BASIS_NOTE_MAX,
  LEVEL_LABEL,
  SOURCE_LABEL,
  consentEventLine,
  dmy,
  hintLabel,
  humanizeMarketingError,
  isMarketingDisabled,
} from "../marketing/logic";
import type { ConsentBasis, ConsentLevel } from "../types";
import { ErrorLine } from "./marketing-notices";
import { ModalDialog } from "./modal-dialog";
import { Badge, Card, FIELD_INPUT, FIELD_LABEL, PrimaryButton, SecondaryButton } from "./ui";

const TONE: Record<ConsentLevel, "ok" | "now" | "neutral"> = { full: "ok", selective: "now", none: "neutral" };
const RANK: Record<ConsentLevel, number> = { none: 0, selective: 1, full: 2 };

/**
 * Маркетингова згода клієнта (§26.12): рівень, канал, джерело, версія тексту, журнал. Адмін може ЗНИЗИТИ або відкликати
 * (за підтвердженням); підвищення - лише з підставою й нотаткою і лише якщо `Marketing:AdminGrantEnabled` (інакше пояснення).
 */
export function MarketingConsentCard({ clientId }: { clientId: string }) {
  const { user } = useAuth();
  const allowed = canManageConsent(user.role);
  const q = useMarketingConsent(clientId, allowed);
  const set = useSetMarketingConsent(clientId);
  const [lower, setLower] = useState<ConsentLevel | null>(null);
  const [raising, setRaising] = useState(false);
  const [grantBlocked, setGrantBlocked] = useState(false);
  const [done, setDone] = useState<string | null>(null);
  if (!allowed) return null;

  const data = q.data;
  const tg = data?.channels.find((c) => c.channel === "telegram");
  const level = tg?.level ?? "none";
  const grantOff = grantBlocked || data?.adminGrantEnabled === false;
  const canRaise = level !== "full";

  function lowerTo(target: ConsentLevel) {
    set.mutate(
      { channel: "telegram", level: target },
      {
        onSuccess: () => {
          setLower(null);
          setDone(target === "none" ? "Розсилки для клієнта вимкнено." : "Рівень згоди знижено до «лише акції».");
        },
        onError: () => setLower(null),
      },
    );
  }

  return (
    <Card as="div" className="flex flex-col gap-3">
      <h2 className="text-lg font-semibold">Маркетингова згода</h2>
      {q.isPending ? <div role="status" className="text-sm text-(--muted)">Завантаження…</div> : null}
      {q.isError ? <ErrorLine error={q.error} fallback="Не вдалося завантажити згоду." onRetry={() => q.refetch()} /> : null}
      {data && tg ? (
        <>
          <div data-testid="consent-status" className="flex flex-col gap-1.5 text-sm">
            <div className="flex flex-wrap items-center gap-2">
              <Badge tone={tg.state === "unset" ? "wait" : TONE[level]}>{tg.state === "unset" ? "Ще не обирав" : LEVEL_LABEL[level]}</Badge>
              <span className="text-(--muted)">Канал: Telegram</span>
            </div>
            {tg.state === "set" ? (
              <>
                <span>
                  {tg.source ? SOURCE_LABEL[tg.source] : ""}
                  {tg.source === "admin" && tg.updatedBy?.name ? `: ${tg.updatedBy.name}` : ""}
                  {tg.updatedAt ? `, ${dateTimeLabel(tg.updatedAt)}` : ""}
                </span>
                {tg.textVersion ? <span className="text-[13px] text-(--muted)">Версія тексту згоди: {tg.textVersion}</span> : null}
              </>
            ) : (
              <span className="text-[13px] text-(--muted)">
                Клієнт не давав і не відкликав згоду. {data.offeredAt ? `Пропозицію в боті показано ${dateTimeLabel(data.offeredAt)}.` : "Пропозиція в боті ще не показувалась."}
              </span>
            )}
            {!data.telegramAvailable ? <span className="text-[13px] text-(--muted)">Немає розмови в Telegram: повідомлення клієнту не дійде.</span> : null}
          </div>

          <div className="flex flex-wrap gap-2">
            {level === "full" ? <SecondaryButton onClick={() => setLower("selective")}>Лише акції</SecondaryButton> : null}
            {level !== "none" ? <SecondaryButton onClick={() => setLower("none")}>Вимкнути розсилки</SecondaryButton> : null}
            {canRaise && !grantOff ? (
              <SecondaryButton aria-expanded={raising} onClick={() => setRaising((v) => !v)}>
                Підвищити згоду…
              </SecondaryButton>
            ) : null}
          </div>

          {canRaise && grantOff ? (
            <p data-testid="grant-unavailable" className="m-0 rounded-xl bg-(--page) p-3 text-[13px] text-(--muted)">
              Підвищити згоду за клієнта не можна: її вмикає лише сам клієнт у боті (команда /marketing). Ви можете знизити рівень або вимкнути розсилки.
            </p>
          ) : null}

          {raising && canRaise && !grantOff ? (
            <RaiseForm
              current={level}
              busy={set.isPending}
              error={set.isError && !lower ? set.error : null}
              onSubmit={(input) =>
                set.mutate(
                  { channel: "telegram", ...input },
                  {
                    onSuccess: () => {
                      setRaising(false);
                      setDone("Згоду підвищено. Підставу збережено в журналі.");
                    },
                    onError: (e) => {
                      if (e instanceof BeautyApiError && e.code === "admin_grant_disabled") {
                        setGrantBlocked(true);
                        setRaising(false);
                      }
                    },
                  },
                )
              }
            />
          ) : null}

          {done ? (
            <p role="status" className="m-0 rounded-xl bg-[#DDF3E6] px-3.5 py-2.5 text-sm text-[#145A32]">{done}</p>
          ) : null}
          {set.isError && !raising ? <ErrorLine error={set.error} /> : null}

          <details className="text-sm">
            <summary className="min-h-11 cursor-pointer py-2.5 font-semibold">Історія змін ({data.history.length})</summary>
            {data.history.length === 0 ? (
              <p className="m-0 text-(--muted)">Змін ще не було.</p>
            ) : (
              <ol data-testid="consent-history" className="m-0 flex list-none flex-col gap-1.5 p-0">
                {data.history.map((e, i) => (
                  <li key={`${e.at}-${i}`} className="border-t border-(--line) pt-1.5">
                    <span className="text-(--muted)">{dateTimeLabel(e.at)}: </span>
                    {consentEventLine(e)}
                    {e.basisNote ? <span className="block text-[13px] text-(--muted)">Нотатка: {e.basisNote}</span> : null}
                  </li>
                ))}
              </ol>
            )}
          </details>
        </>
      ) : null}

      {lower ? (
        <ModalDialog
          title={lower === "none" ? "Вимкнути розсилки для клієнта?" : "Залишити лише акції?"}
          busy={set.isPending}
          onClose={() => setLower(null)}
          actions={
            <>
              <PrimaryButton disabled={set.isPending} onClick={() => lowerTo(lower)}>
                {set.isPending ? "Зберігаємо…" : lower === "none" ? "Вимкнути розсилки" : "Залишити лише акції"}
              </PrimaryButton>
              <SecondaryButton disabled={set.isPending} onClick={() => setLower(null)}>Скасувати</SecondaryButton>
            </>
          }
        >
          <p className="m-0">
            {lower === "none"
              ? "Клієнт більше не отримуватиме акцій і новин. Нагадування про візити працюватимуть як раніше. Повернути розсилки зможе лише сам клієнт у боті."
              : "Клієнт отримуватиме лише акції на послуги, якими користується, без новин. Повернути повний рівень зможе лише сам клієнт у боті."}
          </p>
          {set.isError ? <p role="alert" className="m-0 text-[#8A1F1F]">{humanizeMarketingError(set.error)}</p> : null}
        </ModalDialog>
      ) : null}
    </Card>
  );
}

function RaiseForm({
  current,
  busy,
  error,
  onSubmit,
}: {
  current: ConsentLevel;
  busy: boolean;
  error: unknown;
  onSubmit: (v: { level: ConsentLevel; basis: ConsentBasis; basisNote: string }) => void;
}) {
  const options = (["selective", "full"] as ConsentLevel[]).filter((l) => RANK[l] > RANK[current]);
  const [level, setLevel] = useState<ConsentLevel>(options[0]);
  const [basis, setBasis] = useState<ConsentBasis | "">("");
  const [note, setNote] = useState("");
  const [problem, setProblem] = useState<string | null>(null);
  const id = useId();

  function submit(e: React.FormEvent) {
    e.preventDefault();
    if (!basis) return setProblem("Оберіть підставу.");
    if (note.trim().length < 1) return setProblem("Додайте нотатку до підстави.");
    if ([...note].length > BASIS_NOTE_MAX) return setProblem(`Нотатка: максимум ${BASIS_NOTE_MAX} символів.`);
    setProblem(null);
    onSubmit({ level, basis, basisNote: note.trim() });
  }

  return (
    <form onSubmit={submit} aria-label="Підвищення згоди" className="flex flex-col gap-3 rounded-xl border border-dashed border-(--accent) p-3.5">
      <p className="m-0 rounded-xl bg-[#FFF1CC] px-3 py-2 text-[13px] text-[#5C3900]">
        Згоду зазвичай дає сам клієнт у боті. Підвищуйте її лише за наявності підтвердження від клієнта: підставу й нотатку буде збережено в журналі.
      </p>
      <fieldset className="m-0 flex min-w-0 flex-col gap-1 border-0 p-0">
        <legend className={`${FIELD_LABEL} mb-1 p-0`}>Новий рівень</legend>
        {options.map((l) => (
          <label key={l} className="flex min-h-11 cursor-pointer items-center gap-3 text-sm">
            <input type="radio" name={`${id}-lvl`} checked={level === l} onChange={() => setLevel(l)} />
            {LEVEL_LABEL[l]}
          </label>
        ))}
      </fieldset>
      <div className="flex flex-col gap-1.5">
        <label htmlFor={`${id}-b`} className={FIELD_LABEL}>Підстава</label>
        <select id={`${id}-b`} value={basis} onChange={(e) => setBasis(e.target.value as ConsentBasis | "")} className={FIELD_INPUT}>
          <option value="">Оберіть підставу</option>
          {(Object.keys(BASIS_LABEL) as ConsentBasis[]).map((b) => (
            <option key={b} value={b}>{BASIS_LABEL[b]}</option>
          ))}
        </select>
      </div>
      <div className="flex flex-col gap-1.5">
        <label htmlFor={`${id}-n`} className={FIELD_LABEL}>Нотатка до підстави (до {BASIS_NOTE_MAX} символів)</label>
        <textarea id={`${id}-n`} rows={2} maxLength={BASIS_NOTE_MAX} value={note} onChange={(e) => setNote(e.target.value)} className="w-full resize-y rounded-xl border border-(--line-strong) px-3.5 py-2.5 text-[15px]" />
        <span className="text-xs text-(--muted)" aria-live="polite">{[...note].length} / {BASIS_NOTE_MAX}</span>
      </div>
      {problem ? <p role="alert" className="m-0 text-sm text-[#8A1F1F]">{problem}</p> : null}
      {error ? <ErrorLine error={error} /> : null}
      <div>
        <PrimaryButton type="submit" disabled={busy}>{busy ? "Зберігаємо…" : "Підвищити згоду"}</PrimaryButton>
      </div>
    </form>
  );
}

/** «Купони клієнта» (§26.12): лише `codeHint`, акція, строк, статус. Ховається, якщо розсилки вимкнено. */
export function ClientCouponsCard({ clientId }: { clientId: string }) {
  const { user } = useAuth();
  const allowed = canManageConsent(user.role);
  const q = useClientCoupons(clientId, allowed);
  const [nowMs] = useState(() => Date.now());
  if (!allowed || (q.isError && isMarketingDisabled(q.error))) return null;
  const list = q.data ?? [];
  return (
    <Card as="div" className="flex flex-col gap-3">
      <h2 className="text-lg font-semibold">Купони клієнта</h2>
      {q.isPending ? <div role="status" className="text-sm text-(--muted)">Завантаження…</div> : null}
      {q.isError ? <ErrorLine error={q.error} onRetry={() => q.refetch()} /> : null}
      {q.isSuccess && list.length === 0 ? <p className="m-0 text-sm text-(--muted)">Купонів немає.</p> : null}
      <ul className="m-0 flex list-none flex-col gap-2 p-0">
        {list.map((c) => {
          const expired = Date.parse(c.validTo) <= nowMs;
          const state = c.status === "revoked" ? { label: "Відкликано", tone: "neutral" as const } : c.usedAt ? { label: `Використано ${dateTimeLabel(c.usedAt)}`, tone: "ok" as const } : expired ? { label: "Строк минув", tone: "neutral" as const } : { label: `Діє до ${dmy(c.validTo, undefined, true)}`, tone: "now" as const };
          return (
            <li key={c.id} data-testid="client-coupon" className="flex flex-col gap-1 rounded-xl border border-(--line) p-3 text-sm">
              <span className="flex flex-wrap items-center justify-between gap-2">
                <span className="font-semibold">{c.promotion.name}</span>
                <Badge tone={state.tone}>{state.label}</Badge>
              </span>
              <span className="text-(--muted)">
                Код: <span aria-label={`закінчується на ${c.codeHint}`}>{hintLabel(c.codeHint)}</span>
                {c.campaign ? ` · розсилка «${c.campaign.name}»` : ""}
              </span>
            </li>
          );
        })}
      </ul>
    </Card>
  );
}
