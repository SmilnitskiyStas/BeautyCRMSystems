"use client";

import { keepPreviousData, useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { BeautyApiError } from "@/features/beauty-auth/errors";
import { beautyApi } from "../api";
import { isMarketingDisabled } from "../marketing/logic";
import type {
  AudienceQueryInput,
  CampaignRequest,
  CampaignStatus,
  ConfirmCampaignInput,
  ConsentInput,
  CouponInput,
} from "../types";

/** 4xx не повторюємо (перевірки ролі/модуля/ліміту); мережеві збої - до двох повторів. */
const retry = (count: number, e: unknown) => !(e instanceof BeautyApiError && e.status >= 400 && e.status < 500) && count < 2;

export const marketingKeys = {
  probe: ["beauty", "marketing", "probe"] as const,
  promotions: ["beauty", "marketing", "promotions"] as const,
  coupons: (promotionId = "") => ["beauty", "marketing", "coupons", promotionId] as const,
  clientCoupons: (clientId: string) => ["beauty", "marketing", "client-coupons", clientId] as const,
  audience: (q: AudienceQueryInput) => ["beauty", "marketing", "audience", JSON.stringify(q)] as const,
  campaigns: (status: CampaignStatus | "all") => ["beauty", "marketing", "campaigns", status] as const,
  campaign: (id: string) => ["beauty", "marketing", "campaign", id] as const,
  preview: (id: string, revision: number) => ["beauty", "marketing", "preview", id, revision] as const,
  stats: (id: string) => ["beauty", "marketing", "stats", id] as const,
  summary: (id: string) => ["beauty", "marketing", "summary", id] as const,
  consent: (clientId: string) => ["beauty", "marketing", "consent", clientId] as const,
};

/**
 * Легка перевірка, чи доступний маркетинг (§26.11.1): `marketing_disabled` / `module_disabled` -> функція прихована.
 * Кешується на 5 хв; для specialist не виконується.
 */
export function useMarketingAvailability(enabled = true) {
  const q = useQuery({
    queryKey: marketingKeys.probe,
    queryFn: () => beautyApi.getCampaigns({ take: 1 }),
    enabled,
    retry,
    staleTime: 5 * 60_000,
  });
  return { ...q, disabled: isMarketingDisabled(q.error) };
}

export const usePromotions = (enabled = true) =>
  useQuery({ queryKey: marketingKeys.promotions, queryFn: () => beautyApi.getPromotions(), enabled, retry });

export const useSharedCoupons = (promotionId?: string, enabled = true) =>
  useQuery({ queryKey: marketingKeys.coupons(promotionId), queryFn: () => beautyApi.getCoupons(promotionId ? { promotionId } : undefined), enabled, retry });

export function useCreateCoupon() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (input: CouponInput) => beautyApi.createCoupon(input),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["beauty", "marketing", "coupons"] }),
  });
}

export function useRevokeCoupon() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => beautyApi.revokeCoupon(id),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["beauty", "marketing", "coupons"] }),
  });
}

export const useClientCoupons = (clientId: string, enabled = true) =>
  useQuery({ queryKey: marketingKeys.clientCoupons(clientId), queryFn: () => beautyApi.getClientCoupons(clientId), enabled, retry });

/** Живий лічильник аудиторії (§26.12): без імен і ПД; попередні дані лишаються на екрані під час перерахунку. */
export const useAudiencePreview = (q: AudienceQueryInput, enabled: boolean) =>
  useQuery({
    queryKey: marketingKeys.audience(q),
    queryFn: () => beautyApi.previewAudience(q),
    enabled,
    retry,
    staleTime: 15_000,
    placeholderData: keepPreviousData,
  });

export function useCampaigns(status: CampaignStatus | "all", enabled = true) {
  return useInfiniteQuery({
    queryKey: marketingKeys.campaigns(status),
    queryFn: ({ pageParam }) => beautyApi.getCampaigns({ ...(status !== "all" ? { status } : {}), ...(pageParam ? { cursor: pageParam } : {}) }),
    initialPageParam: "",
    getNextPageParam: (last) => last.nextCursor,
    enabled,
    retry,
  });
}

export const useCampaign = (id: string, enabled = true) =>
  useQuery({
    queryKey: marketingKeys.campaign(id),
    queryFn: () => beautyApi.getCampaign(id),
    enabled,
    retry,
    // Поки йде відправка, оновлюємо раз на 15 с; пауза на прихованій вкладці.
    refetchInterval: (query) => (query.state.data?.status === "sending" ? 15_000 : false),
    refetchIntervalInBackground: false,
  });

export const useCampaignPreview = (id: string | null, revision: number, enabled = true) =>
  useQuery({
    queryKey: marketingKeys.preview(id ?? "", revision),
    queryFn: () => beautyApi.getCampaignPreview(id!),
    enabled: enabled && !!id,
    retry,
    staleTime: 0,
    gcTime: 0,
  });

export const useCampaignStats = (id: string, status: CampaignStatus | undefined, enabled = true) =>
  useQuery({
    queryKey: marketingKeys.stats(id),
    queryFn: () => beautyApi.getCampaignStats(id),
    enabled: enabled && !!status && status !== "draft",
    retry,
    refetchInterval: status === "sending" ? 15_000 : false,
    refetchIntervalInBackground: false,
  });

export const useCampaignCouponSummary = (id: string, enabled: boolean) =>
  useQuery({ queryKey: marketingKeys.summary(id), queryFn: () => beautyApi.getCampaignCouponSummary(id), enabled, retry });

function useInvalidateCampaigns() {
  const qc = useQueryClient();
  return () => qc.invalidateQueries({ queryKey: ["beauty", "marketing"] });
}

export function useSaveCampaign() {
  const invalidate = useInvalidateCampaigns();
  return useMutation({
    mutationFn: ({ id, request }: { id: string | null; request: CampaignRequest }) => (id ? beautyApi.updateCampaign(id, request) : beautyApi.createCampaign(request)),
    onSuccess: () => invalidate(),
  });
}

export function useDeleteCampaign() {
  const invalidate = useInvalidateCampaigns();
  return useMutation({ mutationFn: (id: string) => beautyApi.deleteCampaign(id), onSuccess: () => invalidate() });
}

export function useConfirmCampaign() {
  const invalidate = useInvalidateCampaigns();
  return useMutation({
    mutationFn: ({ id, input, key }: { id: string; input: ConfirmCampaignInput; key: string }) => beautyApi.confirmCampaign(id, input, key),
    onSuccess: () => invalidate(),
  });
}

export function useCancelCampaign() {
  const invalidate = useInvalidateCampaigns();
  return useMutation({ mutationFn: (id: string) => beautyApi.cancelCampaign(id), onSuccess: () => invalidate() });
}

export const useMarketingConsent = (clientId: string, enabled = true) =>
  useQuery({ queryKey: marketingKeys.consent(clientId), queryFn: () => beautyApi.getMarketingConsent(clientId), enabled, retry });

export function useSetMarketingConsent(clientId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (input: ConsentInput) => beautyApi.setMarketingConsent(clientId, input),
    onSuccess: (saved) => {
      qc.setQueryData(marketingKeys.consent(clientId), saved);
      void qc.invalidateQueries({ queryKey: ["beauty", "marketing", "audience"] });
    },
  });
}
