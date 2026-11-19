"use client";

import { useEffect, useId, useRef, useState } from "react";
import { accessOf } from "@/features/beauty-auth/access";
import { useAuth } from "@/features/beauty-auth/components/auth-provider";
import { useCreateTelegramLink, useDeleteTelegramLink, useTelegramLink } from "../hooks/use-telegram-link";
import {
  CONSENT_VERSION,
  STATE_LABEL,
  TTL_MINUTES,
  canManageTelegramLink,
  cardView,
  channelsFromError,
  consentText,
  countdownLabel,
  currentVersionFromError,
  humanizeTelegramLinkError,
  revokeBanner,
  secondsLeft,
  stateAnnouncement,
  type TelegramChannelRef,
  type TelegramLinkState,
  type TelegramLinkStatus,
} from "../inventory/telegram-link";
import { ModalDialog } from "./modal-dialog";
import { Card, PrimaryButton, QueryState, SecondaryButton } from "./ui";

const timeLabel = (iso: string) => new Date(iso).toLocaleTimeString("uk-UA", { hour: "2-digit", minute: "2-digit" });
const dateLabel = (iso: string) => new Date(iso).toLocaleDateString("uk-UA", { day: "2-digit", month: "2-digit", year: "numeric" });
const dateTimeText = (iso: string) => `${dateLabel(iso)}, ${timeLabel(iso)}`;

/** Видимий відлік TTL: окремий компонент, щоб секундні оновлення не перемальовували картку; не в live-області. */
function Countdown({ expiresAt, onExpire }: { expiresAt: string; onExpire: () => void }) {
  const [now, setNow] = useState(() => Date.now());
  const fired = useRef(false);
  useEffect(() => {
    const t = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(t);
  }, []);
  const left = secondsLeft(expiresAt, now);
  useEffect(() => {
    if (left === 0 && !fired.current) {
      fired.current = true;
      onExpire();
    }
  }, [left, onExpire]);
  return (
    <p className="m-0 text-sm">
      Діє до {timeLabel(expiresAt)}
      {left > 0 ? <span className="text-(--muted)"> (залишилось {countdownLabel(left)})</span> : <span> — термін дії минув</span>}
    </p>
  );
}

