"use client";

import { useId, useState } from "react";
import {
  useAiChat,
  useAiPromoPlan,
  useAiRequests,
  useApproveAiRequest,
  useLaunchAiCampaign,
  useSendAiChatMessage,
} from "../hooks/use-beauty-admin";
import { money } from "../format";
import type { AiPromoPlan, PromoGoalId } from "../types";
import { Badge, Card, PageHeader, PrimaryButton, QueryState, SecondaryButton, Table, TD, Tabs, TextLink } from "./ui";

type Tab = "req" | "chat" | "promo";

function RequestsTab() {
  const q = useAiRequests();
  const approve = useApproveAiRequest();
  const [selectedId, setSelectedId] = useState("r1");
  const list = q.data ?? [];
  const cur = list.find((r) => r.id === selectedId) ?? list[0];

  return (
    <QueryState isPending={q.isPending} isError={q.isError} onRetry={() => q.refetch()}>
      {cur ? (
        <div className="flex flex-wrap items-start gap-6">
          <Card className="flex flex-[1_1_320px] flex-col gap-2.5">
            <h2 className="text-lg font-semibold">Вхідні заявки</h2>
            {list.map((r) => (
              <button
                key={r.id}
                type="button"
                aria-pressed={r.id === cur.id}
                onClick={() => setSelectedId(r.id)}
                className={`flex min-h-11 cursor-pointer flex-col gap-1 rounded-xl border-2 bg-white p-3 text-left text-(--ink) ${r.id === cur.id ? "border-(--accent)" : "border-(--line-strong)"}`}
              >
                <span className="flex justify-between gap-2 text-sm">
                  <span className="font-semibold">{r.name} · {r.channel}</span>
                  <span className="text-(--muted)">{r.time}</span>
                </span>
                <span className="text-[13px] text-(--muted)">{r.text}</span>
                <Badge tone={r.replied ? "ok" : "now"} className="self-start !px-2 !py-0.5 !text-xs">
                  {r.replied ? "Відповідь надіслано" : "Нова"}
                </Badge>
              </button>
            ))}
          </Card>
          <Card className="flex flex-[999_1_440px] flex-col gap-3.5">
            <div className="flex flex-wrap items-center justify-between gap-3">
              <h2 className="text-lg font-semibold">{cur.name} · {cur.channel}</h2>
              <Badge tone="vip">{cur.intent}</Badge>
            </div>
            <div className="rounded-xl bg-(--page) p-3.5 text-sm">{cur.text}</div>
            <div className="text-[13px] font-semibold text-(--muted)">Чернетка відповіді від AI</div>
            <div className="rounded-xl border-2 border-(--line-strong) p-3.5 text-[15px] leading-snug">{cur.reply}</div>
            <div className="flex flex-wrap items-center gap-2">
              <PrimaryButton disabled={cur.replied || approve.isPending} onClick={() => approve.mutate(cur.id)}>
                {cur.replied ? "Надіслано" : approve.isPending ? "Надсилаємо…" : "Надіслати відповідь"}
              </PrimaryButton>
              <SecondaryButton>Редагувати</SecondaryButton>
              <TextLink href="/beauty/calendar">Відкрити календар майстра</TextLink>
              <TextLink href="/beauty/channels">Налаштувати канали</TextLink>
            </div>
            {approve.isError ? <p role="alert" className="text-sm text-[#8A1F1F]">Не вдалося надіслати відповідь.</p> : null}
          </Card>
        </div>
      ) : null}
    </QueryState>
  );
}

