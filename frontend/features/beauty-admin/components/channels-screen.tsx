"use client";

import Link from "next/link";
import { useId, useState } from "react";
import {
  useChannels,
  useConnectChannel,
  useDisconnectChannel,
  useUpdateChannel,
} from "../hooks/use-beauty-admin";
import type { ChannelConfig, ChannelId } from "../types";
import { AI_MODES, buildChannelPreview } from "./channel-preview";
import {
  Badge,
  Card,
  FIELD_INPUT,
  FIELD_LABEL,
  PageHeader,
  PrimaryButton,
  QueryState,
  SecondaryButton,
  Switch,
} from "./ui";

function ConnectForm({ channel }: { channel: ChannelConfig }) {
  const [secret, setSecret] = useState("");
  const connect = useConnectChannel();
  const inputId = useId();
  return (
    <form
      className="flex flex-col gap-2.5"
      onSubmit={(e) => {
        e.preventDefault();
        connect.mutate({ id: channel.id, secret });
      }}
    >
      <div className="text-[15px] font-semibold">Як підключити</div>
      <div className="rounded-xl bg-(--page) px-3.5 py-3 text-sm leading-normal">{channel.connectHelp.how}</div>
      <label htmlFor={inputId} className={FIELD_LABEL}>
        {channel.connectHelp.field}
      </label>
      <input
        id={inputId}
        type="text"
        autoComplete="off"
        value={secret}
        onChange={(e) => setSecret(e.target.value)}
        placeholder={channel.connectHelp.placeholder}
        className={FIELD_INPUT}
      />
      <div className="text-[13px] text-(--muted)">
        Ключі зберігаються в зашифрованому вигляді й після підключення показуються лише частково.
      </div>
      {connect.isError ? (
        <p role="alert" className="text-sm text-[#8A1F1F]">
          {connect.error instanceof Error ? connect.error.message : "Не вдалося підключити канал."}
        </p>
      ) : null}
      <button
        type="submit"
        disabled={connect.isPending}
        className="min-h-[52px] cursor-pointer rounded-[26px] border-0 bg-(--accent) text-base font-semibold text-white disabled:opacity-60"
      >
        {connect.isPending ? "Підключаємо…" : `Підключити ${channel.name}`}
      </button>
    </form>
  );
}

