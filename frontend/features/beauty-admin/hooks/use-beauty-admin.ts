"use client";

import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { beautyApi } from "../api";
import type {
  CancellationSettings,
  ChannelConfig,
  ChannelId,
  ChannelPatch,
  LocationId,
  PromoDraft,
  PromoGoalId,
  AbsenceInput,
  StaffCreateInput,
  StaffUpdateInput,
  WorkingHours,
} from "../types";

export const beautyKeys = {
  locations: ["beauty", "locations"] as const,
  overview: (loc: LocationId | null) => ["beauty", "overview", loc] as const,
  calendar: (id: string) => ["beauty", "calendar", id] as const,
  clients: ["beauty", "clients"] as const,
  client: (id: string) => ["beauty", "client", id] as const,
  staff: ["beauty", "staff"] as const,
  staffProfile: (id: string) => ["beauty", "staff", id] as const,
  services: ["beauty", "services"] as const,
  staffInvites: (id: string) => ["beauty", "staff-invites", id] as const,
  absences: (from: string, to: string, specialistId?: string) => ["beauty", "absences", from, to, specialistId ?? null] as const,
  priceList: ["beauty", "price-list"] as const,
  promoPreview: (d: PromoDraft) => ["beauty", "promo-preview", d] as const,
  network: ["beauty", "analytics", "network"] as const,
  locationAnalytics: (id: LocationId) => ["beauty", "analytics", "location", id] as const,
  promoAnalytics: ["beauty", "analytics", "promotions"] as const,
  channels: ["beauty", "channels"] as const,
  cancellation: ["beauty", "settings", "cancellation"] as const,
  aiRequests: ["beauty", "ai", "requests"] as const,
  aiChat: ["beauty", "ai", "chat"] as const,
  aiPlan: (goal: PromoGoalId) => ["beauty", "ai", "plan", goal] as const,
};

export const useLocations = () =>
  useQuery({ queryKey: beautyKeys.locations, queryFn: () => beautyApi.getLocations() });

export const useOverview = (locationId: LocationId | null) =>
  useQuery({
    queryKey: beautyKeys.overview(locationId),
    queryFn: () => beautyApi.getOverview(locationId),
    placeholderData: keepPreviousData,
  });

export const useCalendarWeek = (specialistId: string) =>
  useQuery({
    queryKey: beautyKeys.calendar(specialistId),
    queryFn: () => beautyApi.getCalendarWeek(specialistId),
    placeholderData: keepPreviousData,
  });

export function useCancelAppointment(specialistId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => beautyApi.cancelAppointment(id),
    onSuccess: () => qc.invalidateQueries({ queryKey: beautyKeys.calendar(specialistId) }),
  });
}

export function useMoveAppointment(specialistId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, startsAt }: { id: string; startsAt: string }) => beautyApi.moveAppointment(id, startsAt),
    onSuccess: () => qc.invalidateQueries({ queryKey: beautyKeys.calendar(specialistId) }),
  });
}

export const useCancellationSettings = () =>
  useQuery({ queryKey: beautyKeys.cancellation, queryFn: () => beautyApi.getCancellationSettings() });

export function useUpdateCancellationSettings() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (s: CancellationSettings) => beautyApi.updateCancellationSettings(s),
    onSuccess: (saved) => qc.setQueryData(beautyKeys.cancellation, saved),
  });
}

export const useClients =() =>
  useQuery({ queryKey: beautyKeys.clients, queryFn: () => beautyApi.getClients() });

export const useClient = (id: string) =>
  useQuery({ queryKey: beautyKeys.client(id), queryFn: () => beautyApi.getClient(id) });

export function useAddClientNote(id: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (text: string) => beautyApi.addClientNote(id, text),
    onSuccess: () => qc.invalidateQueries({ queryKey: beautyKeys.client(id) }),
  });
}

