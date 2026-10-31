"use client";

import { Fragment, useEffect, useId, useRef, useState, useSyncExternalStore, type KeyboardEvent } from "react";
import { BeautyApiError, humanizeError } from "@/features/beauty-auth/errors";
import { dateTimeLabel } from "../format";
import { maskCouponCodes } from "../marketing/logic";
import { safeMask } from "../phone";
import {
  aiStateView,
  buttonLabels,
  clockOf,
  contactChip,
  dayHeading,
  failureLabel,
  handoffReasonLabel,
  hasControlChars,
  marketingChip,
  maskLabel,
  MAX_REPLY,
  pressedText,
  REPLY_BLOCKED_TEXT,
  replyLength,
  senderOf,
} from "../inbox-logic";
import { useConversation, useMarkConversationRead, useReplyToConversation, useResumeAi } from "../hooks/use-inbox";
import { useLocations } from "../hooks/use-beauty-admin";
import { reminderLine } from "../reminders";
import type { ConversationMessage } from "../types";
import { AiConfirmDialog } from "./ai-confirm-dialog";
import { ConfirmedBadge } from "./client-confirmed-badge";
import { Badge, Card, FIELD_LABEL, PrimaryButton, QueryState, SecondaryButton, TextLink } from "./ui";

const subscribeVisibility = (cb: () => void) => {
  document.addEventListener("visibilitychange", cb);
  return () => document.removeEventListener("visibilitychange", cb);
};
const isVisible = () => document.visibilityState !== "hidden";

/** Бульбашка повідомлення: напрямок, відправник, час у зоні закладу, статус доставки / очікування. */
function Bubble({ m, timezone }: { m: ConversationMessage; timezone?: string }) {
  const inbound = m.direction === "inbound";
  const system = m.senderType === "system";
  const mk = marketingChip(m);
  const pressed = mk ? null : pressedText(m);
  const chip = contactChip(m) ?? mk;
  const labels = buttonLabels(m);
  const tone = system
    ? "bg-white text-(--muted)"
    : inbound
      ? "border border-(--line-strong) bg-white"
      : m.senderType === "staff"
        ? "bg-(--accent) text-white"
        : "bg-(--tint)";
  return (
    <li data-testid="message" data-status={m.status} data-kind={pressed ? "callback" : chip ? "contact" : undefined} className={`flex flex-col gap-1 ${inbound ? "items-start" : system ? "items-center" : "items-end"}`}>
      <div
        className={`max-w-[92%] rounded-2xl px-3.5 py-2.5 text-sm leading-snug break-words whitespace-pre-wrap sm:max-w-[80%] ${tone} ${m.status === "draft" ? "border border-dashed border-(--accent)" : ""}`}
      >
        {chip ?? pressed ?? maskCouponCodes(m.body)}
      </div>
      {labels.length > 0 ? (
        <ul
          data-testid="client-buttons"
          aria-label="Кнопки, які бачить клієнт (неактивні)"
          className="m-0 flex max-w-[92%] list-none flex-wrap gap-1.5 p-0 sm:max-w-[80%]"
        >
          {labels.map((label, i) => (
            <li
              key={`${i}-${label}`}
              data-testid="client-button"
              className="cursor-default rounded-full border border-dashed border-(--line-strong) bg-white px-3 py-1 text-xs text-(--muted) select-none"
            >
              {label}
            </li>
          ))}
        </ul>
      ) : null}
      <div className="flex flex-wrap items-center gap-x-2 gap-y-1 text-xs text-(--muted)">
        <span className="font-semibold">{senderOf(m)}</span>
        <time dateTime={m.createdAt}>{clockOf(m.createdAt, timezone)}</time>
        {m.status === "held" ? (
          <Badge tone="wait" className="!px-2 !py-0.5 !text-xs">
            очікує відповіді людини
          </Badge>
        ) : null}
        {m.status === "draft" ? (
          <Badge tone="neutral" className="!px-2 !py-0.5 !text-xs">
            чернетка, не надіслано клієнту
          </Badge>
        ) : null}
        {m.status === "sending" || m.status === "pending" ? <span>надсилається…</span> : null}
        {m.status === "failed" ? <span className="font-semibold text-[#8A1F1F]">{failureLabel(m.failureCode)}</span> : null}
      </div>
    </li>
  );
}