function ChannelSettings({ channel }: { channel: ChannelConfig }) {
  const update = useUpdateChannel();
  const disconnect = useDisconnectChannel();
  const [confirmOff, setConfirmOff] = useState(false);
  const [greeting, setGreeting] = useState<string | null>(null);
  const modeId = useId();
  const greetingId = useId();
  const patch = (p: Parameters<typeof update.mutate>[0]["patch"]) => update.mutate({ id: channel.id, patch: p });
  const aiActive = channel.connected && channel.ai;
  const mode = AI_MODES.find((m) => m.id === channel.mode) ?? AI_MODES[0];

  const toggles: { label: string; note: string; checked: boolean; onChange: (v: boolean) => void }[] = [
    { label: "Приймати повідомлення в єдиний ящик", note: "Повідомлення з каналу зʼявляються в розділі «AI-асистент → Заявки».", checked: channel.inbox, onChange: (v) => patch({ inbox: v }) },
    { label: "Запис клієнтів через канал", note: "Клієнт обирає заклад, майстра, послугу й час прямо в чаті. Запис потрапляє в календар.", checked: channel.booking, onChange: (v) => patch({ booking: v }) },
    { label: "AI-агент спілкується з клієнтами", note: "Відповідає про ціни, акції та вільний час, пропонує запис.", checked: channel.ai, onChange: (v) => patch({ ai: v }) },
  ];

  return (
    <>
      {update.isError ? (
        <p role="alert" className="text-sm text-[#8A1F1F]">
          Не вдалося зберегти зміни.
        </p>
      ) : null}
      <div className="flex flex-col gap-1">
        {toggles.map((t) => (
          <div key={t.label} className="flex items-center justify-between gap-4 border-t border-(--line) py-3">
            <span className="flex min-w-0 flex-col gap-0.5">
              <span className="text-[15px] font-semibold">{t.label}</span>
              <span className="text-[13px] leading-snug text-(--muted)">{t.note}</span>
            </span>
            <Switch label={t.label} checked={t.checked} onChange={t.onChange} />
          </div>
        ))}
      </div>

      {aiActive ? (
        <div className="flex flex-col gap-2.5 border-t border-(--line) pt-3.5">
          <label htmlFor={modeId} className="text-[15px] font-semibold">
            Як діє AI-агент
          </label>
          <select
            id={modeId}
            value={channel.mode}
            onChange={(e) => patch({ mode: e.target.value as ChannelConfig["mode"] })}
            className="min-h-[52px] w-full rounded-[14px] border-2 border-(--line-strong) bg-white px-3.5 text-[15px] font-semibold"
          >
            {AI_MODES.map((m) => (
              <option key={m.id} value={m.id}>
                {m.label}
              </option>
            ))}
          </select>
          <div className="text-[13px] text-(--muted)">{mode.hint}</div>

          <label htmlFor={greetingId} className="mt-1.5 text-[15px] font-semibold">
            Привітання клієнту
          </label>
          <textarea
            id={greetingId}
            rows={3}
            value={greeting ?? channel.greeting}
            onChange={(e) => setGreeting(e.target.value)}
            onBlur={() => {
              if (greeting !== null && greeting !== channel.greeting) patch({ greeting });
              setGreeting(null);
            }}
            className="w-full resize-y rounded-xl border-2 border-(--line-strong) px-3.5 py-3 text-[15px] leading-snug"
          />

          <div className="mt-1.5 text-[15px] font-semibold">Передавати розмову менеджеру</div>
          {(
            [
              ["negative", "Скарга або негативний відгук"],
              ["payment", "Питання про оплату й повернення коштів"],
              ["human", "Клієнт просить звʼязати з людиною"],
            ] as const
          ).map(([key, label]) => (
            <div key={key} className="flex items-center justify-between gap-4">
              <span className="text-sm">{label}</span>
              <Switch label={label} checked={channel.handoff[key]} onChange={(v) => patch({ handoff: { [key]: v } })} />
            </div>
          ))}
        </div>
      ) : null}

      <div className="flex flex-wrap items-center gap-2 border-t border-(--line) pt-3.5">
        <div className="min-w-[200px] flex-1 text-[13px] text-(--muted)">Ключ підключення: {channel.maskedSecret ?? "••••••••••••"}</div>
        {confirmOff ? (
          <>
            <PrimaryButton disabled={disconnect.isPending} onClick={() => disconnect.mutate(channel.id, { onSuccess: () => setConfirmOff(false) })}>
              {disconnect.isPending ? "Відключаємо…" : "Так, відключити"}
            </PrimaryButton>
            <SecondaryButton onClick={() => setConfirmOff(false)}>Залишити</SecondaryButton>
          </>
        ) : (
          <SecondaryButton className="!text-[#8A1F1F]" onClick={() => setConfirmOff(true)}>
            Відключити канал
          </SecondaryButton>
        )}
      </div>
    </>
  );
}

