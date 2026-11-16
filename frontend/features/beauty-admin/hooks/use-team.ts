"use client";

import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { BeautyApiError } from "@/features/beauty-auth/errors";
import { beautyApi } from "../api";
import type { AccessSaveInput, TeamInviteInput } from "../team/types";

/** 4xx (403/404/409/422) не минуть самі: не повторюємо. */
const retry = (count: number, e: unknown) => !(e instanceof BeautyApiError && e.status >= 400 && e.status < 500) && count < 2;

export const teamKeys = {
  all: ["beauty", "team"] as const,
  users: ["beauty", "team", "users"] as const,
  invites: ["beauty", "team", "invites"] as const,
  access: (id: string) => ["beauty", "team", "access", id] as const,
  audit: (userId: string | null) => ["beauty", "team", "audit", userId] as const,
  myRevenue: (from: string, to: string) => ["beauty", "me-revenue", from, to] as const,
};

export const useTeamUsers = (enabled = true) =>
  useQuery({ queryKey: teamKeys.users, queryFn: () => beautyApi.getTeamUsers(), enabled, retry });

export const useTeamInvites = (enabled = true) =>
  useQuery({ queryKey: teamKeys.invites, queryFn: () => beautyApi.getTeamInvites(), enabled, retry });

/** Без кешування між відкриттями: права мають бути актуальними (див. `expectedVersion`). */
export const useUserAccess = (id: string | null) =>
  useQuery({ queryKey: teamKeys.access(id ?? ""), queryFn: () => beautyApi.getUserAccess(id!), enabled: !!id, retry, gcTime: 0 });

/** Перегляд diff (`dryRun`) нічого не змінює й не інвалідує кеш. */
export const useDryRunAccess = (id: string) =>
  useMutation({ mutationFn: (input: AccessSaveInput) => beautyApi.saveUserAccess(id, { ...input, dryRun: true }) });

export function useSaveAccess(id: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (input: AccessSaveInput) => beautyApi.saveUserAccess(id, { ...input, dryRun: false }),
    onSuccess: () => qc.invalidateQueries({ queryKey: teamKeys.all }),
  });
}

export const useDryRunReset = (id: string) =>
  useMutation({ mutationFn: (expectedVersion: number) => beautyApi.resetUserAccess(id, { expectedVersion, dryRun: true }) });

export function useResetAccess(id: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (expectedVersion: number) => beautyApi.resetUserAccess(id, { expectedVersion, dryRun: false }),
    onSuccess: () => qc.invalidateQueries({ queryKey: teamKeys.all }),
  });
}

export function useSetUserActive(id: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (v: { isActive: boolean; expectedVersion?: number }) => beautyApi.setUserActive(id, v.isActive, v.expectedVersion),
    onSuccess: () => qc.invalidateQueries({ queryKey: teamKeys.all }),
  });
}

export function useCreateTeamInvite() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (input: TeamInviteInput) => beautyApi.createTeamInvite(input),
    onSuccess: () => qc.invalidateQueries({ queryKey: teamKeys.invites }),
    // Токен одноразовий: не тримаємо його в кеші мутацій.
    gcTime: 0,
  });
}

export function useRevokeTeamInvite() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (inviteId: string) => beautyApi.revokeInvite(inviteId),
    onSuccess: () => qc.invalidateQueries({ queryKey: teamKeys.invites }),
  });
}

/** Журнал змін (лише директор): сторінки за `nextCursor`. */
export const useAccessAudit = (userId: string | null, enabled = true) =>
  useInfiniteQuery({
    queryKey: teamKeys.audit(userId),
    queryFn: ({ pageParam }) => beautyApi.getAccessAudit({ ...(userId ? { userId } : {}), ...(pageParam ? { cursor: pageParam } : {}) }),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (last) => last.nextCursor ?? undefined,
    enabled,
    retry: false,
  });

export const useMyRevenue = (from: string, to: string, enabled = true) =>
  useQuery({ queryKey: teamKeys.myRevenue(from, to), queryFn: () => beautyApi.getMyRevenue({ from, to }), enabled, retry });
