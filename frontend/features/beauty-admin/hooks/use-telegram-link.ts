"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { BeautyApiError } from "@/features/beauty-auth/errors";
import { beautyApi } from "../api";
import { POLL_MS, shouldPoll, type TelegramLinkCreateRequest } from "../inventory/telegram-link";
import { beautyKeys } from "./use-beauty-admin";

const KEY = beautyKeys.inventory.telegramLink;
const retry = (count: number, e: unknown) => !(e instanceof BeautyApiError && e.status >= 400 && e.status < 500) && count < 2;

/**
 * Стан прив'язки Telegram (§32.24.8). Опитування кожні 5 с лише в `pending|awaitingConfirmation`, не довше `expiresAt`;
 * у прихованій вкладці не опитує (`refetchIntervalInBackground: false`). `enabled = false` - без запитів (не owner / немає модуля).
 */
export const useTelegramLink = (enabled = true) =>
  useQuery({
    queryKey: KEY,
    queryFn: () => beautyApi.getTelegramLink(),
    enabled,
    retry,
    refetchInterval: (q) => (shouldPoll(q.state.data, Date.now()) ? POLL_MS : false),
    refetchIntervalInBackground: false,
  });

/**
 * `POST`: відповідь з `deepLink` повертається лише викликачу (`mutate`-колбеки); `gcTime: 0` + `reset()` у компоненті -
 * токен не лишається в кеші мутацій.
 */
export function useCreateTelegramLink() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (r: TelegramLinkCreateRequest) => beautyApi.createTelegramLink(r),
    gcTime: 0,
    onSuccess: () => qc.invalidateQueries({ queryKey: KEY }),
  });
}

export function useDeleteTelegramLink() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () => beautyApi.deleteTelegramLink(),
    onSuccess: () => qc.invalidateQueries({ queryKey: KEY }),
  });
}