export function ChannelsScreen() {
  const q = useChannels();
  const [selectedId, setSelectedId] = useState<ChannelId>("tg");
  const channels = q.data;
  const cur = channels?.find((c) => c.id === selectedId) ?? channels?.[0];

  const connectedCount = channels?.filter((c) => c.connected).length ?? 0;
  const aiCount = channels?.filter((c) => c.connected && c.ai).length ?? 0;
  const preview = cur ? buildChannelPreview(cur) : [];

  return (
    <>
      <PageHeader
        title="Канали"
        subtitle={channels ? `Підключено ${connectedCount} з ${channels.length} · AI відповідає у ${aiCount}` : "Канали зв’язку з клієнтами"}
      />
      <QueryState isPending={q.isPending} isError={q.isError} onRetry={() => q.refetch()}>
        {channels && cur ? (
          <>
            <div className="grid gap-3" style={{ gridTemplateColumns: "repeat(auto-fit, minmax(240px, 1fr))" }}>
              {channels.map((c) => (
                <button
                  key={c.id}
                  type="button"
                  aria-pressed={c.id === cur.id}
                  onClick={() => setSelectedId(c.id)}
                  className={`flex min-h-[72px] cursor-pointer items-center gap-3 rounded-2xl border-2 bg-white p-3.5 text-left text-(--ink) ${c.id === cur.id ? "border-(--accent)" : "border-[#E4DFEC]"}`}
                >
                  <span aria-hidden="true" className="flex size-11 flex-none items-center justify-center rounded-full bg-[#EADCF2] text-sm font-semibold text-[#4A2260]">
                    {c.icon}
                  </span>
                  <span className="flex min-w-0 flex-1 flex-col gap-1">
                    <span className="text-[15px] font-semibold">{c.name}</span>
                    <span className="flex flex-wrap gap-1.5">
                      <Badge tone={c.connected ? "ok" : "neutral"} className="!rounded-md !px-2 !py-0.5 !text-xs">
                        {c.connected ? "Підключено" : "Не підключено"}
                      </Badge>
                      {c.connected && c.ai ? (
                        <Badge tone="vip" className="!rounded-md !px-2 !py-0.5 !text-xs">
                          AI
                        </Badge>
                      ) : null}
                    </span>
                  </span>
                </button>
              ))}
            </div>

            <div className="flex flex-wrap items-start gap-6">
              <Card className="flex flex-[999_1_460px] flex-col gap-4">
                <div className="flex flex-wrap items-center justify-between gap-3">
                  <h2 className="text-xl font-semibold">{cur.name}</h2>
                  <Badge tone={cur.connected ? "ok" : "neutral"}>{cur.connected ? "Підключено" : "Не підключено"}</Badge>
                </div>
                <div className="text-sm text-(--muted)">{cur.description}</div>
                {cur.connected ? <ChannelSettings key={cur.id} channel={cur} /> : <ConnectForm key={cur.id} channel={cur} />}
              </Card>

              <aside className="flex min-w-0 flex-[1_1_340px] flex-col gap-4">
                <Card as="div" className="flex flex-col gap-3">
                  <h2 className="text-lg font-semibold">Так це бачить клієнт</h2>
                  <div className="flex min-h-[120px] flex-col gap-2 rounded-[14px] bg-(--page) p-3" aria-label="Попередній перегляд розмови" role="log">
                    {preview.map((m, i) => (
                      <div
                        key={`${cur.id}-${i}`}
                        className={`max-w-[85%] rounded-[14px] px-3 py-2 text-[13px] leading-snug ${
                          m.from === "bot"
                            ? "self-start bg-(--accent) text-white"
                            : m.from === "manager"
                              ? "self-start bg-[#E9E6EF] text-(--ink)"
                              : "self-end bg-white text-(--ink)"
                        }`}
                      >
                        {m.text}
                      </div>
                    ))}
                  </div>
                  {cur.connected && cur.ai && cur.booking ? (
                    <div className="rounded-xl bg-[#E3E9FB] px-3.5 py-3 text-[13px] leading-snug text-[#223A8C]">
                      Запис створено в чаті й одразу зʼявився в календарі майстра.
                      <br />
                      <Link href="/beauty/calendar" className="inline-flex min-h-11 items-center font-semibold text-[#223A8C] underline">
                        Переглянути в календарі
                      </Link>
                    </div>
                  ) : null}
                  {cur.connected && cur.inbox ? (
                    <div className="rounded-xl bg-[#FFF1CC] px-3.5 py-3 text-[13px] leading-snug text-[#5C3900]">
                      Нова заявка зʼявиться в розділі «AI-асистент → Заявки».{" "}
                      <Link href="/beauty/ai" className="inline-flex min-h-11 items-center font-semibold text-[#5C3900] underline">
                        Відкрити заявки
                      </Link>
                    </div>
                  ) : null}
                </Card>
                <Card as="div" className="flex flex-col gap-2">
                  <h2 className="text-lg font-semibold">Сповіщення персоналу</h2>
                  <div className="text-[13px] leading-snug text-(--muted)">
                    Адміністратор отримає сповіщення про нове повідомлення, запис через канал або розмову, яку AI передав людині.
                  </div>
                  <div className="flex justify-between gap-3 text-sm"><span className="text-(--muted)">Куди</span><span className="font-semibold">Telegram адміністратора</span></div>
                  <div className="flex justify-between gap-3 text-sm"><span className="text-(--muted)">Коли</span><span className="font-semibold">Завжди, у робочі години</span></div>
                </Card>
              </aside>
            </div>
          </>
        ) : null}
      </QueryState>
    </>
  );
}
