"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { BeautyApiError } from "@/features/beauty-auth/errors";
import { beautyApi } from "../api";
import type { CallTask, CallTaskPatch, CallTaskQuery, ContactPreferencesInput, ReminderSettings } from "../types";

const retry = (count: number, e: unknown) => !(e instanceof BeautyApiError && e.status >= 400 && e.status < 500) && count < 2;

export const reminderKeys = {
  prefs: (clientId: string) => ["beauty", "contact-prefs", clientId] as const,
  tasks: (q: CallTaskQuery) => ["beauty", "call-tasks", q.day, q.status, q.locationId ?? ""] as const,
  settings: ["beauty", "settings", "reminders"] as const,
};

export const useContactPreferences = (clientId: string, enabled = true) =>
  useQuery({ queryKey: reminderKeys.prefs(clientId), queryFn: () => beautyApi.getContactPreferences(clientId), enabled, retry });

/** Зміна вподобання перепланує майбутні записи: оновлюємо календар, профіль і задачі дзвінків. */
export function useSetContactPreferences(clientId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (input: ContactPreferencesInput) => beautyApi.setContactPreferences(clientId, input),
    onSuccess: (saved) => {
      const { replannedAppointments, ...prefs } = saved;
      void replannedAppointments;
      qc.setQueryData(reminderKeys.prefs(clientId), prefs);
      void qc.invalidateQueries({ queryKey: ["beauty", "calendar"] });
      void qc.invalidateQueries({ queryKey: ["beauty", "call-tasks"] });
      void qc.invalidateQueries({ queryKey: ["beauty", "inbox", "detail"] });
    },
  });
}

/** Задачі дзвінків: оновлення раз на 30 с (кілька адміністраторів), пауза на прихованій вкладці. */
export const useCallTasks = (q: CallTaskQuery, enabled = true) =>
  useQuery({
    queryKey: reminderKeys.tasks(q),
    queryFn: () => beautyApi.getCallTasks(q),
    enabled,
    retry,
    refetchInterval: 30_000,
    refetchIntervalInBackground: false,
  });

export function useUpdateCallTask() {
  const qc = useQueryClient();
  return useMutation<CallTask, unknown, { id: string; patch: CallTaskPatch }>({
    mutationFn: ({ id, patch }) => beautyApi.updateCallTask(id, patch),
    onSettled: () => qc.invalidateQueries({ queryKey: ["beauty", "call-tasks"] }),
  });
}

export const useReminderSettings = (enabled = true) =>
  useQuery({ queryKey: reminderKeys.settings, queryFn: () => beautyApi.getReminderSettings(), enabled, retry });

export function useSaveReminderSettings() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (s: ReminderSettings) => beautyApi.saveReminderSettings(s),
    onSuccess: (saved) => {
      qc.setQueryData(reminderKeys.settings, saved);
      // Доступність способів у картках клієнтів залежить від налаштувань.
      void qc.invalidateQueries({ queryKey: ["beauty", "contact-prefs"] });
    },
  });
}