function ChatTab() {
  const q = useAiChat();
  const send = useSendAiChatMessage();
  return (
    <QueryState isPending={q.isPending} isError={q.isError} onRetry={() => q.refetch()}>
      {q.data ? (
        <div className="flex flex-wrap items-start gap-6">
          <Card className="flex flex-[999_1_440px] flex-col gap-3">
            <h2 className="text-lg font-semibold">{q.data.clientName} · {q.data.channel}</h2>
            <div role="log" aria-label="Розмова" className="flex flex-col gap-2 rounded-xl bg-(--page) p-3.5">
              {q.data.messages.map((m, i) => (
                <div
                  key={i}
                  className={`max-w-[80%] rounded-[14px] px-3.5 py-2.5 text-sm leading-snug ${m.from === "staff" ? "self-end bg-(--accent) text-white" : "self-start bg-white text-(--ink)"}`}
                >
                  {m.text}
                </div>
              ))}
            </div>
            <div className="text-[13px] font-semibold text-(--muted)">Підказки AI: натисніть, щоб надіслати</div>
            <div className="flex flex-col gap-2">
              {q.data.suggestions.map((s) => (
                <button
                  key={s}
                  type="button"
                  disabled={send.isPending}
                  onClick={() => send.mutate(s)}
                  className="min-h-11 cursor-pointer rounded-xl border-2 border-(--line-strong) bg-white px-3.5 py-2.5 text-left text-sm text-(--ink) disabled:opacity-60"
                >
                  {s}
                </button>
              ))}
            </div>
          </Card>
          <aside className="flex min-w-0 flex-[1_1_300px] flex-col gap-2.5 rounded-2xl bg-white p-5">
            <h2 className="text-lg font-semibold">Контекст для відповіді</h2>
            {q.data.context.map((c) => (
              <div key={c.label} className="flex justify-between gap-3 text-sm">
                <span className="text-(--muted)">{c.label}</span>
                <span className="font-semibold">{c.value}</span>
              </div>
            ))}
            {q.data.warning ? <div role="note" className="rounded-xl bg-[#FFF1CC] p-3 text-[13px] text-[#5C3900]">{q.data.warning}</div> : null}
            <TextLink href="/beauty/clients/oc">Відкрити профіль клієнта</TextLink>
          </aside>
        </div>
      ) : null}
    </QueryState>
  );
}

function PromoPlanView({ plan }: { plan: AiPromoPlan }) {
  const [text, setText] = useState<string | null>(null);
  const [segs, setSegs] = useState<string[] | null>(null);
  const launch = useLaunchAiCampaign();
  const textId = useId();
  const selected = segs ?? plan.recommendedSegmentIds;
  const reach = plan.segments.filter((s) => selected.includes(s.id)).reduce((a, s) => a + s.count, 0);
  const body = text ?? plan.text;

  return (
    <div className="flex flex-wrap items-start gap-6">
      <section className="flex min-w-0 flex-[999_1_480px] flex-col gap-5">
        <Card as="div" className="flex flex-col gap-2.5">
          <h2 className="text-lg font-semibold">Рекомендована ціна: −{plan.percent}%</h2>
          <Table headers={["Послуга", "Зараз", "З акцією"]} minWidth={420}>
            {plan.rows.map((r) => (
              <tr key={r.name} className="border-t border-(--line)">
                <td className={`${TD} font-semibold`}>{r.name}</td>
                <td className={`${TD} text-(--muted)`}>{money(r.old)}</td>
                <td className={`${TD} font-semibold text-(--promo)`}>{money(r.price)}</td>
              </tr>
            ))}
          </Table>
          <div className="text-[13px] text-(--muted)">Діє: {plan.when}</div>
        </Card>
        <Card as="div" className="flex flex-col gap-2.5">
          <label htmlFor={textId} className="text-lg font-semibold">Текст оголошення</label>
          <textarea
            id={textId}
            rows={5}
            value={body}
            onChange={(e) => setText(e.target.value)}
            className="w-full resize-y rounded-xl border-2 border-(--line-strong) px-3.5 py-3 text-[15px] leading-snug"
          />
        </Card>
      </section>

      <aside className="flex min-w-0 flex-[1_1_340px] flex-col gap-5">
        <Card as="div" className="flex flex-col gap-2.5">
          <h2 className="text-lg font-semibold">Кому надіслати</h2>
          <div className="text-[13px] text-(--muted)">AI підібрав групи клієнтів під мету акції. Групи можна змінити.</div>
          {plan.segments.map((s) => {
            const on = selected.includes(s.id);
            return (
              <button
                key={s.id}
                type="button"
                aria-pressed={on}
                onClick={() => setSegs(on ? selected.filter((x) => x !== s.id) : [...selected, s.id])}
                className={`flex min-h-14 cursor-pointer items-center justify-between gap-3 rounded-[14px] border-2 px-3.5 py-2.5 text-left text-(--ink) ${on ? "border-(--accent) bg-(--tint)" : "border-(--line-strong) bg-white"}`}
              >
                <span className="flex flex-col gap-0.5">
                  <span className="text-[15px] font-semibold">{s.label}</span>
                  <span className="text-[13px] text-(--muted)">{s.note}</span>
                </span>
                <span className="text-base font-semibold">{s.count}</span>
              </button>
            );
          })}
          <div className="flex justify-between border-t border-(--line) pt-2.5 text-[15px] font-semibold">
            <span>Охоплення</span>
            <span aria-live="polite">{reach} клієнтів</span>
          </div>
        </Card>
        <Card as="div" className="flex flex-col gap-2.5">
          <h2 className="text-lg font-semibold">Запуск</h2>
          <button
            type="button"
            disabled={launch.isPending || launch.isSuccess || reach === 0}
            onClick={() => launch.mutate({ goalId: plan.goalId, segmentIds: selected, text: body })}
            className="min-h-[52px] cursor-pointer rounded-[26px] border-0 bg-(--accent) text-base font-semibold text-white disabled:cursor-not-allowed disabled:opacity-60"
          >
            {launch.isSuccess ? "Розсилку заплановано" : launch.isPending ? "Плануємо…" : `Надіслати ${reach} клієнтам`}
          </button>
          {launch.isError ? <p role="alert" className="text-sm text-[#8A1F1F]">Не вдалося запланувати розсилку.</p> : null}
          <TextLink href="/beauty/promos">Налаштувати в розділі «Ціни та акції»</TextLink>
          <div className="text-[13px] text-(--muted)">AI лише пропонує. Ціни, текст і список клієнтів ви підтверджуєте самі.</div>
        </Card>
      </aside>
    </div>
  );
}