function ReplyBox({ id, canReply, blockedReason }: { id: string; canReply: boolean; blockedReason?: string }) {
  const reply = useReplyToConversation(id);
  const [text, setText] = useState("");
  const [error, setError] = useState<string | null>(null);
  const area = useRef<HTMLTextAreaElement>(null);
  /** Ключ ідемпотентності на спробу: повтор того самого тексту після збою мережі використовує той самий ключ. */
  const attempt = useRef<{ text: string; key: string } | null>(null);
  const areaId = useId();
  const counterId = useId();
  const len = replyLength(text);
  const tooLong = len > MAX_REPLY;
  const invalid = len === 0 || tooLong || hasControlChars(text);

  function send() {
    const body = text.trim();
    if (invalid || reply.isPending || !canReply) return;
    if (attempt.current?.text !== body) attempt.current = { text: body, key: crypto.randomUUID() };
    setError(null);
    reply.mutate(
      { text: body, key: attempt.current.key },
      {
        onSuccess: () => {
          attempt.current = null;
          setText("");
          area.current?.focus();
        },
        onError: (e) => {
          // Постійні відмови не повторюємо тим самим ключем; мережа / 429 / 5xx - повтор безпечний (ідемпотентно).
          if (e instanceof BeautyApiError && ["idempotency_conflict", "invalid_text", "channel_inactive", "channel_unsupported"].includes(e.code)) {
            attempt.current = null;
          }
          const wait = e instanceof BeautyApiError && e.status === 429 && e.retryAfterSeconds ? ` Спробуйте через ${e.retryAfterSeconds} с.` : "";
          setError(`${humanizeError(e, "Не вдалося надіслати відповідь. Спробуйте ще раз.")}${wait}`);
        },
      },
    );
  }
  const onKeyDown = (e: KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) {
      e.preventDefault();
      send();
    }
  };

  if (!canReply) {
    return (
      <p role="note" data-testid="reply-blocked" className="rounded-xl bg-(--page) px-3.5 py-3 text-sm">
        {REPLY_BLOCKED_TEXT[blockedReason ?? ""] ?? "Відповісти з вебу в цій розмові зараз не можна."}
      </p>
    );
  }
  return (
    <form
      className="flex flex-col gap-2"
      onSubmit={(e) => {
        e.preventDefault();
        send();
      }}
    >
      <label htmlFor={areaId} className={FIELD_LABEL}>
        Відповідь клієнту в Telegram
      </label>
      <textarea
        ref={area}
        id={areaId}
        value={text}
        rows={3}
        onChange={(e) => setText(e.target.value)}
        onKeyDown={onKeyDown}
        aria-describedby={counterId}
        aria-invalid={tooLong || undefined}
        placeholder="Напишіть відповідь. Ctrl+Enter — надіслати"
        className="min-h-24 w-full rounded-xl border border-(--line-strong) bg-white px-3.5 py-2.5 text-[15px] text-(--ink) aria-[invalid=true]:border-[#8A1F1F]"
      />
      <div className="flex flex-wrap items-center justify-between gap-3">
        <span id={counterId} className={`text-[13px] ${tooLong ? "font-semibold text-[#8A1F1F]" : "text-(--muted)"}`}>
          {len} / {MAX_REPLY}
          {tooLong ? " — задовго" : ""}. Звичайний текст, без форматування.
        </span>
        <PrimaryButton type="submit" disabled={invalid || reply.isPending}>
          {reply.isPending ? "Надсилаємо…" : "Надіслати"}
        </PrimaryButton>
      </div>
      {error ? (
        <p role="alert" className="rounded-xl bg-[#FBE4E4] px-3.5 py-2.5 text-sm text-[#8A1F1F]">
          {error}
        </p>
      ) : null}
    </form>
  );
}

