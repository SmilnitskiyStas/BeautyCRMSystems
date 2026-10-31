"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { BeautyApiError } from "@/features/beauty-auth/errors";
import { beautyApi } from "../api";
import type { ConversationFilter, LocationId } from "../types";

/** Автооновлення inbox опитуванням (§22.11, ADR-012 §2.6): 10 с; на прихованій вкладці TanStack пропускає тики. SSE немає. */
export const INBOX_POLL_MS = 10_000;
/** Бейдж меню не потребує такої частоти. */
export const INBOX_BADGE_POLL_MS = 30_000;

export const inboxKeys = {
  all: ["beauty", "inbox"] as const,
  list: (filter: ConversationFilter, locationId: LocationId) => ["beauty", "inbox", "list", filter, locationId] as const,
  detail: (id: string) => ["beauty", "inbox", "detail", id] as const,
  badge: ["beauty", "inbox", "badge"] as const,
};

/** 4xx не повторюємо (403/404 не минуть самі). */
const retry = (count: number, e: unknown) => !(e instanceof BeautyApiError && e.status >= 400 && e.status < 500) && count < 2;

export const useConversations = (filter: ConversationFilter, locationId: LocationId, enabled = true) =>
  useQuery({
    queryKey: inboxKeys.list(filter, locationId),
    queryFn: () => beautyApi.getConversations({ filter, locationId: locationId || undefined, take: 50 }),
    enabled,
    retry,
    refetchInterval: INBOX_POLL_MS,
    refetchIntervalInBackground: false,
  });

export const useConversation = (id: string | null) =>
  useQuery({
    queryKey: inboxKeys.detail(id ?? ""),
    queryFn: () => beautyApi.getConversation(id as string),
    enabled: !!id,
    retry,
    refetchInterval: INBOX_POLL_MS,
    refetchIntervalInBackground: false,
  });

/** Кількість розмов, що чекають відповіді людини (бейдж меню «Розмови»). */
export const useNeedsReplyCount = (enabled: boolean) =>
  useQuery({
    queryKey: inboxKeys.badge,
    queryFn: () => beautyApi.getConversations({ filter: "needs_reply", take: 1 }),
    select: (r) => r.meta.needsReplyCount,
    enabled,
    retry,
    refetchInterval: INBOX_BADGE_POLL_MS,
    refetchIntervalInBackground: false,
  });

function useRefreshInbox() {
  const qc = useQueryClient();
  return () => qc.invalidateQueries({ queryKey: inboxKeys.all });
}

/** Ключ ідемпотентності передає виклик: повтор того самого ключа після мережевої помилки не дублює повідомлення. */
export function useReplyToConversation(id: string) {
  const refresh = useRefreshInbox();
  return useMutation({
    mutationFn: ({ text, key }: { text: string; key: string }) => beautyApi.replyToConversation(id, text, key),
    onSuccess: refresh,
  });
}

export function useResumeAi(id: string) {
  const refresh = useRefreshInbox();
  return useMutation({ mutationFn: () => beautyApi.resumeConversationAi(id), onSuccess: refresh });
}

export function useMarkConversationRead(id: string) {
  const refresh = useRefreshInbox();
  return useMutation({ mutationFn: (upToMessageId?: string) => beautyApi.markConversationRead(id, upToMessageId), onSuccess: refresh });
}