export const useStaff = (enabled = true) =>
  useQuery({ queryKey: beautyKeys.staff, queryFn: () => beautyApi.getStaff(), enabled });

export const useStaffProfile = (id: string) =>
  useQuery({ queryKey: beautyKeys.staffProfile(id), queryFn: () => beautyApi.getStaffProfile(id) });

export const useServices = () =>
  useQuery({ queryKey: beautyKeys.services, queryFn: () => beautyApi.getServices() });

/** Після зміни працівника оновлюємо і список, і профіль. */
function useInvalidateStaff() {
  const qc = useQueryClient();
  return () => qc.invalidateQueries({ queryKey: beautyKeys.staff });
}

export function useCreateStaff() {
  const invalidate = useInvalidateStaff();
  return useMutation({ mutationFn: (input: StaffCreateInput) => beautyApi.createStaff(input), onSuccess: invalidate });
}

export function useUpdateStaff(id: string) {
  const invalidate = useInvalidateStaff();
  return useMutation({ mutationFn: (input: StaffUpdateInput) => beautyApi.updateStaff(id, input), onSuccess: invalidate });
}

export function useSetStaffServices(id: string) {
  const invalidate = useInvalidateStaff();
  return useMutation({ mutationFn: (serviceIds: string[]) => beautyApi.setStaffServices(id, serviceIds), onSuccess: invalidate });
}

export function useSetStaffSchedule(id: string) {
  const invalidate = useInvalidateStaff();
  return useMutation({
    mutationFn: ({ locationId, workingHours }: { locationId: string; workingHours: WorkingHours }) =>
      beautyApi.setStaffSchedule(id, locationId, workingHours),
    onSuccess: invalidate,
  });
}

/** Токен запрошення живе лише в результаті мутації (не в кеші запитів). */
export function useInviteStaff() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, email }: { id: string; email: string }) => beautyApi.inviteStaff(id, email),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["beauty", "staff-invites"] }),
  });
}

export const useStaffInvites = (staffId: string) =>
  useQuery({ queryKey: beautyKeys.staffInvites(staffId), queryFn: () => beautyApi.getStaffInvites(staffId) });

export function useRevokeInvite(staffId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (inviteId: string) => beautyApi.revokeInvite(inviteId),
    onSuccess: () => qc.invalidateQueries({ queryKey: beautyKeys.staffInvites(staffId) }),
  });
}

export const useAbsences = (q: { from: string; to: string; specialistId?: string }, enabled = true) =>
  useQuery({
    queryKey: beautyKeys.absences(q.from, q.to, q.specialistId),
    queryFn: () => beautyApi.getAbsences(q),
    enabled,
  });

function useInvalidateAbsences() {
  const qc = useQueryClient();
  return () => qc.invalidateQueries({ queryKey: ["beauty", "absences"] });
}

export function useCreateAbsence(specialistId: string) {
  const invalidate = useInvalidateAbsences();
  return useMutation({ mutationFn: (input: AbsenceInput) => beautyApi.createAbsence(specialistId, input), onSuccess: invalidate });
}

export function useApproveAbsence() {
  const invalidate = useInvalidateAbsences();
  return useMutation({ mutationFn: (id: string) => beautyApi.approveAbsence(id), onSuccess: invalidate });
}

export function useRejectAbsence() {
  const invalidate = useInvalidateAbsences();
  return useMutation({ mutationFn: (id: string) => beautyApi.rejectAbsence(id), onSuccess: invalidate });
}

export function useCancelAbsence() {
  const invalidate = useInvalidateAbsences();
  return useMutation({ mutationFn: (id: string) => beautyApi.cancelAbsence(id), onSuccess: invalidate });
}

export const usePriceList = () =>
  useQuery({ queryKey: beautyKeys.priceList, queryFn: () => beautyApi.getPriceList() });

export const usePromoPreview = (draft: PromoDraft) =>
  useQuery({
    queryKey: beautyKeys.promoPreview(draft),
    queryFn: () => beautyApi.previewPromotion(draft),
    placeholderData: keepPreviousData,
  });