export function ConversationThread({ id, onBack }: { id: string; onBack: () => void }) {
  const q = useConversation(id);
  const locations = useLocations();
  const resume = useResumeAi(id);
  const markRead = useMarkConversationRead(id);
  const visible = useSyncExternalStore(subscribeVisibility, isVisible, () => true);
  const [confirming, setConfirming] = useState(false);
  const [resumed, setResumed] = useState(false);
  /** Роздільник «нові повідомлення» визначається один раз при першому завантаженні розмови. */
  const [divider, setDivider] = useState<{ ready: boolean; id: string | null }>({ ready: false, id: null });
  const heading = useRef<HTMLHeadingElement>(null);
  const log = useRef<HTMLUListElement>(null);
  const resumeBtn = useRef<HTMLButtonElement>(null);
  const wasConfirming = useRef(false);
  const markedFor = useRef<string | null>(null);

  const data = q.data;
  const conv = data?.conversation;
  const timezone = locations.data?.find((l) => l.id === conv?.location?.id)?.timezone;
  const messages = data?.messages ?? [];
  const lastInbound = [...messages].reverse().find((m) => m.direction === "inbound");
  const lastInboundId = lastInbound?.id;
  const unread = conv?.unreadCount ?? 0;
  const markReadMutate = markRead.mutate;
  if (data && !divider.ready) {
    const inbound = data.messages.filter((m) => m.direction === "inbound");
    setDivider({ ready: true, id: unread > 0 ? (inbound[Math.max(0, inbound.length - unread)]?.id ?? null) : null });
  }
  const dividerId = divider.id;
  const rows = messages.map((m, i) => {
    const day = dayHeading(m.createdAt, timezone);
    return { m, day, head: i === 0 || day !== dayHeading(messages[i - 1].createdAt, timezone) };
  });

  // Фокус на заголовок розмови при відкритті (клавіатура / скрінрідер).
  const hasConv = !!conv;
  useEffect(() => {
    if (hasConv) heading.current?.focus();
  }, [id, hasConv]);

  // Повернення фокуса після закриття діалогу «Повернути ШІ».
  useEffect(() => {
    if (wasConfirming.current && !confirming) (resumeBtn.current ?? heading.current)?.focus();
    wasConfirming.current = confirming;
  }, [confirming]);

  // «Прочитано»: при відкритті й коли вкладка знову видима; не частіше одного разу на останнє вхідне.
  useEffect(() => {
    if (!visible || unread <= 0 || !lastInboundId || markedFor.current === lastInboundId) return;
    markedFor.current = lastInboundId;
    markReadMutate(lastInboundId);
  }, [visible, unread, lastInboundId, markReadMutate]);

  // Нові повідомлення: прокрутка донизу.
  useEffect(() => {
    const el = log.current;
    if (el) el.scrollTop = el.scrollHeight;
  }, [messages.length]);

  const state = conv ? aiStateView(conv, timezone) : null;
  const canResume = !!conv && (conv.aiState === "handoff" || !!conv.awaitingStaffSince);

  return (
    <Card className="flex flex-col gap-4 p-4 sm:p-5">
      <QueryState isPending={q.isPending} isError={q.isError} onRetry={() => q.refetch()}>
        {conv ? (
          <>
            <div className="flex flex-col gap-2">
              <button
                type="button"
                onClick={onBack}
                className="min-h-11 cursor-pointer self-start border-0 bg-transparent p-0 text-sm font-semibold text-(--accent) underline lg:hidden"
              >
                ← До списку розмов
              </button>
              <h2 ref={heading} tabIndex={-1} className="beauty-display text-xl font-semibold outline-none">
                {conv.title}
              </h2>
              <p className="text-sm text-(--muted)">
                {conv.channel.name}
                {conv.location ? ` · ${conv.location.name}` : ""}
              </p>
              {safeMask(conv.client?.phoneMasked) ? (
                <p data-testid="phone-masked" aria-label={maskLabel(safeMask(conv.client?.phoneMasked)!)} className="m-0 text-sm text-(--muted)">
                  Телефон: <span aria-hidden="true">{safeMask(conv.client?.phoneMasked)}</span>
                </p>
              ) : null}
              {conv.client?.phonePending ? (
                <div data-testid="phone-pending-badge" className="flex flex-wrap items-center gap-2">
                  <Badge tone="wait">Надано інший номер</Badge>
                  <TextLink href={`/beauty/clients/${conv.client.id}`}>Вирішити в картці клієнта</TextLink>
                </div>
              ) : null}
              {conv.client ? <TextLink href={`/beauty/clients/${conv.client.id}`}>Профіль клієнта</TextLink> : null}
            </div>

            {state ? (
              <div data-testid="ai-state" className="flex flex-wrap items-center justify-between gap-3 rounded-xl bg-(--page) p-3">
                <div className="flex min-w-0 flex-col gap-1">
                  <Badge tone={state.tone} className="self-start">
                    {state.label}
                  </Badge>
                  {state.extra ? <span className="text-[13px] text-(--muted)">{state.extra}</span> : null}
                </div>
                {canResume && !confirming ? (
                  <SecondaryButton
                    ref={resumeBtn}
                    onClick={() => {
                      setResumed(false);
                      setConfirming(true);
                    }}
                  >
                    Повернути ШІ
                  </SecondaryButton>
                ) : null}
              </div>
            ) : null}
            {confirming ? (
              <AiConfirmDialog
                title="Повернути розмову ШІ?"
                confirmLabel="Повернути ШІ"
                busy={resume.isPending}
                onCancel={() => setConfirming(false)}
                onConfirm={() =>
                  resume.mutate(undefined, {
                    onSuccess: () => {
                      setConfirming(false);
                      setResumed(true);
                    },
                  })
                }
              >
                <p>ШІ знову відповідатиме клієнту в цій розмові. На повідомлення, що очікували відповіді людини, він не відповідатиме.</p>
                {conv.handoffReason ? <p>Причина передачі: {handoffReasonLabel(conv.handoffReason)}.</p> : null}
                {resume.isError ? (
                  <p role="alert" className="font-semibold">
                    {humanizeError(resume.error, "Не вдалося повернути ШІ. Спробуйте ще раз.")}
                  </p>
                ) : null}
              </AiConfirmDialog>
            ) : null}
            {resumed ? (
              <p role="status" className="rounded-xl bg-[#DDF3E6] px-3.5 py-2.5 text-sm text-[#145A32]">
                ШІ знову відповідає в цій розмові.
              </p>
            ) : null}

            {data && data.upcomingAppointments.length > 0 ? (
              <div className="flex flex-col gap-1.5">
                <h3 className="text-sm font-semibold">Найближчі записи клієнта</h3>
                <ul className="m-0 flex list-none flex-col gap-1.5 p-0 text-[13px]">
                  {data.upcomingAppointments.map((a) => (
                    <li key={a.id} className="rounded-xl border border-(--line) px-3 py-2">
                      <span className="font-semibold">
                        {dateTimeLabel(a.startsAt, locations.data?.find((l) => l.name === a.locationName)?.timezone ?? timezone)}
                      </span>
                      {` · ${a.serviceName} · ${a.locationName}`}
                      <div className="text-(--muted)">Нагадування: {reminderLine(a.reminders.method, a.reminders.offsetsMinutes)}</div>
                      {a.clientConfirmedAt ? (
                        <div className="mt-1">
                          <ConfirmedBadge at={a.clientConfirmedAt} timezone={locations.data?.find((l) => l.name === a.locationName)?.timezone ?? timezone} />
                        </div>
                      ) : null}
                    </li>
                  ))}
                </ul>
              </div>
            ) : null}

            {messages.length === 0 ? (
              <p className="text-sm text-(--muted)">У розмові ще немає повідомлень.</p>
            ) : (
              <ul
                ref={log}
                role="log"
                aria-live="polite"
                aria-relevant="additions"
                aria-label="Повідомлення розмови"
                tabIndex={0}
                className="m-0 flex max-h-[50vh] min-h-40 list-none flex-col gap-3 overflow-y-auto rounded-xl bg-(--page) p-3"
              >
                {rows.map(({ m, day, head }) => {
                  return (
                    <Fragment key={m.id}>
                      {head ? <li className="self-center text-xs font-semibold text-(--muted)">{day}</li> : null}
                      {m.id === dividerId ? (
                        <li data-testid="unread-divider" className="flex items-center gap-2 text-xs font-semibold text-(--accent)">
                          <span aria-hidden="true" className="h-px flex-1 bg-(--accent)" />
                          Нові повідомлення
                          <span aria-hidden="true" className="h-px flex-1 bg-(--accent)" />
                        </li>
                      ) : null}
                      <Bubble m={m} timezone={timezone} />
                    </Fragment>
                  );
                })}
              </ul>
            )}

            <ReplyBox id={id} canReply={conv.canReply} blockedReason={conv.replyBlockedReason} />
          </>
        ) : null}
      </QueryState>
    </Card>
  );
}
