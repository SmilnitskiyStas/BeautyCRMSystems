"use client";

import { useState } from "react";
import { useAuth } from "@/features/beauty-auth/components/auth-provider";
import { canUseInbox } from "@/features/beauty-auth/permissions";
import { aiStateView, isCallbackPreview, previewText, SENDER_LABEL, stamp } from "../inbox-logic";
import { useConversations } from "../hooks/use-inbox";
import { useLocations } from "../hooks/use-beauty-admin";
import type { ConversationFilter, ConversationSummary } from "../types";
import { ConversationThread } from "./inbox-thread";
import { Badge, Card, Chip, EmptyState, FIELD_INPUT, FIELD_LABEL, PageHeader, QueryState } from "./ui";

function ConversationRow({ c, selected, timezone, onOpen }: { c: ConversationSummary; selected: boolean; timezone?: string; onOpen: () => void }) {
  const state = aiStateView(c, timezone);
  return (
    <li>
      <button
        type="button"
        onClick={onOpen}
        aria-current={selected ? "true" : undefined}
        className={`flex min-h-11 w-full cursor-pointer flex-col gap-1 border-0 border-t border-(--line) px-3 py-3 text-left first:border-t-0 ${selected ? "bg-(--tint)" : "bg-white hover:bg-(--page)"}`}
      >
        <span className="flex items-start justify-between gap-2">
          <span className="min-w-0 text-[15px] font-semibold break-words">{c.title}</span>
          {c.unreadCount > 0 ? (
            <span className="flex-none rounded-full bg-(--accent) px-2 text-[13px] font-bold text-white">
              <span aria-hidden="true">{c.unreadCount}</span>
              <span className="sr-only">непрочитаних: {c.unreadCount}</span>
            </span>
          ) : null}
        </span>
        <span className="text-xs text-(--muted)">
          {c.channel.name}
          {c.location ? ` · ${c.location.name}` : ""}
        </span>
        <span className="line-clamp-2 text-sm break-words">
          {isCallbackPreview(c.lastMessagePreview) ? null : <span className="text-(--muted)">{SENDER_LABEL[c.lastMessageSenderType]}: </span>}
          {previewText(c.lastMessagePreview)}
        </span>
        <span className="flex flex-wrap items-center gap-x-2 gap-y-1 text-xs text-(--muted)">
          <time dateTime={c.lastMessageAt}>{stamp(c.lastMessageAt, timezone)}</time>
          <Badge tone={state.tone} className="!px-2 !py-0.5 !text-xs">
            {state.label}
          </Badge>
        </span>
      </button>
    </li>
  );
}

function InboxView() {
  const [filter, setFilter] = useState<ConversationFilter>("needs_reply");
  const [locationId, setLocationId] = useState("");
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const locations = useLocations();
  const list = useConversations(filter, locationId);
  const needs = list.data?.meta.needsReplyCount;
  const zoneOf = (c: ConversationSummary) => locations.data?.find((l) => l.id === c.location?.id)?.timezone;

  return (
    <>
      <PageHeader
        title="Розмови"
        subtitle="Повідомлення клієнтів з каналів. Список оновлюється сам кожні 10 секунд."
      />
      <div className="grid grid-cols-1 items-start gap-6 lg:grid-cols-[minmax(300px,380px)_minmax(0,1fr)]">
        <div className={`flex min-w-0 flex-col gap-3 ${selectedId ? "hidden lg:flex" : ""}`}>
          <div className="flex flex-wrap items-center gap-2" role="group" aria-label="Фільтр розмов">
            <Chip pressed={filter === "needs_reply"} onClick={() => setFilter("needs_reply")}>
              Потрібна відповідь{needs !== undefined ? ` (${needs})` : ""}
            </Chip>
            <Chip pressed={filter === "all"} onClick={() => setFilter("all")}>
              Усі
            </Chip>
          </div>
          <div className="flex flex-col gap-1.5">
            <label htmlFor="inbox-location" className={FIELD_LABEL}>
              Заклад
            </label>
            <select id="inbox-location" value={locationId} onChange={(e) => setLocationId(e.target.value)} className={FIELD_INPUT}>
              <option value="">Усі заклади</option>
              {locations.data?.map((l) => (
                <option key={l.id} value={l.id}>
                  {l.name}
                </option>
              ))}
            </select>
          </div>
          <QueryState isPending={list.isPending} isError={list.isError} onRetry={() => list.refetch()}>
            <Card as="div" className="overflow-hidden !p-0">
              {list.data && list.data.items.length > 0 ? (
                <ul aria-label="Список розмов" className="m-0 list-none p-0">
                  {list.data.items.map((c) => (
                    <ConversationRow key={c.id} c={c} selected={c.id === selectedId} timezone={zoneOf(c)} onOpen={() => setSelectedId(c.id)} />
                  ))}
                </ul>
              ) : (
                <div className="p-5">
                  <EmptyState>
                    {filter === "needs_reply"
                      ? "Немає розмов, що чекають на відповідь. Коли ШІ передасть розмову адміністратору, вона з'явиться тут."
                      : "Розмов поки немає."}
                  </EmptyState>
                </div>
              )}
            </Card>
          </QueryState>
        </div>

        <div className={`min-w-0 ${selectedId ? "" : "hidden lg:block"}`}>
          {selectedId ? (
            <ConversationThread key={selectedId} id={selectedId} onBack={() => setSelectedId(null)} />
          ) : (
            <Card className="text-sm text-(--muted)">Оберіть розмову зі списку, щоб побачити повідомлення й відповісти клієнту.</Card>
          )}
        </div>
      </div>
    </>
  );
}

export function InboxScreen() {
  const { user } = useAuth();
  if (!canUseInbox(user.role)) {
    return (
      <>
        <PageHeader title="Розмови" />
        <Card>
          <p role="note" className="text-sm">
            Розмови клієнтів доступні лише власнику й адміністратору.
          </p>
        </Card>
      </>
    );
  }
  return <InboxView />;
}