function PromoTab() {
  const [goal, setGoal] = useState<PromoGoalId>("fill");
  const q = useAiPromoPlan(goal);
  const goalId = useId();
  return (
    <QueryState isPending={q.isPending} isError={q.isError} onRetry={() => q.refetch()}>
      {q.data ? (
        <div className="flex flex-col gap-5">
          <Card as="div" className="flex flex-col gap-2.5">
            <label htmlFor={goalId} className="text-lg font-semibold">Мета акції</label>
            <select
              id={goalId}
              value={goal}
              onChange={(e) => setGoal(e.target.value as PromoGoalId)}
              className="min-h-[52px] w-full rounded-[14px] border-2 border-(--line-strong) bg-white px-3.5 text-[15px] font-semibold"
            >
              {q.data.goals.map((g) => (
                <option key={g.id} value={g.id}>{g.label}</option>
              ))}
            </select>
            <div className="text-sm text-(--muted)">{q.data.reason}</div>
          </Card>
          {/* key скидає ручні правки тексту й груп при зміні мети */}
          <PromoPlanView key={q.data.goalId} plan={q.data} />
        </div>
      ) : null}
    </QueryState>
  );
}

export function AiScreen() {
  const [tab, setTab] = useState<Tab>("req");
  return (
    <>
      <PageHeader
        title="AI-асистент"
        subtitle="Заявки, спілкування з клієнтами та допомога з акціями"
        actions={
          <Tabs
            value={tab}
            onChange={setTab}
            tabs={[
              { id: "req", label: "Заявки" },
              { id: "chat", label: "Спілкування з клієнтами" },
              { id: "promo", label: "Допомога з акцією" },
            ]}
          />
        }
      />
      {tab === "req" ? <RequestsTab /> : null}
      {tab === "chat" ? <ChatTab /> : null}
      {tab === "promo" ? <PromoTab /> : null}
    </>
  );
}