function Body({ status, refetch }: { status: TelegramLinkStatus; refetch: () => void }) {
  const create = useCreateTelegramLink();
  const remove = useDeleteTelegramLink();
  const consentId = useId();
  const noteId = useId();
  const channelGroup = useId();
  const statusHeading = useRef<HTMLHeadingElement>(null);
  const prevState = useRef<TelegramLinkState | null>(null);

  // Токен/посилання - лише тут (стан компонента); не в URL, localStorage, логах, аналітиці.
  const [deepLink, setDeepLink] = useState<string | null>(null);
  const [consent, setConsent] = useState(false);
  const [channels, setChannels] = useState<TelegramChannelRef[]>([]);
  const [pickedChannel, setPickedChannel] = useState("");
  const [version, setVersion] = useState(status.consentVersion || CONSENT_VERSION);
  const [error, setError] = useState<string | null>(null);
  const [announce, setAnnounce] = useState("");
  const [copied, setCopied] = useState("");
  const [confirming, setConfirming] = useState(false);

  const view = cardView(status);
  const banner = revokeBanner(status);
  const busy = create.isPending || remove.isPending;

  // Зміна стану: одне озвучення (live) і фокус на заголовок статусу; перший рендер - без цього.
  useEffect(() => {
    const prev = prevState.current;
    prevState.current = status.state;
    if (prev === null || prev === status.state) return;
    setAnnounce(stateAnnouncement(status.state, prev) ?? "");
    statusHeading.current?.focus();
    if (status.state !== "pending") setDeepLink(null);
    if (status.state === "active") {
      setConsent(false);
      setError(null);
    }
  }, [status.state]);

  const botName = status.channel?.name ?? channels.find((c) => c.id === pickedChannel)?.name ?? null;
  const needsChannel = channels.length > 1;
  const canSubmit = consent && !busy && (!needsChannel || pickedChannel !== "");

  function connect(channelOverride?: string) {
    setError(null);
    setCopied("");
    create.mutate(
      { consent: true, consentVersion: version, ...(channelOverride || pickedChannel ? { channelId: channelOverride || pickedChannel } : {}) },
      {
        onSuccess: (r) => {
          setDeepLink(r.deepLink || null);
          setChannels([]);
          setPickedChannel("");
          create.reset();
        },
        onError: (e) => {
          const list = channelsFromError(e);
          if (list.length > 0) {
            setChannels(list);
            setPickedChannel("");
          }
          const v = currentVersionFromError(e);
          if (v) {
            setVersion(v);
            setConsent(false);
          }
          setError(humanizeTelegramLinkError(e));
          create.reset();
        },
      },
    );
  }

  function disconnect() {
    setError(null);
    remove.mutate(undefined, {
      onSuccess: () => {
        setConfirming(false);
        setDeepLink(null);
      },
      onError: (e) => {
        setConfirming(false);
        setError(humanizeTelegramLinkError(e));
      },
    });
  }

  async function copy() {
    if (!deepLink) return;
    try {
      await navigator.clipboard.writeText(deepLink);
      setCopied("Посилання скопійовано.");
    } catch {
      setCopied("Не вдалося скопіювати. Скористайтеся кнопкою «Відкрити Telegram».");
    }
  }

  return (
    <div className="flex flex-col gap-4">
      <p role="status" aria-live="polite" className="sr-only">
        {announce}
      </p>

      <h3
        ref={statusHeading}
        tabIndex={-1}
        className="m-0 text-base font-semibold outline-none focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-(--accent)"
      >
        Стан: {STATE_LABEL[status.state]}
      </h3>

      {banner ? (
        <p data-testid="tg-revoke-banner" className="m-0 rounded-xl bg-[#FFF3D6] px-3.5 py-2.5 text-sm text-[#5C4100]">
          Увага: {banner}
        </p>
      ) : null}

      {view.disabledNote ? <p className="m-0 text-sm text-(--muted)">Поки недоступно. Сповіщення в Telegram ще не ввімкнено для вашого бізнесу.</p> : null}

      {status.state === "none" && !view.disabledNote ? (
        <div className="flex flex-col gap-3">
          <p id={consentId} className="m-0 text-sm">
            {consentText(botName)}
          </p>
          {needsChannel ? (
            <fieldset className="m-0 flex flex-col gap-2 border-0 p-0">
              <legend className="mb-1 text-[13px] font-semibold">Оберіть бота для сповіщень</legend>
              {channels.map((c) => (
                <label key={c.id} className="flex min-h-11 items-center gap-2 text-sm">
                  <input type="radio" name={channelGroup} value={c.id} checked={pickedChannel === c.id} onChange={() => setPickedChannel(c.id)} className="h-5 w-5" />
                  {c.name}
                </label>
              ))}
            </fieldset>
          ) : null}
          <label className="flex min-h-11 items-start gap-2 text-sm">
            <input type="checkbox" checked={consent} onChange={(e) => setConsent(e.target.checked)} aria-describedby={consentId} className="mt-0.5 h-5 w-5 flex-none" />
            <span>Я прочитав(ла) і погоджуюсь з умовами вище</span>
          </label>
          <div className="flex flex-wrap items-center gap-3">
            <PrimaryButton onClick={() => connect()} disabled={!canSubmit} aria-describedby={noteId}>
              {create.isPending ? "Створюємо посилання…" : "Підключити"}
            </PrimaryButton>
            <p id={noteId} className="m-0 text-[13px] text-(--muted)">
              {!consent ? "Щоб підключити, поставте позначку згоди." : needsChannel && !pickedChannel ? "Оберіть бота зі списку." : `Посилання діє ${TTL_MINUTES} хв.`}
            </p>
          </div>
        </div>
      ) : null}

      {status.state === "pending" ? (
        <div className="flex flex-col gap-3">
          {deepLink ? (
            <>
              <p className="m-0 text-sm">
                Відкрийте Telegram за посиланням і натисніть «Старт» у боті{status.channel ? ` «${status.channel.name}»` : ""}. Потім підтвердьте підключення в боті.
              </p>
              <div className="flex flex-wrap items-center gap-3">
                <a
                  href={deepLink}
                  target="_blank"
                  rel="noopener noreferrer"
                  className="inline-flex min-h-11 items-center rounded-[22px] bg-(--accent) px-5 text-sm font-semibold text-white no-underline"
                >
                  Відкрити Telegram (в новій вкладці)
                </a>
                <SecondaryButton onClick={copy}>Копіювати посилання</SecondaryButton>
              </div>
              <p role="status" className="m-0 text-[13px] text-(--muted)">
                {copied}
              </p>
            </>
          ) : (
            <p className="m-0 text-sm">Посилання показується один раз і більше недоступне. Створіть нове, щоб продовжити.</p>
          )}
          {status.expiresAt ? <Countdown expiresAt={status.expiresAt} onExpire={refetch} /> : null}
        </div>
      ) : null}

      {status.state === "awaitingConfirmation" ? (
        <div className="flex flex-col gap-3">
          <p className="m-0 text-sm">Чат отримано. Відкрийте Telegram і натисніть «Підтверджую» у відповідному повідомленні бота.</p>
          {status.expiresAt ? <Countdown expiresAt={status.expiresAt} onExpire={refetch} /> : null}
        </div>
      ) : null}

      {status.state === "active" ? (
        <div className="flex flex-col gap-1 text-sm">
          <p className="m-0">
            Підключено{status.activatedAt ? ` ${dateLabel(status.activatedAt)}` : ""}
            {status.channel ? `, бот «${status.channel.name}»` : ""}.
          </p>
          <p className="m-0 text-(--muted)">{status.lastDeliveryAt ? `Остання доставка: ${dateTimeText(status.lastDeliveryAt)}.` : "Сповіщень ще не надсилали."}</p>
        </div>
      ) : null}

      {error ? (
        <p role="alert" className="m-0 rounded-xl bg-[#FBE4E4] px-3.5 py-2.5 text-sm text-[#8A1F1F]">
          {error}
        </p>
      ) : null}

      {(status.state === "pending" || status.state === "awaitingConfirmation") && view.canRenew ? (
        <div>
          <SecondaryButton
            onClick={() => {
              // Згода вже дана при першому створенні; нове посилання замінює попереднє (replaced), канал той самий.
              connect(status.channel?.id);
            }}
            disabled={busy}
          >
            Створити нове посилання
          </SecondaryButton>
        </div>
      ) : null}

      {view.canDisconnect ? (
        <div>
          <SecondaryButton onClick={() => setConfirming(true)} disabled={busy}>
            Відключити
          </SecondaryButton>
        </div>
      ) : null}

      {confirming ? (
        <ModalDialog
          title="Відключити Telegram-сповіщення?"
          busy={remove.isPending}
          onClose={() => setConfirming(false)}
          actions={
            <>
              <PrimaryButton onClick={disconnect} disabled={remove.isPending}>
                {remove.isPending ? "Відключаємо…" : "Відключити"}
              </PrimaryButton>
              <SecondaryButton onClick={() => setConfirming(false)} disabled={remove.isPending}>
                Скасувати
              </SecondaryButton>
            </>
          }
        >
          <p className="m-0">Сповіщення про склад перестануть надходити в Telegram, а ідентифікатор вашого чату буде видалено. Підключити знову можна будь-коли.</p>
        </ModalDialog>
      ) : null}
    </div>
  );
}

/** Картка «Telegram-сповіщення складу» (§32.24.9): лише owner при увімкненому модулі `beauty_inventory`; сервер перевіряє незалежно. */
export function TelegramLinkCard() {
  const allowed = canManageTelegramLink(accessOf(useAuth().user));
  const q = useTelegramLink(allowed);
  if (!allowed) return null;
  return (
    <Card>
      <h2 className="mb-4 text-lg font-semibold">Telegram-сповіщення складу</h2>
      <QueryState isPending={q.isPending} isError={q.isError} onRetry={() => q.refetch()}>
        {q.data ? <Body status={q.data} refetch={() => void q.refetch()} /> : null}
      </QueryState>
    </Card>
  );
}
