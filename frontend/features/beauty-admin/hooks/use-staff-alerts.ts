"use client";

import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { BeautyApiError } from "@/features/beauty-auth/errors";
import { beautyApi } from "../api";
import type { AlertQuery } from "../inventory/types";
import { beautyKeys } from "./use-beauty-admin";

const K = beautyKeys.inventory;
/** Опитування бейджа - як `InboxBadge` (§22.11): 30 с, не у фоновій вкладці. */
export const ALERTS_BADGE_POLL_MS = 30_000;
const retry = (count: number, e: unknown) => !(e instanceof BeautyApiError && e.status >= 400 && e.status < 500) && count < 2;

/** Кількість непрочитаних сповіщень складу. `enabled` = модуль `beauty_inventory` увімкнено і є `inventory.view`. */
export const useUnreadAlertCount = (enabled: boolean) =>
  useQuery({
    queryKey: K.alertsUnread,
    queryFn: () => beautyApi.getInventoryAlertsUnreadCount(),
    enabled,
    retry,
    refetchInterval: ALERTS_BADGE_POLL_MS,
    refetchIntervalInBackground: false,
  });

export function useStaffAlerts(q: AlertQuery, enabled = true) {
  const query = useInfiniteQuery({
    queryKey: K.alerts(q),
    queryFn: ({ pageParam }) => beautyApi.getInventoryAlerts({ ...q, ...(pageParam ? { cursor: pageParam } : {}) }),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (last) => (last.hasMore ? last.nextCursor : undefined),
    enabled,
    retry,
  });
  return { ...query, items: query.data?.pages.flatMap((p) => p.items) ?? [] };
}

function useInvalidateAlerts() {
  const qc = useQueryClient();
  return () => qc.invalidateQueries({ queryKey: ["beauty", "inventory-alerts"] });
}

export function useMarkAlertRead() {
  const done = useInvalidateAlerts();
  return useMutation({ mutationFn: (id: string) => beautyApi.markInventoryAlertRead(id), onSuccess: done });
}

export function useMarkAllAlertsRead() {
  const done = useInvalidateAlerts();
  return useMutation({ mutationFn: (locationId?: string) => beautyApi.markAllInventoryAlertsRead(locationId), onSuccess: done });
}