export const useCreatePromotion = () =>
  useMutation({ mutationFn: (draft: PromoDraft) => beautyApi.createPromotion(draft) });

export const useNetworkAnalytics = () =>
  useQuery({ queryKey: beautyKeys.network, queryFn: () => beautyApi.getNetworkAnalytics() });

export const useLocationAnalytics = (id: LocationId) =>
  useQuery({
    queryKey: beautyKeys.locationAnalytics(id),
    queryFn: () => beautyApi.getLocationAnalytics(id),
    placeholderData: keepPreviousData,
  });

export const usePromotionAnalytics = () =>
  useQuery({ queryKey: beautyKeys.promoAnalytics, queryFn: () => beautyApi.getPromotionAnalytics() });

export const useChannels = () =>
  useQuery({ queryKey: beautyKeys.channels, queryFn: () => beautyApi.getChannels() });

function replaceChannel(list: ChannelConfig[] | undefined, next: ChannelConfig) {
  return list?.map((c) => (c.id === next.id ? next : c));
}

/** Зміна налаштувань каналу з optimistic-оновленням перемикачів. */
export function useUpdateChannel() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, patch }: { id: ChannelId; patch: ChannelPatch }) =>
      beautyApi.updateChannel(id, patch),
    onMutate: async ({ id, patch }) => {
      await qc.cancelQueries({ queryKey: beautyKeys.channels });
      const prev = qc.getQueryData<ChannelConfig[]>(beautyKeys.channels);
      qc.setQueryData<ChannelConfig[]>(beautyKeys.channels, (list) =>
        list?.map((c) => {
          if (c.id !== id) return c;
          const { handoff, ...rest } = patch;
          return { ...c, ...rest, handoff: { ...c.handoff, ...handoff } };
        }),
      );
      return { prev };
    },
    onError: (_e, _v, ctx) => {
      if (ctx?.prev) qc.setQueryData(beautyKeys.channels, ctx.prev);
    },
    onSettled: () => qc.invalidateQueries({ queryKey: beautyKeys.channels }),
  });
}

export function useConnectChannel() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (input: { id: ChannelId; secret: string }) => beautyApi.connectChannel(input),
    onSuccess: (next) => qc.setQueryData<ChannelConfig[]>(beautyKeys.channels, (l) => replaceChannel(l, next)),
  });
}

export function useDisconnectChannel() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: ChannelId) => beautyApi.disconnectChannel(id),
    onSuccess: (next) => qc.setQueryData<ChannelConfig[]>(beautyKeys.channels, (l) => replaceChannel(l, next)),
  });
}

export const useAiRequests = () =>
  useQuery({ queryKey: beautyKeys.aiRequests, queryFn: () => beautyApi.getAiRequests() });

export function useApproveAiRequest() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => beautyApi.approveAiRequest(id),
    onSuccess: () => qc.invalidateQueries({ queryKey: beautyKeys.aiRequests }),
  });
}

export const useAiChat = () =>
  useQuery({ queryKey: beautyKeys.aiChat, queryFn: () => beautyApi.getAiChat() });

export function useSendAiChatMessage() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (text: string) => beautyApi.sendAiChatMessage(text),
    onSuccess: () => qc.invalidateQueries({ queryKey: beautyKeys.aiChat }),
  });
}

export const useAiPromoPlan = (goal: PromoGoalId) =>
  useQuery({
    queryKey: beautyKeys.aiPlan(goal),
    queryFn: () => beautyApi.getAiPromoPlan(goal),
    placeholderData: keepPreviousData,
  });

export const useLaunchAiCampaign = () =>
  useMutation({
    mutationFn: (input: { goalId: PromoGoalId; segmentIds: string[]; text: string }) =>
      beautyApi.launchAiCampaign(input),
  });
