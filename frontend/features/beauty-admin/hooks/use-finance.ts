"use client";

import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { BeautyApiError } from "@/features/beauty-auth/errors";
import { beautyApi } from "../api";
import { detailsKey, detailsParams, type DetailsTarget } from "../analytics/details";
import type { LocationId, PhonePendingAction, ServiceCostInput } from "../types";

/** 4xx (403/404/422/429) не минуть самі: не повторюємо. */
const retry = (count: number, e: unknown) => !(e instanceof BeautyApiError && e.status >= 400 && e.status < 500) && count < 2;

export const financeKeys = {
  costs: (includeInactive: boolean) => ["beauty", "service-costs", includeInactive] as const,
  costDetail: (serviceId: string) => ["beauty", "service-cost", serviceId] as const,
};

/**
 * Деталі періоду (§24.8.1): ключ `['analytics-details', from, to, locationId]`; сторінки додаються за `nextCursor`
 * («Показати ще»). Помилки (зокрема 429) не повторюються автоматично.
 */
export const useNetworkDetails = (target: DetailsTarget, locationId: LocationId | null, enabled = true) =>
  useInfiniteQuery({
    queryKey: detailsKey(target, locationId),
    queryFn: ({ pageParam }) => beautyApi.getNetworkDetails({ ...detailsParams(target, locationId), ...(pageParam ? { cursor: pageParam } : {}) }),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (last) => (last.hasMore ? last.nextCursor : undefined),
    enabled,
    retry: false,
  });

export const useServiceCosts = (enabled = true) =>
  useQuery({ queryKey: financeKeys.costs(false), queryFn: () => beautyApi.getServiceCosts(false), enabled, retry });

export const useServiceCostDetail = (serviceId: string, enabled = true) =>
  useQuery({ queryKey: financeKeys.costDetail(serviceId), queryFn: () => beautyApi.getServiceCostDetail(serviceId), enabled: enabled && serviceId !== "", retry });

/** Зміна собівартості не змінює минулі записи, але оновлює список і редактор. */
function useInvalidateCosts() {
  const qc = useQueryClient();
  return () => {
    void qc.invalidateQueries({ queryKey: ["beauty", "service-costs"] });
    void qc.invalidateQueries({ queryKey: ["beauty", "service-cost"] });
  };
}

export function useSetServiceCost(serviceId: string) {
  const invalidate = useInvalidateCosts();
  return useMutation({ mutationFn: (input: ServiceCostInput) => beautyApi.setServiceCost(serviceId, input), onSuccess: invalidate });
}

export function useDeleteServiceCost(serviceId: string) {
  const invalidate = useInvalidateCosts();
  return useMutation({ mutationFn: (locationId: LocationId | null) => beautyApi.deleteServiceCost(serviceId, locationId), onSuccess: invalidate });
}

/** Рішення щодо нового номера: оновлюємо картку клієнта, задачі дзвінків і розмови (бейдж «Надано інший номер»). */
export function useResolvePhonePending(clientId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (action: PhonePendingAction) => beautyApi.resolvePhonePending(clientId, action),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ["beauty", "client", clientId] });
      void qc.invalidateQueries({ queryKey: ["beauty", "contact-prefs", clientId] });
      void qc.invalidateQueries({ queryKey: ["beauty", "call-tasks"] });
      void qc.invalidateQueries({ queryKey: ["beauty", "inbox"] });
    },
  });
}
